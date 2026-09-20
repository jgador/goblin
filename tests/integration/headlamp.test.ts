import test from "node:test";
import assert from "node:assert/strict";
import {
    createServer,
    request as httpRequest,
    type IncomingHttpHeaders,
} from "node:http";
import { createHash, randomBytes } from "node:crypto";
import { once } from "node:events";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { startBackend, writePasswordHash } from "../support/backend.js";

// node:http preserves an explicit Host header, unlike Node's fetch implementation.
function send(
    url: string,
    options: {
        method?: string;
        headers: Record<string, string>;
        body?: string;
        redirect?: string;
    },
): Promise<Response> {
    return new Promise((resolve, reject) => {
        const req = httpRequest(
            url,
            { method: options.method ?? "GET", headers: options.headers },
            (res) => {
                let body = "";
                res.setEncoding("utf8").on("data", (part) => {
                    body += part;
                });
                res.on("error", reject);
                res.on("end", () =>
                    resolve(
                        new Response(body, {
                            status: res.statusCode!,
                            headers: Object.fromEntries(
                                Object.entries(res.headers)
                                    .filter(([, value]) => value !== undefined)
                                    .map(([key, value]) => [
                                        key,
                                        Array.isArray(value)
                                            ? value.join(",")
                                            : value!,
                                    ]),
                            ),
                        }),
                    ),
                );
            },
        );
        req.on("error", reject);
        req.setTimeout(10_000, () =>
            req.destroy(new Error("HTTP test timed out")),
        );
        req.end(options.body);
    });
}

test("Headlamp shares Goblin login on local and Azure origins, streams, and excludes credentials and mutation routes", async (t) => {
    for (const origin of [
        "http://localhost:8788",
        "http://goblin-prod.southeastasia.cloudapp.azure.com",
        "https://goblin-prod.southeastasia.cloudapp.azure.com",
    ]) {
        await t.test(origin, async (t) => {
            const dataDir = await mkdtemp(
                join(tmpdir(), "goblin-headlamp-http-"),
            );
            t.after(() => rm(dataDir, { recursive: true, force: true }));
            const observed: {
                url: string;
                headers: IncomingHttpHeaders;
                body: string;
            }[] = [];
            const upstream = createServer(async (req, res) => {
                let body = "";
                for await (const part of req) body += part;
                observed.push({ url: req.url!, headers: req.headers, body });
                res.setHeader(
                    "Set-Cookie",
                    "upstream-auth=must-not-reach-browser",
                );
                res.setHeader("Content-Type", "application/json");
                res.write(JSON.stringify({ path: req.url }));
                res.end();
            });
            upstream.on("upgrade", (req, socket) => {
                // YARP owns these hop-by-hop headers. Duplicating them breaks
                // Headlamp's Go reverse proxy when it connects to Kubernetes.
                if (
                    req.headers.upgrade !== "websocket" ||
                    req.headers.connection?.toLowerCase() !== "upgrade"
                ) {
                    socket.end(
                        "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\n\r\n",
                    );
                    return;
                }
                observed.push({
                    url: req.url!,
                    headers: req.headers,
                    body: "",
                });
                const accept = createHash("sha1")
                    .update(
                        req.headers["sec-websocket-key"] +
                            "258EAFA5-E914-47DA-95CA-C5AB0DC85B11",
                    )
                    .digest("base64");
                socket.write(
                    `HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ${accept}\r\n\r\n`,
                );
                socket.write(Buffer.from([0x81, 5, ...Buffer.from("hello")]));
                socket.on("error", () => {});
                socket.on("end", () => socket.destroy());
            });
            upstream.listen(0, "127.0.0.1");
            await once(upstream, "listening");
            t.after(() => {
                upstream.closeAllConnections();
                upstream.close();
            });
            const address = upstream.address() as { port: number };
            const passwordHashFile = join(dataDir, "owner-password");
            await writePasswordHash(passwordHashFile, "headlamp-test-password");
            const app = await startBackend({
                dataDir,
                passwordHashFile,
                publicOrigin: origin,
                allowInsecureHttp: true,
                headlampUrl: `http://127.0.0.1:${address.port}`,
            });
            t.after(() => app.close());
            let cookie = "";
            const request = async (
                path: string,
                method = "GET",
                body?: unknown,
                headers: Record<string, string> = {},
            ) =>
                send(app.url + path, {
                    method,
                    redirect: "manual",
                    headers: {
                        Host: new URL(origin).host,
                        Origin: origin,
                        Cookie: cookie,
                        "Content-Type": "application/json",
                        ...headers,
                    },
                    body: body === undefined ? undefined : JSON.stringify(body),
                });
            assert.equal((await request("/headlamp/config")).status, 401);
            const locked = await request("/headlamp/", "GET", undefined, {
                Accept: "text/html",
            });
            assert.equal(locked.status, 302);
            assert.equal(locked.headers.get("location"), "/?returnTo=headlamp");
            assert.equal(observed.length, 0);
            const login = await request("/api/session", "POST", {
                password: "headlamp-test-password",
            });
            cookie = login.headers.get("set-cookie")!.split(";")[0];
            assert.deepEqual(await (await request("/api/cluster")).json(), {
                available: true,
            });
            assert.equal(
                (await request("/headlamp")).headers.get("location"),
                "/headlamp/",
            );
            const api =
                "/headlamp/clusters/goblin/api/v1/pods?watch=true&resourceVersion=42";
            const response = await request(api, "GET", undefined, {
                Cookie: cookie + "; headlamp-token=untrusted-token",
                Authorization: "Bearer untrusted-token",
                "Impersonate-User": "system:admin",
                "X-Forwarded-User": "untrusted-user",
                "X-Forwarded-Id-Token": "untrusted-token",
                "Proxy-to": "http://untrusted.example",
            });
            assert.equal(response.status, 200);
            assert.deepEqual(await response.json(), { path: api });
            assert.equal(response.headers.get("set-cookie"), null);
            assert.equal(response.headers.get("cache-control"), "no-store");
            for (const header of [
                "cookie",
                "authorization",
                "impersonate-user",
                "x-forwarded-user",
                "x-forwarded-id-token",
                "proxy-to",
            ])
                assert.equal(
                    observed.at(-1)!.headers[header],
                    undefined,
                    header,
                );
            const review = {
                apiVersion: "authorization.k8s.io/v1",
                kind: "SelfSubjectAccessReview",
                spec: { resourceAttributes: { resource: "pods", verb: "get" } },
            };
            assert.equal(
                (
                    await request(
                        "/headlamp/clusters/goblin/apis/authorization.k8s.io/v1/selfsubjectaccessreviews",
                        "POST",
                        review,
                    )
                ).status,
                200,
            );
            assert.deepEqual(JSON.parse(observed.at(-1)!.body), review);
            const count = observed.length;
            for (const [path, method] of [
                ["/headlamp/clusters/goblin/api/v1/pods", "DELETE"],
                ["/headlamp/clusters/goblin/api/v1/pods", "POST"],
                ["/headlamp/cluster", "POST"],
                ["/headlamp/auth/set-token", "POST"],
                ["/headlamp/externalproxy", "GET"],
                [
                    "/headlamp/clusters/goblin/serviceproxy/default/service",
                    "GET",
                ],
                ["/headlamp/clusters/other/api/v1/pods", "GET"],
                ["/headlamp/clusters/goblin/helm/releases", "GET"],
            ])
                assert.equal(
                    (await request(path, method)).status,
                    403,
                    `${method} ${path}`,
                );
            assert.equal(
                (
                    await request(api, "GET", undefined, {
                        Origin: "https://foreign.example",
                    })
                ).status,
                403,
            );
            assert.equal(
                (
                    await request(api, "GET", undefined, {
                        Host: "foreign.example",
                    })
                ).status,
                403,
            );
            assert.equal(observed.length, count);
            await new Promise<void>((resolve, reject) => {
                const req = httpRequest(app.url + api, {
                    headers: {
                        Host: new URL(origin).host,
                        Origin: origin,
                        Cookie: cookie,
                        Connection: "Upgrade",
                        Upgrade: "websocket",
                        "Sec-WebSocket-Version": "13",
                        "Sec-WebSocket-Key": randomBytes(16).toString("base64"),
                    },
                });
                req.on("error", reject);
                req.on("response", (res) =>
                    reject(
                        new Error(
                            `Expected WebSocket upgrade, got ${res.statusCode}`,
                        ),
                    ),
                );
                req.on("upgrade", (res, socket, head) => {
                    const finish = (data: Buffer) => {
                        try {
                            assert.equal(res.statusCode, 101);
                            assert.ok(data.includes(Buffer.from("hello")));
                            resolve();
                        } catch (error) {
                            reject(error);
                        } finally {
                            socket.destroy();
                        }
                    };
                    if (head.length) finish(head);
                    else socket.once("data", finish);
                    socket.on("error", reject);
                });
                req.end();
            });
            assert.equal(observed.at(-1)!.headers.cookie, undefined);
            const normalCsp = (await request("/")).headers.get(
                "content-security-policy",
            )!;
            assert.ok(!normalCsp.includes("unsafe-inline"));
            assert.equal(
                (await request("/api/session/lock", "POST", {})).status,
                200,
            );
            assert.equal((await request(api)).status, 401);
            upstream.closeAllConnections();
            upstream.close();
        });
    }
});

test("Headlamp is unavailable without a configured cluster and fails safely when its upstream stops", async (t) => {
    const dataDir = await mkdtemp(join(tmpdir(), "goblin-headlamp-disabled-"));
    t.after(() => rm(dataDir, { recursive: true, force: true }));
    const passwordHashFile = join(dataDir, "owner-password");
    await writePasswordHash(passwordHashFile, "headlamp-test-password");
    for (const headlampUrl of [undefined, "http://127.0.0.1:1"]) {
        const app = await startBackend({
            dataDir,
            passwordHashFile,
            headlampUrl,
        });
        try {
            const headers = {
                Host: "localhost:8787",
                Origin: "http://localhost:8787",
                "Content-Type": "application/json",
            };
            const login = await send(app.url + "/api/session", {
                method: "POST",
                headers,
                body: JSON.stringify({ password: "headlamp-test-password" }),
            });
            const authenticated = {
                ...headers,
                Cookie: login.headers.get("set-cookie")!.split(";")[0],
            };
            assert.deepEqual(
                await (
                    await send(app.url + "/api/cluster", {
                        headers: authenticated,
                    })
                ).json(),
                { available: !!headlampUrl },
            );
            const response = await send(app.url + "/headlamp/", {
                headers: authenticated,
            });
            assert.equal(response.status, headlampUrl ? 503 : 404);
            assert.equal(
                (await response.json()).error.code,
                "cluster_unavailable",
            );
            assert.equal(
                (await send(app.url + "/readyz", { headers })).status,
                200,
            );
        } finally {
            await app.close();
        }
    }
});
