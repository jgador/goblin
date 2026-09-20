import test from "node:test";
import assert from "node:assert/strict";
import { request as httpRequest } from "node:http";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { startBackend, writePasswordHash } from "../support/backend.js";

test("the work preview and its assets are public while workspace APIs remain protected", async (t) => {
    const dataDir = await mkdtemp(join(tmpdir(), "goblin-work-"));
    t.after(() => rm(dataDir, { recursive: true, force: true }));
    const passwordHashFile = join(dataDir, "owner-password");
    await writePasswordHash(passwordHashFile, "work-preview-test");
    const app = await startBackend({ dataDir, passwordHashFile });
    t.after(() => app.close());
    const paths = new Map([
        ["/", "text/html"],
        ["/app.js", "text/javascript"],
        ["/styles.css", "text/css"],
        ["/work", "text/html"],
        ["/work/", "text/html"],
        ["/work/app.js", "text/javascript"],
        ["/work/styles.css", "text/css"],
        ["/assets/branding/icon.svg", "image/svg+xml"],
        ["/api/status", "application/json"],
    ]);
    for (const [path, contentType] of paths) {
        const result = await new Promise<{
            status: number;
            type: string;
            csp: string;
            body: string;
        }>((resolve, reject) => {
            const request = httpRequest(
                app.url + path,
                { headers: { Host: "localhost:8787" } },
                (response) => {
                    let body = "";
                    response.setEncoding("utf8");
                    response.on("data", (chunk) => {
                        body += chunk;
                    });
                    response.on("end", () =>
                        resolve({
                            status: response.statusCode ?? 0,
                            type: response.headers["content-type"] ?? "",
                            csp: String(
                                response.headers["content-security-policy"] ??
                                    "",
                            ),
                            body,
                        }),
                    );
                },
            );
            request.on("error", reject);
            request.end();
        });
        assert.equal(result.status, path === "/api/status" ? 401 : 200, path);
        assert.ok(result.type.startsWith(contentType), path);
        assert.match(result.csp, /script-src 'self'; style-src 'self'/);
        assert.ok(result.body.length > 0);
        if (path === "/work" || path === "/work/") {
            assert.match(result.body, /src="\/work\/app\.js"/);
            assert.match(result.body, /href="\/work\/styles\.css"/);
        }
    }
});

test(
    "Work HTTP commands preserve bigint IDs and remain replayable",
    {
        skip: !process.env.GOBLIN_TEST_POSTGRES_APP,
    },
    async (t) => {
        const dataDir = await mkdtemp(join(tmpdir(), "goblin-work-ids-"));
        const passwordHashFile = join(dataDir, "owner-password");
        await writePasswordHash(passwordHashFile, "work-ids-test");
        const app = await startBackend({
            dataDir,
            passwordHashFile,
            enableWork: true,
        });
        t.after(async () => {
            await app.close();
            await rm(dataDir, { recursive: true, force: true });
        });
        let cookie = "";
        async function request(
            path: string,
            body?: unknown,
        ): Promise<{
            status: number;
            body: Record<string, unknown>;
        }> {
            return new Promise((resolve, reject) => {
                const call = httpRequest(
                    app.url + path,
                    {
                        method: body === undefined ? "GET" : "POST",
                        headers: {
                            Host: "localhost:8787",
                            Origin: "http://localhost:8787",
                            Cookie: cookie,
                            "Content-Type": "application/json",
                        },
                    },
                    (response) => {
                        let raw = "";
                        response.setEncoding("utf8");
                        response.on("data", (chunk) => {
                            raw += chunk;
                        });
                        response.on("end", () => {
                            cookie =
                                response.headers["set-cookie"]?.[0]?.split(
                                    ";",
                                )[0] ?? cookie;
                            resolve({
                                status: response.statusCode ?? 0,
                                body: JSON.parse(raw),
                            });
                        });
                    },
                );
                call.on("error", reject);
                call.end(body === undefined ? undefined : JSON.stringify(body));
            });
        }
        assert.equal(
            (await request("/api/identities", { kinds: ["Work"] })).status,
            401,
        );
        assert.equal(
            (await request("/api/session", { password: "work-ids-test" }))
                .status,
            200,
        );
        const allocation = await request("/api/identities", {
            kinds: ["Work", "Command", "Command"],
        });
        assert.equal(allocation.status, 200);
        const ids = allocation.body.ids as string[];
        assert.equal(ids.length, 3);
        for (const id of ids) {
            assert.equal(typeof id, "string");
            assert.ok(BigInt(id) > 0n);
        }
        const workId = (9007199254740993n + BigInt(Date.now())).toString();
        const command = {
            commandId: ids[1],
            workId,
            action: "Create",
            text: "Keep all bigint digits",
        };
        const created = await request("/api/work/commands", command);
        assert.equal(created.status, 200);
        assert.equal(created.body.version, "1");
        assert.equal((created.body.work as { id: string }).id, workId);
        assert.deepEqual(
            (await request("/api/work/commands", command)).body,
            created.body,
        );
        const retrieved = await request("/api/work/" + workId);
        assert.equal(retrieved.status, 200);
        assert.equal(retrieved.body.version, created.body.version);
        assert.deepEqual(retrieved.body.work, created.body.work);
        const context = await request("/api/work/commands", {
            commandId: ids[2],
            workId,
            action: "AddContext",
            expectedVersion: "1",
            text: "More context",
        });
        assert.equal(context.status, 200);
        assert.equal(context.body.version, "2");
        const conflicting = await request("/api/work/commands", {
            ...command,
            text: "Different command",
        });
        assert.equal(conflicting.status, 409);
        assert.equal(
            (conflicting.body.error as { code: string }).code,
            "command_id_reused",
        );
        for (const invalid of [
            "0",
            "-1",
            "9223372036854775808",
            "1.5",
            "not-an-id",
        ]) {
            const rejected = await request("/api/work/commands", {
                ...command,
                commandId: invalid,
            });
            assert.ok([400, 409].includes(rejected.status), invalid);
        }
        for (const kinds of [[], ["Event"], ["Attempt"], ["Unknown"], null]) {
            assert.ok(
                [400, 409].includes(
                    (await request("/api/identities", { kinds })).status,
                ),
            );
        }
    },
);
