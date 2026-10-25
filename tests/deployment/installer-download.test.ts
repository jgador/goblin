import { environmentVariables as Env } from "../../config/environment.mjs";
import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { once } from "node:events";
import {
    access,
    mkdir,
    mkdtemp,
    readFile,
    rm,
    writeFile,
} from "node:fs/promises";
import { createServer } from "node:http";
import { join, resolve } from "node:path";
import test from "node:test";
import { promisify } from "node:util";

const run = promisify(execFile);

// Runs the installer's real download helper with curl against a local server.
test("installer downloads resume interrupted transfers, retry temporary errors, and stop on permanent ones", async (t) => {
    const artifacts = resolve(".artifacts/installer-download");
    await mkdir(artifacts, { recursive: true });
    const root = await mkdtemp(join(artifacts, "download-"));
    t.after(() => rm(root, { recursive: true, force: true }));
    const installer = await readFile("deploy/azure/setup/installer.sh", "utf8");
    const start = installer.indexOf("download() {");
    assert.ok(start >= 0);
    const helper = installer.slice(
        start,
        installer.indexOf("\n}\n", start) + 3,
    );
    // Backoff waits are not part of this test.
    const bin = join(root, "bin");
    await mkdir(bin);
    await writeFile(join(bin, "sleep"), "#!/bin/sh\nexit 0\n", { mode: 0o700 });

    const payload = Buffer.from("complete artifact\n".repeat(20000));
    const half = Math.floor(payload.length / 2);
    let scenario: "interrupted" | "temporary" | "missing" = "interrupted";
    const ranges: (string | undefined)[] = [];
    const server = createServer((request, response) => {
        ranges.push(request.headers.range);
        if (scenario === "missing") {
            response.writeHead(404);
            response.end("missing artifact");
        } else if (scenario === "temporary" && ranges.length === 1) {
            response.writeHead(503);
            response.end("temporary failure");
        } else if (request.headers.range) {
            const offset = Number(
                /^bytes=(\d+)-$/.exec(request.headers.range)?.[1],
            );
            response.writeHead(206, {
                "Content-Length": payload.length - offset,
                "Content-Range": `bytes ${offset}-${payload.length - 1}/${payload.length}`,
            });
            response.end(payload.subarray(offset));
        } else if (scenario === "interrupted") {
            response.writeHead(200, { "Content-Length": payload.length });
            response.write(payload.subarray(0, half), () =>
                response.socket?.destroy(),
            );
        } else {
            response.end(payload);
        }
    });
    server.listen(0, "127.0.0.1");
    await once(server, "listening");
    t.after(
        () => new Promise<void>((resolve) => server.close(() => resolve())),
    );
    const address = server.address();
    assert.ok(address && typeof address !== "string");
    const destination = join(root, "artifact");
    const download = () =>
        run(
            "bash",
            [
                "-c",
                `set -Eeuo pipefail\n${helper}\ndownload "$1" "$2"`,
                "installer-download-test",
                `http://127.0.0.1:${address.port}/artifact`,
                destination,
            ],
            {
                env: {
                    [Env.PATH.name]: `${bin}:${process.env[Env.PATH.name]}`,
                    [Env.TMPDIR.name]: root,
                },
                timeout: 30_000,
            },
        );

    await download();
    assert.equal(ranges.length, 2);
    assert.equal(ranges[0], undefined);
    assert.ok(Number(/^bytes=(\d+)-$/.exec(ranges[1] ?? "")?.[1]) > 0);
    assert.ok((await readFile(destination)).equals(payload));
    await assert.rejects(access(destination + ".part"));

    scenario = "temporary";
    ranges.length = 0;
    await rm(destination);
    await download();
    assert.equal(ranges.length, 2);
    assert.ok((await readFile(destination)).equals(payload));

    scenario = "missing";
    ranges.length = 0;
    await writeFile(destination, "retained complete artifact");
    await assert.rejects(download(), { code: 22 });
    assert.equal(ranges.length, 1, "a permanent HTTP error is not retried");
    assert.equal(
        await readFile(destination, "utf8"),
        "retained complete artifact",
    );
    await assert.rejects(access(destination + ".part"));
});
