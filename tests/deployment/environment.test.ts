import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import { environmentVariables } from "../../config/environment.mjs";

const identity = (name: string) => name.replaceAll("_", "").toUpperCase();

async function catalogs() {
    const [csharp, rust] = await Promise.all([
        readFile(
            "backend/src/Goblin.Contracts/Configuration/EnvironmentVariables.cs",
            "utf8",
        ),
        readFile("tools/goblinctl/src/environment.rs", "utf8"),
    ]);
    const entries = (source: string, pattern: RegExp) =>
        new Map(
            Array.from(source.matchAll(pattern), (match) => [
                identity(match[1]!),
                match[2]!,
            ]),
        );
    return [
        entries(csharp, /public const string (\w+) = "([^"]+)";/g),
        entries(rust, /pub const (\w+): &str = "([^"]+)";/g),
        new Map(
            Object.entries(environmentVariables).map(([key, entry]) => [
                identity(key),
                entry.name,
            ]),
        ),
    ];
}

test("environment metadata is discoverable and shared names agree across languages", async () => {
    const names = new Set<string>();
    for (const [key, entry] of Object.entries(environmentVariables)) {
        assert.equal(entry.name, key);
        assert.ok(
            !names.has(entry.name),
            `Duplicate environment name: ${entry.name}`,
        );
        names.add(entry.name);
        for (const field of [
            "purpose",
            "format",
            "fallback",
            "required",
        ] as const)
            assert.ok(entry[field].trim(), `Missing ${field}: ${key}`);
        assert.equal(typeof entry.sensitive, "boolean");
    }
    const languages = await catalogs();
    for (const [index, language] of languages.entries()) {
        for (const [key, name] of language) {
            for (const other of languages.slice(index + 1)) {
                if (other.has(key))
                    assert.equal(
                        other.get(key),
                        name,
                        `Shared environment name drifted: ${key}`,
                    );
            }
        }
    }
});

test("application and installer deployment names are present in the definition catalogs", async () => {
    const languages = await catalogs();
    const applicationNames = new Set(languages[0]!.values());
    const allNames = new Set(
        languages.flatMap((language) => [...language.values()]),
    );
    const sandbox = await readFile("deploy/auth/sandbox.yaml", "utf8");
    const overlay = await readFile(
        "deploy/azure/app/kustomization.yaml",
        "utf8",
    );
    for (const source of [sandbox, overlay]) {
        for (const match of source.matchAll(
            /(?:name:\s*|\[name=)(GOBLIN_[A-Z_]+)/g,
        ))
            assert.ok(
                applicationNames.has(match[1]!),
                `Undocumented application environment: ${match[1]}`,
            );
    }
    for (const file of [
        "Dockerfile",
        "deploy/azure/bootstrap.sh",
        "deploy/azure/setup/installer.sh",
        "deploy/azure/install-app.sh",
        "deploy/postgres/setup.sh",
    ]) {
        const source = await readFile(file, "utf8");
        // Skip replacement placeholders and lowercase shell-local variables.
        for (const match of source.matchAll(
            /(?<![A-Z_])\b(GOBLIN_[A-Z_]+|GOBLINCTL(?:_[A-Z_]+)?)\b/g,
        )) {
            if (source[match.index! - 1] === "_") continue;
            assert.ok(
                allNames.has(match[1]!),
                `${file}: undocumented environment ${match[1]}`,
            );
        }
    }
});
