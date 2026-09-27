import test from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, readFile, writeFile, rm, readdir } from "node:fs/promises";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { runInNewContext } from "node:vm";
import { request as httpRequest } from "node:http";
import { startBackend, writePasswordHash } from "../support/backend.js";

test("workspace timezone is authenticated, validated, shared, durable, and protected from stale updates", async (t) => {
    const dataDir = await mkdtemp(join(tmpdir(), "goblin-preferences-"));
    const passwordHashFile = join(dataDir, "owner-password");
    const preferencesFile = join(dataDir, "workspace-preferences.json");
    const publicOrigin = "http://localhost:8787";
    await writePasswordHash(passwordHashFile, "preferences-test");
    let app = await startBackend({ dataDir, passwordHashFile, publicOrigin });
    t.after(async () => {
        await app.close();
        await rm(dataDir, { recursive: true, force: true });
    });
    async function request(
        path: string,
        cookie = "",
        body?: unknown,
        origin = publicOrigin,
    ) {
        return new Promise<Response>((resolve, reject) => {
            const outgoing = httpRequest(
                app.url + path,
                {
                    method: body === undefined ? "GET" : "POST",
                    headers: {
                        Host: new URL(publicOrigin).host,
                        Origin: origin,
                        Cookie: cookie,
                        "Content-Type": "application/json",
                    },
                },
                (incoming) => {
                    let raw = "";
                    incoming
                        .setEncoding("utf8")
                        .on("data", (chunk) => (raw += chunk));
                    incoming.on("error", reject);
                    incoming.on("end", () => {
                        const headers = new Headers();
                        for (const [key, value] of Object.entries(
                            incoming.headers,
                        ))
                            for (const entry of Array.isArray(value)
                                ? value
                                : value
                                  ? [value]
                                  : [])
                                headers.append(key, entry);
                        resolve(
                            new Response(raw, {
                                status: incoming.statusCode!,
                                headers,
                            }),
                        );
                    });
                },
            );
            outgoing.on("error", reject);
            outgoing.end(body === undefined ? undefined : JSON.stringify(body));
        });
    }
    async function unlock() {
        const response = await request("/api/session", "", {
            password: "preferences-test",
        });
        assert.equal(response.status, 200);
        return response.headers.getSetCookie()[0].split(";")[0];
    }
    for (const path of [
        "/api/preferences",
        "/api/preferences/timezones",
        "/api/preferences/logs.js",
    ])
        assert.equal((await request(path)).status, 401);
    assert.equal(
        (
            await request("/api/preferences/timezone", "", {
                timeZone: "Asia/Manila",
            })
        ).status,
        401,
    );
    const first = await unlock();
    const second = await unlock();
    assert.deepEqual(await (await request("/api/preferences", first)).json(), {
        timeZone: null,
    });
    const zones = (await (
        await request("/api/preferences/timezones", first)
    ).json()) as string[];
    assert.ok(zones.length > 400);
    for (const zone of ["UTC", "Asia/Manila"])
        assert.ok(zones.includes(zone), zone);
    for (const timeZone of [
        "",
        "not/a-zone",
        "Philippine Standard Time",
        "+08:00",
        "Asia/Manila<script>",
        null,
        123,
    ])
        assert.equal(
            (await request("/api/preferences/timezone", first, { timeZone }))
                .status,
            400,
        );
    assert.equal(
        (
            await request(
                "/api/preferences/timezone",
                first,
                { timeZone: "Asia/Manila" },
                "https://foreign.example",
            )
        ).status,
        403,
    );
    const concurrent = await Promise.all(
        [first, second].map((cookie) =>
            request("/api/preferences/timezone", cookie, {
                timeZone: "Asia/Manila",
                expectedTimeZone: null,
            }),
        ),
    );
    assert.deepEqual(
        concurrent.map((result) => result.status).sort(),
        [200, 409],
    );
    const saved = (await (
        await request("/api/preferences", second)
    ).json()) as { timeZone: string };
    assert.equal(
        (
            await request("/api/preferences/timezone", second, {
                timeZone: "Asia/Manila",
                expectedTimeZone: null,
            })
        ).status,
        409,
    );
    // Verify changes to the UTC fallback and back to Philippine time.
    for (const timeZone of ["UTC", "Asia/Manila"]) {
        assert.equal(
            (
                await request("/api/preferences/timezone", second, {
                    timeZone,
                    expectedTimeZone: saved.timeZone,
                })
            ).status,
            200,
        );
        saved.timeZone = timeZone;
    }
    assert.deepEqual(JSON.parse(await readFile(preferencesFile, "utf8")), {
        timeZone: "Asia/Manila",
    });
    assert.deepEqual(
        (await readdir(dataDir)).filter((name) => name.endsWith(".tmp")),
        [],
    );

    await app.close();
    app = await startBackend({ dataDir, passwordHashFile, publicOrigin });
    const restarted = await unlock();
    assert.deepEqual(
        await (await request("/api/preferences", restarted)).json(),
        { timeZone: "Asia/Manila" },
    );
    const script = await request("/api/preferences/logs.js", restarted);
    assert.match(script.headers.get("Content-Type")!, /text\/javascript/);
    assert.equal(script.headers.get("Cache-Control"), "no-store");
    const storage = new Map<string, string>();
    runInNewContext(await script.text(), {
        localStorage: {
            setItem: (key: string, value: string) => storage.set(key, value),
        },
    });
    assert.deepEqual(JSON.parse(storage.get("VLUI:TIMEZONE")!), {
        value: "Asia/Manila",
    });

    await writeFile(preferencesFile, "broken-json");
    assert.equal((await request("/api/preferences", restarted)).status, 503);
    assert.equal(
        (
            await request("/api/preferences/timezone", restarted, {
                timeZone: "Asia/Manila",
            })
        ).status,
        503,
    );
    assert.equal(await readFile(preferencesFile, "utf8"), "broken-json");
});
