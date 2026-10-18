import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import { existsSync } from "node:fs";
import {
    validateAssets,
    releaseFiles,
} from "../scripts/installation-assets.mts";

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
    "install/index.html",
    "install/install.css",
    "install/install.js",
];
const installationSource = existsSync(
    ".artifacts/installation-assets/output/azure/releases.json",
)
    ? ".artifacts/installation-assets/output"
    : "website/install";
const catalog = validateAssets(installationSource);
const [version, recommendation] = process.argv.slice(3);
if (version) {
    assert.ok(
        catalog.releases.some((entry) => entry.version === version),
        "Selected release is absent from the website snapshot",
    );
    if (recommendation === "recommend")
        assert.equal(catalog.recommended, version);
}
const installationFiles = [
    "azure/releases.json",
    ...catalog.releases.flatMap((entry) =>
        releaseFiles.map((name) => `azure/versions/${entry.version}/${name}`),
    ),
];
files.push(...installationFiles.map((file) => `install/${file}`));
for (const file of files) {
    const response = await fetch(
        new URL(file === "index.html" ? "/" : file, origin),
        { signal: AbortSignal.timeout(15000), cache: "no-store" },
    );
    assert.equal(response.status, 200, `${file}: HTTP ${response.status}`);
    assert.equal(
        digest(Buffer.from(await response.arrayBuffer())),
        digest(
            await readFile(
                installationFiles.includes(file.replace(/^install\//, ""))
                    ? `${installationSource}/${file.replace(/^install\//, "")}`
                    : new URL(file, import.meta.url),
            ),
        ),
        `${file} differs from the release source`,
    );
    assert.equal(response.headers.get("x-content-type-options"), "nosniff");
    assert.equal(response.headers.get("x-frame-options"), "DENY");
    assert.ok(
        response.headers
            .get("content-security-policy")
            ?.includes("frame-ancestors 'none'"),
    );
    if (file.startsWith("install/")) {
        assert.ok(
            response.headers
                .get("content-security-policy")
                ?.includes("connect-src 'self'"),
        );
        if (file.startsWith("install/azure/versions/")) {
            assert.equal(
                response.headers.get("access-control-allow-origin"),
                "*",
            );
            if (file.endsWith(".json"))
                assert.ok(
                    response.headers
                        .get("content-type")
                        ?.startsWith("application/json"),
                );
        }
    } else {
        assert.ok(
            response.headers
                .get("content-security-policy")
                ?.includes("connect-src 'none'"),
        );
    }
}
const installRedirect = await fetch(new URL("install", origin), {
    redirect: "manual",
    signal: AbortSignal.timeout(15000),
});
assert.equal(installRedirect.status, 301);
assert.equal(installRedirect.headers.get("location"), "/install/");
assert.equal(
    new URL(installRedirect.headers.get("location"), origin).pathname,
    "/install/",
);
const health = await fetch(new URL("healthz", origin), {
    signal: AbortSignal.timeout(15000),
});
assert.equal(health.status, 200);
assert.equal((await health.text()).trim(), "ok");
const missing = await fetch(new URL("website-missing-file", origin), {
    signal: AbortSignal.timeout(15000),
});
assert.equal(missing.status, 404);
for (const path of [
    "install/source.json",
    "install/azure/source.json",
    "install/aws/releases.json",
    "install/gcp/releases.json",
]) {
    const response = await fetch(new URL(path, origin), {
        signal: AbortSignal.timeout(15000),
    });
    assert.equal(response.status, 404, `${path} must not be exposed`);
}
console.log(
    `Verified release bytes, security headers, health, and 404 responses at ${origin.origin}.`,
);
