import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";

const origin = new URL(process.argv[2]);
assert.ok(["http:", "https:"].includes(origin.protocol));
assert.equal(origin.pathname, "/");
const digest = (bytes) => createHash("sha256").update(bytes).digest("hex");
const files = [
    "index.html",
    "website.css",
    "website.js",
    "assets/goblin.svg",
    "assets/inter-18pt-medium.ttf",
];
for (const file of files) {
    const response = await fetch(
        new URL(file === "index.html" ? "/" : file, origin),
        { signal: AbortSignal.timeout(15000), cache: "no-store" },
    );
    assert.equal(response.status, 200, `${file}: HTTP ${response.status}`);
    assert.equal(
        digest(Buffer.from(await response.arrayBuffer())),
        digest(await readFile(new URL(file, import.meta.url))),
        `${file} differs from the release source`,
    );
    assert.equal(response.headers.get("x-content-type-options"), "nosniff");
    assert.equal(response.headers.get("x-frame-options"), "DENY");
    assert.ok(
        response.headers
            .get("content-security-policy")
            ?.includes("frame-ancestors 'none'"),
    );
}
const health = await fetch(new URL("healthz", origin), {
    signal: AbortSignal.timeout(15000),
});
assert.equal(health.status, 200);
assert.equal((await health.text()).trim(), "ok");
const missing = await fetch(new URL("website-missing-file", origin), {
    signal: AbortSignal.timeout(15000),
});
assert.equal(missing.status, 404);
console.log(
    `Verified release bytes, security headers, health, and 404 responses at ${origin.origin}.`,
);
