import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { resolve } from "node:path";
import test from "node:test";
import { pathToFileURL } from "node:url";

const { downloadArchive } = (await import(
    pathToFileURL(resolve("deploy/install-gh.mjs")).href
)) as {
    downloadArchive(
        url: string,
        checksum: string,
        options: {
            request: typeof fetch;
            wait: (delay: number) => Promise<void>;
        },
    ): Promise<Buffer>;
};
const payload = Buffer.from("verified test archive");
const checksum = createHash("sha256").update(payload).digest("hex");
const url = "https://github.com/cli/cli/releases/download/test/archive.tar.gz";

test("GitHub CLI download retries temporary HTTP errors with bounded backoff", async () => {
    for (const status of [408, 429, 500, 502, 503, 504]) {
        let attempts = 0;
        const delays: number[] = [];
        const archive = await downloadArchive(url, checksum, {
            request: async (_url, options) => {
                assert.ok(options?.signal instanceof AbortSignal);
                attempts++;
                return attempts < 4
                    ? new Response("temporary", { status })
                    : new Response(payload);
            },
            wait: async (delay) => {
                delays.push(delay);
            },
        });
        assert.equal(attempts, 4);
        assert.deepEqual(delays, [1000, 2000, 4000]);
        assert.deepEqual(archive, payload);
    }
});

test("GitHub CLI download retries interrupted transfers and timeouts", async () => {
    for (const failure of [
        new TypeError("fetch failed"),
        new DOMException("download timed out", "TimeoutError"),
        new DOMException("body transfer aborted by deadline", "AbortError"),
    ]) {
        for (const phase of ["request", "body"]) {
            let attempts = 0;
            const archive = await downloadArchive(url, checksum, {
                request: async () => {
                    attempts++;
                    if (attempts === 1) {
                        if (phase === "request") throw failure;
                        return new Response(
                            new ReadableStream({
                                start(controller) {
                                    controller.enqueue(payload.subarray(0, 3));
                                    controller.error(failure);
                                },
                            }),
                        );
                    }
                    return new Response(payload);
                },
                wait: async () => {},
            });
            assert.equal(attempts, 2);
            assert.deepEqual(archive, payload);
        }
    }
});

test("GitHub CLI download stops after four temporary failures", async () => {
    let attempts = 0;
    const delays: number[] = [];
    await assert.rejects(
        downloadArchive(url, checksum, {
            request: async () => {
                attempts++;
                return new Response("unavailable", { status: 504 });
            },
            wait: async (delay) => {
                delays.push(delay);
            },
        }),
        /HTTP 504, attempt 4\/4/,
    );
    assert.equal(attempts, 4);
    assert.deepEqual(delays, [1000, 2000, 4000]);
});

test("GitHub CLI download never retries permanent errors or corrupt archives", async () => {
    for (const response of [
        new Response("missing", { status: 404 }),
        new Response("forbidden", { status: 403 }),
        new Response("unsupported", { status: 501 }),
        new Response("corrupt archive"),
    ]) {
        let attempts = 0;
        await assert.rejects(
            downloadArchive(url, checksum, {
                request: async () => {
                    attempts++;
                    return response;
                },
                wait: async () => {
                    assert.fail("Permanent failures must not be retried");
                },
            }),
            /HTTP (404|403|501)|checksum mismatch/,
        );
        assert.equal(attempts, 1);
    }
});
