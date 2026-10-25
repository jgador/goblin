import assert from "node:assert/strict";
import { glob, readFile, stat } from "node:fs/promises";
import { basename, dirname, relative, resolve, sep } from "node:path";
import test from "node:test";

test("backend embedded resources exist at their expected paths in the Docker publish stage", async () => {
    const dockerfile = await readFile("Dockerfile", "utf8");
    const stages = dockerfile
        .split("FROM dotnet-base AS restore")[1]
        ?.split("FROM dotnet-base AS database-restore")[0];
    assert.ok(stages, "Cannot locate the web restore and publish stages");
    const copies = await Promise.all(
        [...stages.matchAll(/^COPY (?!-)(\S+) (\S+)$/gm)].map(
            async ([, source, destination]) => ({
                source: resolve(source!),
                destination: resolve(destination!),
                directory: (await stat(source!)).isDirectory(),
                destinationDirectory: destination!.endsWith("/"),
            }),
        ),
    );
    let resources = 0;
    for await (const project of glob("backend/src/**/*.csproj")) {
        const xml = await readFile(project, "utf8");
        for (const [, include] of xml.matchAll(
            /<EmbeddedResource\s+Include="([^"]+)"/g,
        )) {
            const resource = resolve(dirname(project), include!);
            await stat(resource);
            resources++;
            assert.ok(
                copies.some((copy) => {
                    if (
                        copy.directory &&
                        resource.startsWith(copy.source + sep)
                    )
                        return (
                            resolve(
                                copy.destination,
                                relative(copy.source, resource),
                            ) === resource
                        );
                    if (resource !== copy.source) return false;
                    return (
                        (copy.destinationDirectory
                            ? resolve(copy.destination, basename(resource))
                            : copy.destination) === resource
                    );
                }),
                `${project} embeds ${include}, but Docker does not copy it to the expected publish path`,
            );
        }
    }
    assert.ok(resources > 0, "No embedded resources were checked");
});

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
