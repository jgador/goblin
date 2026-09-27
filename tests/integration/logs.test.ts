import test from "node:test";
import assert from "node:assert/strict";
import { createServer, request as httpRequest } from "node:http";
import { once } from "node:events";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { execFileSync } from "node:child_process";
import { startBackend, writePasswordHash } from "../support/backend.js";

function send(
    url: string,
    headers: Record<string, string>,
    method = "GET",
    body?: string,
) {
    return new Promise<{
        status: number;
        headers: Record<string, string | string[] | undefined>;
        body: string;
    }>((resolve, reject) => {
        const request = httpRequest(url, { method, headers }, (response) => {
            let result = "";
            response.setEncoding("utf8").on("data", (part) => (result += part));
            response.on("error", reject);
            response.on("end", () =>
                resolve({
                    status: response.statusCode!,
                    headers: response.headers,
                    body: result,
                }),
            );
        });
        request.on("error", reject);
        request.setTimeout(10000, () =>
            request.destroy(new Error("Log proxy timed out")),
        );
        request.end(body);
    });
}

test("JSON console wiring preserves structured values and scopes and defaults to Information", () => {
    const output = execFileSync(
        "dotnet",
        [
            resolve(
                "backend/tests/Goblin.LoggingSmoke/bin/Debug/net10.0/Goblin.LoggingSmoke.dll",
            ),
            "format-smoke",
        ],
        { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] },
    );
    const entries = output
        .split("\n")
        .filter((line) => line.startsWith("{"))
        .map((line) => JSON.parse(line));
    assert.equal(entries.length, 3);
    assert.equal(entries[0].Category, "Goblin.LoggingSmoke");
    assert.equal(entries[0].LogLevel, "Information");
    assert.equal(entries[0].EventId, 42);
    assert.match(entries[0].Timestamp, /(?:Z|\+00:00)$/);
    // Check serialized digits before JavaScript's Number parsing can round them.
    assert.match(output, /"AttemptId":9007199254740993/);
    assert.equal(entries[0].Scopes[0].WorkId, "9007199254740993");
    assert.equal(entries[0].State.Marker, "format-smoke");
    assert.match(entries[1].Message, /\nsecond line$/);
    assert.match(entries[1].Exception, /Synthetic smoke failure/);
    assert.equal(
        entries[2].Category,
        "Microsoft.AspNetCore.Hosting.Diagnostics",
    );
    assert.equal(entries[2].LogLevel, "Information");
    assert.equal(entries[2].State.Marker, "format-smoke");
    assert.match(output, /Plain console format-smoke/);
    assert.doesNotMatch(output, /filtered-smoke-debug-details/);
});

test("logs require Goblin login, proxy read APIs and streams, and exclude ingestion and credentials", async (t) => {
    for (const origin of [
        "http://localhost:8788",
        "http://goblin-logs.southeastasia.cloudapp.azure.com",
    ]) {
        await t.test(origin, async (t) => {
            const observed: {
                path: string;
                headers: Record<string, unknown>;
                body: string;
            }[] = [];
            const upstream = createServer(async (request, response) => {
                let body = "";
                for await (const chunk of request) body += chunk;
                observed.push({
                    path: request.url!,
                    headers: request.headers,
                    body,
                });
                response.setHeader("Content-Type", "application/stream+json");
                response.setHeader("Set-Cookie", "upstream=forbidden");
                response.setHeader("Server", "private-upstream");
                if (request.url!.includes("/tail")) {
                    response.write('{"_msg":"first"}\n');
                    const timer = setTimeout(
                        () => response.end('{"_msg":"second"}\n'),
                        300,
                    );
                    response.on("close", () => clearTimeout(timer));
                } else response.end('{"_msg":"saved"}\n');
            }).listen(0, "127.0.0.1");
            await once(upstream, "listening");
            t.after(() => {
                upstream.closeAllConnections();
                upstream.close();
            });
            const dataDir = await mkdtemp(join(tmpdir(), "goblin-logs-http-"));
            t.after(() => rm(dataDir, { recursive: true, force: true }));
            const passwordHashFile = join(dataDir, "owner-password");
            await writePasswordHash(passwordHashFile, "logs-test-password");
            const app = await startBackend({
                dataDir,
                passwordHashFile,
                publicOrigin: origin,
                allowInsecureHttp: true,
                victoriaLogsUrl: `http://127.0.0.1:${(upstream.address() as { port: number }).port}`,
            });
            t.after(() => app.close());
            const headers = {
                Host: new URL(origin).host,
                Origin: origin,
                "Content-Type": "application/json",
            };
            assert.equal(
                (await send(app.url + "/api/logs", headers)).status,
                401,
            );
            const locked = await send(app.url + "/logs/", {
                ...headers,
                Accept: "text/html",
            });
            assert.equal(locked.status, 302);
            assert.equal(locked.headers.location, "/?returnTo=logs");
            assert.equal(
                (await send(app.url + "/logs/select/logsql/query", headers))
                    .status,
                401,
            );
            const login = await send(
                app.url + "/api/session",
                headers,
                "POST",
                JSON.stringify({ password: "logs-test-password" }),
            );
            const authenticated = {
                ...headers,
                Cookie: (login.headers["set-cookie"] as string[])[0].split(
                    ";",
                )[0],
            };
            assert.deepEqual(
                JSON.parse(
                    (await send(app.url + "/api/logs", authenticated)).body,
                ),
                { available: true },
            );
            assert.equal(
                (await send(app.url + "/logs/", authenticated)).headers
                    .location,
                "/logs/select/vmui/",
            );
            const form = "query=service%3Agoblin&limit=20";
            const query = await send(
                app.url + "/logs/select/logsql/query",
                {
                    ...authenticated,
                    "Content-Type": "application/x-www-form-urlencoded",
                    Authorization: "Bearer forbidden",
                    AccountID: "12",
                    ProjectID: "34",
                    "X-Forwarded-User": "forbidden",
                    "Proxy-To": "https://foreign.example",
                },
                "POST",
                form,
            );
            assert.equal(query.status, 200);
            assert.equal(query.headers["set-cookie"], undefined);
            assert.equal(query.headers.server, undefined);
            assert.equal(query.headers["cache-control"], "no-store");
            assert.equal(observed.at(-1)!.body, form);
            assert.equal(
                observed.at(-1)!.headers["content-type"],
                "application/x-www-form-urlencoded",
            );
            for (const header of [
                "cookie",
                "authorization",
                "accountid",
                "projectid",
                "x-forwarded-user",
                "proxy-to",
            ])
                assert.equal(
                    observed.at(-1)!.headers[header],
                    undefined,
                    header,
                );
            const count = observed.length;
            for (const path of [
                "/logs/insert/jsonline",
                "/logs/delete",
                "/logs/metrics",
                "/logs/flags",
                "/logs/debug/pprof/",
                "/logs/internal/select/query",
                "/logs/select/vmalert/",
                "/logs/select/vmui/%252e%252e/insert/jsonline",
            ]) {
                for (const method of ["GET", "POST"])
                    assert.equal(
                        (await send(app.url + path, authenticated, method))
                            .status,
                        403,
                        `${method} ${path}`,
                    );
            }
            assert.equal(
                (
                    await send(
                        app.url + "/logs/select/logsql/query",
                        authenticated,
                        "DELETE",
                    )
                ).status,
                403,
            );
            assert.equal(
                (
                    await send(
                        app.url + "/logs/select/logsql/query",
                        { ...authenticated, Origin: "https://foreign.example" },
                        "POST",
                        form,
                    )
                ).status,
                403,
            );
            assert.equal(
                (
                    await send(app.url + "/logs/select/logsql/query", {
                        ...authenticated,
                        Host: "foreign.example",
                    })
                ).status,
                403,
            );
            assert.equal(observed.length, count);
            await new Promise<void>((resolve, reject) => {
                const request = httpRequest(
                    app.url + "/logs/select/logsql/tail?query=*",
                    { headers: authenticated },
                    (response) => {
                        let chunks = 0;
                        response.on("data", () => chunks++);
                        response.on("error", reject);
                        response.on("end", () => {
                            try {
                                assert.ok(
                                    chunks >= 2,
                                    "Live tail must reach the client before completion",
                                );
                                resolve();
                            } catch (error) {
                                reject(error);
                            }
                        });
                    },
                );
                request.on("error", reject).end();
            });
            assert.ok(
                !String(
                    (await send(app.url + "/", authenticated)).headers[
                        "content-security-policy"
                    ],
                ).includes("unsafe-inline"),
            );
            await send(
                app.url + "/api/session/lock",
                authenticated,
                "POST",
                "{}",
            );
            assert.equal(
                (
                    await send(
                        app.url + "/logs/select/logsql/query",
                        authenticated,
                    )
                ).status,
                401,
            );
        });
    }
});

test("logging failure leaves Goblin ready and returns a safe unavailable response", async (t) => {
    const dataDir = await mkdtemp(join(tmpdir(), "goblin-logs-disabled-"));
    t.after(() => rm(dataDir, { recursive: true, force: true }));
    const passwordHashFile = join(dataDir, "owner-password");
    await writePasswordHash(passwordHashFile, "logs-test-password");
    for (const victoriaLogsUrl of [undefined, "http://127.0.0.1:1"]) {
        const app = await startBackend({
            dataDir,
            passwordHashFile,
            victoriaLogsUrl,
        });
        try {
            const headers = {
                Host: "localhost:8787",
                Origin: "http://localhost:8787",
                "Content-Type": "application/json",
            };
            const login = await send(
                app.url + "/api/session",
                headers,
                "POST",
                JSON.stringify({ password: "logs-test-password" }),
            );
            const authenticated = {
                ...headers,
                Cookie: (login.headers["set-cookie"] as string[])[0].split(
                    ";",
                )[0],
            };
            assert.deepEqual(
                JSON.parse(
                    (await send(app.url + "/api/logs", authenticated)).body,
                ),
                { available: !!victoriaLogsUrl },
            );
            const response = await send(
                app.url + "/logs/select/logsql/query",
                authenticated,
            );
            assert.equal(response.status, victoriaLogsUrl ? 503 : 404);
            assert.equal(
                JSON.parse(response.body).error.code,
                "logs_unavailable",
            );
            assert.equal(
                (await send(app.url + "/readyz", headers)).status,
                200,
            );
        } finally {
            await app.close();
        }
    }
});
