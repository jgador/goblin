import assert from "node:assert/strict";
import { test } from "node:test";
import { ModelCatalogs } from "../../frontend/src/work/model-catalog.js";
import type {
    ModelCatalog,
    ModelQuery,
} from "../../frontend/src/work/model-selection.js";

const query = { limit: 3, selectedModel: "" } as const;
function catalog(overrides: Partial<ModelCatalog> = {}): ModelCatalog {
    return {
        models: [],
        hasMore: false,
        fetchedAt: new Date(Date.now()).toISOString(),
        stale: false,
        refreshing: false,
        unavailable: false,
        ...overrides,
    };
}
function deferred<T>() {
    let resolve!: (value: T) => void;
    let reject!: (reason: Error) => void;
    const promise = new Promise<T>((yes, no) => {
        resolve = yes;
        reject = no;
    });
    return { promise, resolve, reject };
}
function fixture(currentQuery: () => ModelQuery | undefined = () => query) {
    const requests: {
        path: string;
        body?: unknown;
        response: ReturnType<typeof deferred<ModelCatalog>>;
    }[] = [];
    const changes: string[] = [];
    const cache = new ModelCatalogs(
        async <T>(path: string, body?: unknown) => {
            const response = deferred<ModelCatalog>();
            requests.push({ path, body, response });
            return (await response.promise) as T;
        },
        (id, model) => changes.push(`${id}:${model}`),
        currentQuery,
    );
    return { cache, requests, changes };
}

test("parallel loads share a request and settled catalogs are reused", async () => {
    const { cache, requests, changes } = fixture();
    const first = cache.load("1", query);
    assert.equal(cache.load("1", query), first);
    await Promise.resolve();
    assert.equal(requests.length, 1);
    assert.equal(cache.get("1").loading, true);
    const result = catalog();
    requests[0].response.resolve(result);
    await first;
    await cache.load("1", query);
    assert.equal(requests.length, 1);
    assert.deepEqual(cache.get("1"), {
        catalog: result,
        error: undefined,
        loading: false,
    });
    assert.deepEqual(changes, ["1:"]);
});

for (const reset of ["clear", "invalidate"] as const) {
    for (const completion of ["success", "failure"] as const) {
        test(`${reset} detaches an old ${completion} from a newer request on the same connection`, async () => {
            const { cache, requests, changes } = fixture();
            const old = cache.load("1", query);
            await Promise.resolve();
            if (reset === "clear") cache.clear();
            else cache.invalidate("1");
            const current = cache.load("1", query);
            await Promise.resolve();
            if (completion === "success")
                requests[0].response.resolve(catalog({ refreshing: true }));
            else requests[0].response.reject(new Error("Obsolete failure"));
            await old;
            assert.equal(cache.get("1").loading, true);
            assert.equal(cache.get("1").catalog, undefined);
            assert.equal(cache.get("1").error, undefined);
            assert.deepEqual(changes, []);
            assert.equal(cache.load("1", query), current);
            const result = catalog({ defaultModel: "current" });
            requests[1].response.resolve(result);
            await current;
            assert.equal(requests.length, 2);
            assert.equal(cache.get("1").catalog, result);
            assert.deepEqual(changes, ["1:"]);
            cache.clear();
        });
    }
}

test("a changed model or limit gets a new catalog with encoded query values", async () => {
    const { cache, requests } = fixture();
    const first = cache.load("a/b", query);
    await Promise.resolve();
    requests[0].response.resolve(catalog());
    await first;
    const next = cache.load("a/b", {
        limit: 10,
        selectedModel: "model & preview",
    });
    await Promise.resolve();
    assert.equal(
        requests[1].path,
        "/api/connections/a%2Fb/models?limit=10&selected=model+%26+preview",
    );
    requests[1].response.resolve(catalog());
    await next;
});

test("failed requests back off for a minute and a successful retry clears the error", async (t) => {
    t.mock.timers.enable({ apis: ["Date"], now: 1_000_000 });
    const { cache, requests } = fixture();
    const failed = cache.load("1", query);
    await Promise.resolve();
    requests[0].response.reject(new Error("Model list unavailable"));
    await failed;
    await cache.load("1", query);
    assert.equal(requests.length, 1);
    assert.equal(cache.get("1").error, "Model list unavailable");
    t.mock.timers.tick(60_000);
    const retried = cache.load("1", query);
    await Promise.resolve();
    requests[1].response.resolve(catalog());
    await retried;
    assert.equal(cache.get("1").error, undefined);
});

for (const state of [
    "fresh",
    "stale",
    "unavailable",
    "expired",
    "invalid timestamp",
] as const) {
    test(`${state} catalogs follow their refresh interval without immediate request loops`, async (t) => {
        t.mock.timers.enable({ apis: ["Date"], now: 1_000_000 });
        const { cache, requests } = fixture();
        const first = cache.load("1", query);
        await Promise.resolve();
        requests[0].response.resolve(
            catalog({
                stale: state === "stale",
                unavailable: state === "unavailable",
                fetchedAt:
                    state === "expired"
                        ? new Date(-86_400_000).toISOString()
                        : state === "invalid timestamp"
                          ? "invalid"
                          : new Date(Date.now()).toISOString(),
            }),
        );
        await first;
        await cache.load("1", query);
        assert.equal(requests.length, 1);
        t.mock.timers.tick(state === "fresh" ? 86_400_000 : 60_000);
        const next = cache.load("1", query);
        await Promise.resolve();
        assert.equal(requests.length, 2);
        requests[1].response.resolve(catalog());
        await next;
    });
}

test("refreshing catalogs poll until settled and clear cancels their timer", async (t) => {
    t.mock.timers.enable({ apis: ["Date", "setTimeout"], now: 1_000_000 });
    const { cache, requests } = fixture();
    const first = cache.load("1", query);
    await Promise.resolve();
    requests[0].response.resolve(catalog({ refreshing: true }));
    await first;
    t.mock.timers.tick(1999);
    assert.equal(requests.length, 1);
    t.mock.timers.tick(1);
    const poll = cache.load("1", query);
    await Promise.resolve();
    requests[1].response.resolve(catalog());
    await poll;
    t.mock.timers.tick(2000);
    assert.equal(requests.length, 2);
    const again = cache.load("1", query, true);
    await Promise.resolve();
    requests[2].response.resolve(catalog({ refreshing: true }));
    await again;
    cache.clear();
    t.mock.timers.tick(2000);
    await Promise.resolve();
    assert.equal(requests.length, 3);
});

test("polling follows the visible selection and stops after navigating away", async (t) => {
    t.mock.timers.enable({ apis: ["Date", "setTimeout"], now: 1_000_000 });
    let visible: ModelQuery | undefined = query;
    const { cache, requests } = fixture(() => visible);
    const first = cache.load("1", query);
    await Promise.resolve();
    requests[0].response.resolve(catalog({ refreshing: true }));
    await first;
    visible = { limit: 10, selectedModel: "new-model" };
    t.mock.timers.tick(2000);
    const poll = cache.load("1", visible);
    await Promise.resolve();
    assert.equal(
        requests[1].path,
        "/api/connections/1/models?limit=10&selected=new-model",
    );
    requests[1].response.resolve(catalog({ refreshing: true }));
    await poll;
    visible = undefined;
    t.mock.timers.tick(2000);
    await Promise.resolve();
    assert.equal(requests.length, 2);
    cache.clear();
});

test("manual refresh tracks POST and GET as one request and retains the catalog on failure", async () => {
    const { cache, requests } = fixture();
    const first = cache.load("1", query);
    await Promise.resolve();
    const original = catalog();
    requests[0].response.resolve(original);
    await first;
    const refresh = cache.refresh("1", query);
    await Promise.resolve();
    assert.equal(requests[1].path, "/api/connections/1/models/refresh");
    assert.deepEqual(requests[1].body, {});
    assert.equal(cache.load("1", query), refresh);
    requests[1].response.reject(new Error("Refresh unavailable"));
    await refresh;
    assert.equal(cache.get("1").catalog, original);
    assert.equal(cache.get("1").error, "Refresh unavailable");
    const retry = cache.refresh("1", query);
    await Promise.resolve();
    requests[2].response.resolve(catalog());
    // The resolved POST dispatches the GET on the next microtasks.
    await Promise.resolve();
    await Promise.resolve();
    assert.equal(requests[3].path, "/api/connections/1/models?limit=3");
    const updated = catalog({ defaultModel: "new" });
    requests[3].response.resolve(updated);
    await retry;
    assert.equal(cache.get("1").catalog, updated);
    assert.equal(cache.get("1").error, undefined);
});

test("clearing during manual refresh prevents a follow-up GET", async () => {
    const { cache, requests, changes } = fixture();
    const refresh = cache.refresh("1", query);
    await Promise.resolve();
    cache.clear();
    requests[0].response.resolve(catalog());
    await refresh;
    assert.equal(requests.length, 1);
    assert.deepEqual(changes, []);
});

test("synchronous adapter failures settle loading state", async () => {
    const cache = new ModelCatalogs(
        () => {
            throw new Error("Adapter failed");
        },
        () => {},
        () => undefined,
    );
    await cache.load("1", query);
    assert.equal(cache.get("1").loading, false);
    assert.equal(cache.get("1").error, "Adapter failed");
});
