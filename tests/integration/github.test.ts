import test from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { request as httpRequest } from "node:http";
import { setTimeout as delay } from "node:timers/promises";
import { startBackend, writePasswordHash } from "../support/backend.js";

test(
    "GitHub device login uses protected UI APIs and public ports cannot reach the repository broker",
    { skip: process.platform !== "linux" },
    async (t) => {
        const dataDir = await mkdtemp(join(tmpdir(), "goblin-github-http-"));
        const passwordHashFile = join(dataDir, "owner-password"),
            cli = join(dataDir, "fake-gh");
        await writePasswordHash(passwordHashFile, "github-test");
        await writeFile(
            cli,
            "#!/bin/sh\nprintf '! First copy your one-time code: ABCD-1234\\n' >&2\nexec sleep 60\n",
            { mode: 0o700 },
        );
        const app = await startBackend({
            dataDir,
            passwordHashFile,
            gitHubCommand: cli,
        });
        t.after(async () => {
            await app.close();
            await rm(dataDir, { recursive: true, force: true });
        });
        let cookie = "";
        async function request(
            path: string,
            body?: unknown,
            origin = "http://localhost:8787",
        ) {
            return new Promise<{
                status: number;
                body: Record<string, unknown>;
            }>((resolve, reject) => {
                const req = httpRequest(
                    app.url + path,
                    {
                        method: body === undefined ? "GET" : "POST",
                        headers: {
                            Host: "localhost:8787",
                            Origin: origin,
                            Cookie: cookie,
                            "Content-Type": "application/json",
                        },
                    },
                    (response) => {
                        if (response.headers["set-cookie"])
                            cookie =
                                response.headers["set-cookie"][0]!.split(
                                    ";",
                                )[0]!;
                        let text = "";
                        response.on("data", (chunk) => (text += chunk));
                        response.on("end", () =>
                            resolve({
                                status: response.statusCode!,
                                body: JSON.parse(text),
                            }),
                        );
                    },
                );
                req.on("error", reject);
                req.end(body === undefined ? undefined : JSON.stringify(body));
            });
        }
        assert.equal((await request("/api/github")).status, 401);
        assert.equal(
            (await request("/api/session", { password: "github-test" })).status,
            200,
        );
        assert.equal(
            (
                await request(
                    "/api/github/connect",
                    {},
                    "https://untrusted.example",
                )
            ).status,
            403,
        );
        assert.equal(
            (await request("/api/github/connect", {})).body.status,
            "Connecting",
        );
        let state: Record<string, unknown> = {};
        for (let i = 0; i < 50; i++) {
            state = (await request("/api/github")).body;
            if (state.userCode) break;
            await delay(50);
        }
        assert.equal(state.userCode, "ABCD-1234");
        assert.equal(state.verificationUrl, "https://github.com/login/device");
        assert.equal(
            (await request("/internal/repository/1/input")).status,
            404,
        );
        assert.equal(
            (await request("/internal/repository/1/setup-memory")).status,
            404,
        );
        assert.equal(
            (await request("/internal/repository/1/setup-memory", {})).status,
            404,
        );
        assert.equal(
            (await request("/internal/repository/1/operation-id", {})).status,
            404,
        );
        assert.equal(
            (await request("/api/github/cancel", {})).body.status,
            "Disconnected",
        );
        assert.equal((await request("/api/github")).body.userCode, null);
    },
);
