import assert from "node:assert/strict";
import { test } from "node:test";
import { ApiError, requestJson } from "../../frontend/src/api/client.js";
import { ReadScope } from "../../frontend/src/api/read-scope.js";

test("Goblin reads bypass caches and mutations preserve JSON and same-origin credentials", async (t) => {
    const requests: Request[] = [];
    t.mock.method(
        globalThis,
        "fetch",
        async (path: string, options: RequestInit) => {
            requests.push(new Request(`https://goblin.test${path}`, options));
            return Response.json({ saved: true });
        },
    );
    assert.deepEqual(await requestJson("/api/work"), { saved: true });
    const body = { workId: "9007199254740993", text: "Keep this exact text" };
    await requestJson("/api/work/commands", { body });
    assert.equal(requests[0].method, "GET");
    assert.equal(requests[0].cache, "no-store");
    assert.equal(requests[1].method, "POST");
    assert.equal(requests[1].credentials, "same-origin");
    assert.equal(requests[1].headers.get("Content-Type"), "application/json");
    assert.deepEqual(await requests[1].json(), body);
});

test("public failures retain their code while non-JSON failures use the caller's safe message", async (t) => {
    t.mock.method(globalThis, "fetch", async () =>
        Response.json(
            { error: { code: "prompt_timeout", message: "Check timed out" } },
            { status: 504 },
        ),
    );
    await assert.rejects(
        requestJson("/api/prompt", { body: {} }),
        (error: unknown) => {
            assert.ok(error instanceof ApiError);
            assert.equal(error.code, "prompt_timeout");
            assert.equal(error.status, 504);
            assert.equal(error.message, "Check timed out");
            return true;
        },
    );
    t.mock.method(
        globalThis,
        "fetch",
        async () => new Response("private proxy diagnostics", { status: 502 }),
    );
    await assert.rejects(
        requestJson("/api/work", { failureMessage: "Work is unavailable." }),
        { message: "Work is unavailable." },
    );
});

test("a non-JSON unauthorized response clears the private view before parsing fails", async (t) => {
    let locked = 0;
    t.mock.method(
        globalThis,
        "fetch",
        async () => new Response("unauthorized", { status: 401 }),
    );
    await assert.rejects(
        requestJson("/api/work", { onUnauthorized: () => locked++ }),
        ApiError,
    );
    assert.equal(locked, 1);
});

test("resetting the read scope rejects late JSON even when a transport ignores cancellation", async (t) => {
    const scope = new ReadScope();
    let release!: (response: Response) => void;
    t.mock.method(
        globalThis,
        "fetch",
        () =>
            new Promise<Response>((resolve) => {
                release = resolve;
            }),
    );
    const oldSignal = scope.signal;
    const old = requestJson("/api/work", { signal: oldSignal });
    scope.reset();
    release(Response.json({ private: "old workspace" }));
    await assert.rejects(old, { name: "AbortError" });
    assert.equal(oldSignal.aborted, true);
    assert.equal(scope.signal.aborted, false);
});

test("resetting during JSON decoding rejects the old response", async (t) => {
    const scope = new ReadScope();
    let release!: (result: unknown) => void;
    let started!: () => void;
    const decoding = new Promise<void>((resolve) => {
        started = resolve;
    });
    t.mock.method(
        globalThis,
        "fetch",
        async () =>
            ({
                status: 200,
                ok: true,
                json: () => {
                    started();
                    return new Promise((resolve) => {
                        release = resolve;
                    });
                },
            }) as Response,
    );
    const old = requestJson("/api/work", { signal: scope.signal });
    await decoding;
    scope.reset();
    release({ private: "old workspace" });
    await assert.rejects(old, { name: "AbortError" });
});
