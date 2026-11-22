import { createHash } from "node:crypto";
import { existsSync, readFileSync, writeFileSync, mkdirSync } from "node:fs";
import { execFileSync } from "node:child_process";
import { resolve } from "node:path";
import { setTimeout as sleep } from "node:timers/promises";
import { fileURLToPath } from "node:url";

// Retry only temporary download failures, with a deadline covering the body too.
// Bad artifacts and permanent HTTP errors must fail without further attempts.
export async function downloadArchive(
    url,
    checksum,
    { request = fetch, wait = sleep } = {},
) {
    const attempts = 4;
    for (let attempt = 1; attempt <= attempts; attempt++) {
        let archive;
        let failure;
        try {
            const response = await request(url, {
                signal: AbortSignal.timeout(60_000),
            });
            if (response.ok) {
                archive = Buffer.from(await response.arrayBuffer());
            } else {
                await response.body?.cancel();
                failure = new Error(
                    `GitHub CLI download failed (HTTP ${response.status}, attempt ${attempt}/${attempts})`,
                );
                if (![408, 429, 500, 502, 503, 504].includes(response.status))
                    throw failure;
            }
        } catch (error) {
            if (
                !(error instanceof TypeError) &&
                !["TimeoutError", "AbortError"].includes(error?.name)
            )
                throw error;
            failure = new Error(
                `GitHub CLI download failed (${error.name}, attempt ${attempt}/${attempts})`,
                { cause: error },
            );
        }
        if (archive) {
            if (createHash("sha256").update(archive).digest("hex") !== checksum)
                throw new Error("GitHub CLI checksum mismatch");
            return archive;
        }
        if (attempt === attempts) throw failure;
        const delay = 1000 * 2 ** (attempt - 1);
        console.warn(`${failure.message}; retrying in ${delay / 1000}s`);
        await wait(delay);
    }
}

async function install() {
    const version = "2.101.0";
    const arch = { x64: "amd64", arm64: "arm64" }[process.arch];
    const hashes = {
        amd64: "9bca2d1c16825f109907a23307628a2f0698fbf99662b73a5cf0b020293072b8",
        arm64: "b57e8063f18862647c9d22727c32e9da1b963f8bf9db648fe123a6975695640f",
    };
    if (!arch) throw new Error("Unsupported GitHub CLI architecture");
    // The content hash separates versions and architectures in the BuildKit cache.
    // Verify cached bytes too; a corrupt cache must never become an executable.
    const cacheDirectory = "/tmp/github-cli-cache";
    const archivePath = `${cacheDirectory}/${hashes[arch]}.tar.gz`;
    const matches = (archive) =>
        createHash("sha256").update(archive).digest("hex") === hashes[arch];
    let archive = existsSync(archivePath) ? readFileSync(archivePath) : null;
    if (!archive || !matches(archive)) {
        archive = await downloadArchive(
            `https://github.com/cli/cli/releases/download/v${version}/gh_${version}_linux_${arch}.tar.gz`,
            hashes[arch],
        );
        mkdirSync(cacheDirectory, { recursive: true });
        writeFileSync(archivePath, archive);
    }
    mkdirSync("/githubcli");
    execFileSync("tar", [
        "-xzf",
        archivePath,
        "--strip-components=1",
        "-C",
        "/githubcli",
    ]);
}

if (
    process.argv[1] &&
    resolve(process.argv[1]) === fileURLToPath(import.meta.url)
)
    await install();
