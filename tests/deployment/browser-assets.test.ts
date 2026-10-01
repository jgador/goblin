import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import test from "node:test";

test("every browser module dependency has a public static asset route", async () => {
    const source = await readFile(
        "backend/src/Goblin.Web/Http/StaticAssets.cs",
        "utf8",
    );
    const modules = new Map(
        [
            ...source.matchAll(
                /\("([^"]+\.js)", "([^"]+\.js)", "text\/javascript; charset=utf-8"\)/g,
            ),
        ].map((match) => [match[1]!, match[2]!]),
    );
    assert.ok(modules.has("/work/app.js"));
    assert.ok(modules.has("/app.js"));
    for (const [route, file] of modules) {
        const code = await readFile(resolve("frontend/dist", file), "utf8");
        for (const match of code.matchAll(
            /(?:\bfrom\s*|\bimport\s*)["']([^"']+)["']/g,
        )) {
            const dependency = new URL(match[1]!, `https://goblin.test${route}`)
                .pathname;
            assert.ok(
                modules.has(dependency),
                `${route} imports ${dependency} without a public asset route`,
            );
        }
    }
});
