import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import {
    access,
    appendFile,
    mkdir,
    readdir,
    readFile,
    writeFile,
} from "node:fs/promises";
import {
    basename,
    dirname,
    extname,
    resolve,
    posix,
    matchesGlob,
} from "node:path";
import { fileURLToPath } from "node:url";
import { environmentVariables as Env } from "../config/environment.mts";
import {
    changedFiles,
    comparison,
    loadPolicy,
    parsePolicy,
    select,
} from "./ci.mts";
import type { Policy } from "./ci.mts";

export type Mode = "ci-policy" | "documentation";
interface Document {
    path: string;
    text: string;
}
interface Candidate {
    path: string;
    reasons: string[];
}
interface Packet {
    version: 1;
    mode: Mode;
    base?: string;
    head: string;
    trigger?: PullRequestTrigger;
    changedFiles: string[];
    omittedFiles: number;
    diff: string;
    diffTruncated: boolean;
    candidates: Candidate[];
    omittedCandidates: number;
    inventory?: ReturnType<typeof inventory>;
    coverageProblems?: string[];
}
interface PullRequestTrigger {
    action: string;
    pull_request: {
        head: { ref: string; repo: { full_name: string } };
    };
}
const outputDirectory = ".artifacts/agentic-maintenance";
const limits = {
    files: 100,
    candidates: 12,
    diffBytes: 24000,
    packetBytes: 64000,
};
export function authorized(
    event: unknown,
    env: Record<string, string | undefined>,
    mode: Mode,
): boolean {
    if (
        env[Env.GITHUB_REPOSITORY.name] !== "jgador/goblin" ||
        env[Env.GITHUB_ACTOR.name] !== "jgador" ||
        env[Env.GITHUB_TRIGGERING_ACTOR.name] !== "jgador"
    )
        return false;
    const name = env[Env.GITHUB_EVENT_NAME.name];
    if (!event || typeof event !== "object") return false;
    if (name === "pull_request") {
        const pr = event as Partial<PullRequestTrigger>;
        const head = pr.pull_request?.head;
        if (!head) return false;
        return (
            mode === "documentation" &&
            ["opened", "synchronize", "reopened"].includes(pr.action ?? "") &&
            head?.repo?.full_name === env[Env.GITHUB_REPOSITORY.name] &&
            typeof head.ref === "string" &&
            head.ref.length > 0 &&
            !head.ref.startsWith("gh-aw/")
        );
    }
    if (
        name !== "workflow_dispatch" &&
        !(mode === "ci-policy" && name === "push")
    )
        return false;
    const ref =
        (event as Record<string, unknown>).ref ?? env[Env.GITHUB_REF.name];
    return (
        typeof ref === "string" &&
        ref.startsWith("refs/heads/") &&
        !ref.startsWith("refs/heads/gh-aw/") &&
        (event as Record<string, unknown>).deleted !== true
    );
}
function git(root: string, args: string[]): string {
    const result = spawnSync("git", args, {
        cwd: root,
        encoding: "utf8",
        maxBuffer: 32 * 1024 * 1024,
    });
    assert.ifError(result.error);
    assert.equal(
        result.status,
        0,
        "Maintenance Git operation failed: " + args[0],
    );
    return result.stdout;
}
function tracked(root: string, head?: string): string[] {
    return git(
        root,
        head
            ? ["ls-tree", "-r", "--name-only", "-z", head]
            : ["ls-files", "-z"],
    )
        .split("\0")
        .filter(Boolean);
}
function manifest(file: string, rule: Policy["groups"][string]): boolean {
    return (
        (rule.manifestExtensions ?? []).includes(extname(file).toLowerCase()) ||
        (rule.manifestFilenames ?? []).some(
            (name) => name.toLowerCase() === basename(file).toLowerCase(),
        )
    );
}
export function inventory(policy: Policy, files: string[]) {
    return Object.fromEntries(
        Object.entries(policy.groups)
            .filter(([, rule]) => rule.projectManifests !== undefined)
            .map(([id, rule]) => {
                const discovered = files
                    .filter((file) => manifest(file, rule))
                    .sort();
                const registered = rule.projectManifests ?? [];
                return [
                    id,
                    {
                        discovered,
                        added: discovered.filter(
                            (file) => !registered.includes(file),
                        ),
                        removed: registered
                            .filter((file) => !discovered.includes(file))
                            .sort(),
                    },
                ];
            }),
    );
}
export function proposedPolicy(policy: Policy, files: string[]): Policy {
    const proposal = structuredClone(policy);
    for (const [id, entry] of Object.entries(inventory(policy, files)))
        proposal.groups[id]!.projectManifests = entry.discovered;
    return proposal;
}
export async function coverageProblems(
    root: string,
    policy: Policy,
    files: string[],
): Promise<string[]> {
    const problems: string[] = [];
    const makefile = await readFile(resolve(root, "Makefile"), "utf8");
    for (const [id, rule] of Object.entries(policy.groups)) {
        if (!rule.projectManifests) continue;
        const commands = rule.commands.join("\n");
        const covered = new Set<string>();
        if (id === "csharp") {
            const solution = commands.includes("backend-test")
                ? makefile.match(/dotnet test ([^\s]+\.slnx)/)?.[1]
                : commands.match(/dotnet test ([^\s]+\.slnx)/)?.[1];
            if (solution && files.includes(solution)) {
                const text = await readFile(resolve(root, solution), "utf8");
                for (const project of text.matchAll(
                    /<Project\s+Path="([^"]+)"/g,
                ))
                    covered.add(
                        posix.normalize(
                            posix.join(posix.dirname(solution), project[1]!),
                        ),
                    );
            }
            for (const project of commands.matchAll(
                /dotnet (?:test|build) ([^\s]+\.csproj)/g,
            ))
                covered.add(project[1]!);
        } else if (
            id === "rust" &&
            files.includes("Cargo.toml") &&
            commands.includes("rust-test") &&
            makefile.includes("CARGO_ARGS ?= --workspace")
        ) {
            const text = await readFile(resolve(root, "Cargo.toml"), "utf8");
            const members = text.match(
                /\[workspace\]\s*\n\s*members\s*=\s*\[([\s\S]*?)\]/,
            )?.[1];
            if (members) {
                covered.add("Cargo.toml");
                for (const member of members.matchAll(/"([^"]+)"/g))
                    for (const file of files)
                        if (
                            manifest(file, rule) &&
                            matchesGlob(posix.dirname(file), member[1]!)
                        )
                            covered.add(file);
            }
        } else if (id === "javascript") {
            if (commands.includes("scripts-typecheck"))
                covered.add("package.json");
            if (commands.includes("frontend-build"))
                covered.add("frontend/package.json");
            for (const prefix of commands.matchAll(
                /npm --prefix ([^\s]+) run (?:check|test|build|typecheck)/g,
            ))
                covered.add(prefix[1]! + "/package.json");
        }
        for (const file of rule.projectManifests)
            if (!covered.has(file))
                problems.push(
                    rule.label +
                        " project is not proven included by its runner: " +
                        file,
                );
    }
    return problems;
}
export function ciRelevant(file: string): boolean {
    const name = basename(file).toLowerCase();
    return (
        /\.(csproj|sln|slnx|props|targets|toml|lock|yaml|yml)$/.test(name) ||
        /^(makefile(?:\.dev)?|package\.json|package-lock\.json|global\.json|nuget\.config|rust-toolchain(?:\.toml)?|dependencies\.(?:toml|lock\.json)|tsconfig.*\.json|jsconfig.*\.json|go\.(?:mod|sum)|pyproject\.toml)$/.test(
            name,
        ) ||
        [
            "config/ci-verification.json",
            "scripts/ci.mts",
            "scripts/ci.test.mts",
            "tests/fixtures/fake-codex.mts",
        ].includes(file) ||
        /(?:^|\/)(?:scripts\/.*\.(?:mts|ts|js|sh)|\.cargo\/config(?:\.toml)?)$/.test(
            file,
        )
    );
}
export function documentationTarget(file: string): boolean {
    return (
        /^docs\/.*\.md$/.test(file) ||
        file === "README.md" ||
        /^(?:backend|frontend|tools|website|deploy|tests|scripts)\/(?:.*\/)?README\.md$/.test(
            file,
        )
    );
}
export function documentCandidates(
    files: string[],
    documents: Document[],
): Candidate[] {
    const changed = files.filter(
        (file) =>
            !file.endsWith(".lock.yml") &&
            !file.startsWith(".agents/") &&
            !file.startsWith(".artifacts/"),
    );
    const candidates = new Map<string, Set<string>>();
    function add(path: string, reason: string) {
        const reasons = candidates.get(path) ?? new Set<string>();
        if (reasons.size < 3) reasons.add(reason);
        candidates.set(path, reasons);
    }
    for (const doc of documents) {
        if (!documentationTarget(doc.path)) continue;
        for (const file of changed) {
            if (file === doc.path) {
                add(doc.path, "Document changed");
                continue;
            }
            if (file.endsWith(".md")) {
                if (
                    doc.text.includes(file) ||
                    doc.text.includes(basename(file))
                )
                    add(doc.path, "References changed document " + file);
                continue;
            }
            const stem = basename(file, extname(file));
            if (
                doc.text.includes(file) ||
                (stem.length >= 5 && doc.text.includes(basename(file)))
            )
                add(doc.path, "References " + file);
            const folder = dirname(doc.path);
            if (
                basename(doc.path) === "README.md" &&
                folder !== "." &&
                file.startsWith(folder + "/")
            )
                add(doc.path, "Describes changed subtree");
            for (const [input, target] of [
                [
                    /^(?:\.github\/|config\/ci-verification|scripts\/ci\.)/,
                    "docs/github-actions.md",
                ],
                [
                    /^(?:Makefile(?:\.dev)?$|.*(?:package\.json|tsconfig.*\.json|\.csproj|\.slnx|Cargo\.toml)$)/,
                    "docs/repository-layout.md",
                ],
                [/^backend\/database\/|\.sql$/, "docs/database.md"],
                [
                    /(?:^|\/)(?:environment\.(?:mts|rs)|EnvironmentVariables\.cs)$/,
                    "docs/environment-variables.md",
                ],
                [
                    /\.rs$|(?:^|\/)(?:Cargo\.(?:toml|lock)|rust-toolchain\.toml)$/,
                    "docs/rust-development.md",
                ],
                [
                    /^scripts\/release-|^tools\/xtask\/src\/.*release/,
                    "docs/releases.md",
                ],
            ] as const)
                if (doc.path === target && input.test(file))
                    add(doc.path, "Owns changed behavior");
        }
    }
    // If no explicit reference identifies a behavioral change, ask the agent to
    // route it using the documentation index instead of silently skipping it.
    if (
        changed.some((file) => !file.endsWith(".md")) &&
        candidates.size === 0 &&
        documents.some((doc) => doc.path === "docs/README.md")
    )
        add("docs/README.md", "Route changed behavior to its owning guide");
    return [...candidates]
        .map(([path, reasons]) => ({ path, reasons: [...reasons] }))
        .sort(
            (a, b) =>
                b.reasons.length - a.reasons.length ||
                a.path.localeCompare(b.path),
        );
}
export function boundedDiff(diff: string): {
    diff: string;
    diffTruncated: boolean;
} {
    const bytes = Buffer.from(diff);
    return {
        diff: bytes.subarray(0, limits.diffBytes).toString("utf8"),
        diffTruncated: bytes.length > limits.diffBytes,
    };
}
export async function prepare(
    root: string,
    mode: Mode,
    event: unknown,
    env = process.env,
): Promise<boolean> {
    assert.ok(
        authorized(event, env, mode),
        "Maintenance is restricted to owner-initiated events supported by this workflow",
    );
    const range = comparison(
        env[Env.GITHUB_EVENT_NAME.name]!,
        event,
        env[Env.GITHUB_SHA.name]!,
    );
    if (range.mergeBase) {
        range.base = git(root, ["merge-base", range.base!, range.head]).trim();
        range.mergeBase = false;
    }
    // Keep only the PR metadata needed to recheck publication authorization.
    // Titles, bodies, comments, and other payload fields are not copied into the packet.
    const pr =
        env[Env.GITHUB_EVENT_NAME.name] === "pull_request"
            ? (event as PullRequestTrigger)
            : undefined;
    const files = tracked(root, range.head);
    const changed = changedFiles(root, range);
    const policy = await loadPolicy(root);
    const projects = inventory(policy, files);
    const drift = Object.values(projects).some(
        (entry) => entry.added.length || entry.removed.length,
    );
    const candidates =
        mode === "documentation"
            ? documentCandidates(
                  changed,
                  await Promise.all(
                      files.filter(documentationTarget).map(async (path) => ({
                          path,
                          text: git(root, ["show", range.head + ":" + path]),
                      })),
                  ),
              )
            : [];
    const selection =
        mode === "ci-policy"
            ? select(policy, changed, false, files)
            : undefined;
    const coverageRequired = (selection?.errors.length ?? 0) > 0;
    const relevant = changed.filter(
        mode === "ci-policy"
            ? (file) => coverageRequired || ciRelevant(file)
            : (file) =>
                  !file.endsWith(".lock.yml") && !file.startsWith(".agents/"),
    );
    const hasWork =
        mode === "ci-policy"
            ? drift || relevant.length > 0
            : candidates.length > 0;
    const packet: Packet = {
        version: 1,
        mode,
        base: range.base,
        head: range.head,
        ...(pr
            ? {
                  trigger: {
                      action: pr.action,
                      pull_request: {
                          head: {
                              ref: pr.pull_request.head.ref,
                              repo: {
                                  full_name:
                                      pr.pull_request.head.repo.full_name,
                              },
                          },
                      },
                  },
              }
            : {}),
        changedFiles: relevant.slice(0, limits.files),
        omittedFiles: Math.max(0, relevant.length - limits.files),
        ...boundedDiff(
            range.base
                ? git(root, [
                      "diff",
                      "--no-ext-diff",
                      "--no-renames",
                      "--unified=3",
                      range.base,
                      range.head,
                      "--",
                      ...relevant.slice(0, limits.files),
                  ])
                : "Full snapshot; inspect the listed current inputs.",
        ),
        candidates: candidates.slice(0, limits.candidates),
        omittedCandidates: Math.max(0, candidates.length - limits.candidates),
        ...(mode === "ci-policy" ? { inventory: projects } : {}),
        ...(mode === "ci-policy"
            ? {
                  coverageProblems: [
                      ...selection!.errors,
                      ...(await coverageProblems(
                          root,
                          proposedPolicy(policy, files),
                          files,
                      )),
                  ],
              }
            : {}),
    };
    const directory = resolve(root, outputDirectory, "context");
    await mkdir(directory, { recursive: true });
    const json = JSON.stringify(packet, null, 2) + "\n";
    assert.ok(
        Buffer.byteLength(json) <= limits.packetBytes,
        "Change packet exceeds the context limit; split the change or narrow the manual review",
    );
    await writeFile(resolve(directory, "packet.json"), json);
    if (mode === "ci-policy")
        await writeFile(
            resolve(directory, "proposed-policy.json"),
            JSON.stringify(proposedPolicy(policy, files), null, 2) + "\n",
        );
    if (env[Env.GITHUB_OUTPUT.name])
        await appendFile(
            env[Env.GITHUB_OUTPUT.name]!,
            "has_work=" + hasWork + "\n",
        );
    const message = hasWork
        ? `Prepared ${mode}: ${relevant.length} relevant file(s), ${candidates.length} documentation candidate(s). No inference in this job.`
        : `No ${mode} maintenance candidates; the AI jobs will skip.`;
    console.log(message);
    if (env[Env.GITHUB_STEP_SUMMARY.name])
        await appendFile(env[Env.GITHUB_STEP_SUMMARY.name]!, message + "\n");
    return hasWork;
}
function localLinkTargets(file: string, text: string): string[] {
    const links = [
        ...text.matchAll(/\]\((<[^>]+>|[^\s)]+)(?:\s+"[^"]*")?\)/g),
        ...text.matchAll(
            /^ {0,3}\[[^\]]+\]:\s*(<[^>]+>|[^\s]+)(?:\s+"[^"]*")?$/gm,
        ),
    ];
    return links.flatMap((link) => {
        const target = link[1]!.replace(/^<|>$/g, "");
        if (/^(?:[a-z]+:|#|\/)/i.test(target)) return [];
        const path = decodeURIComponent(target.split("#")[0]!);
        return path
            ? [posix.normalize(posix.join(posix.dirname(file), path))]
            : [];
    });
}
export async function validate(root: string, mode: Mode): Promise<void> {
    const packet = JSON.parse(
        await readFile(resolve(root, outputDirectory, "packet.json"), "utf8"),
    ) as Packet;
    assert.equal(packet.mode, mode);
    assert.match(packet.head, /^[a-f0-9]{40}$/);
    const changed = git(root, ["diff", "--name-only", "-z", packet.head])
        .split("\0")
        .filter(Boolean);
    // gh-aw stages edits when packaging its bundle; HEAD comparison catches both.
    const untracked = git(root, [
        "ls-files",
        "--others",
        "--exclude-standard",
        "-z",
    ])
        .split("\0")
        .filter(Boolean);
    for (const file of [...changed, ...untracked])
        assert.ok(
            mode === "ci-policy"
                ? file === "config/ci-verification.json"
                : documentationTarget(file),
            "Maintenance patch includes an unauthorized file",
        );
    if (mode === "ci-policy") {
        const policy = parsePolicy(
            JSON.parse(
                await readFile(
                    resolve(root, "config/ci-verification.json"),
                    "utf8",
                ),
            ),
        );
        const projects = inventory(policy, tracked(root));
        assert.ok(
            Object.values(projects).every(
                (entry) => !entry.added.length && !entry.removed.length,
            ),
            "Project inventory still differs from the CI policy",
        );
        const coverage = await coverageProblems(root, policy, tracked(root));
        assert.deepEqual(
            coverage,
            [],
            "Project runner coverage is incomplete: " + coverage.join("; "),
        );
        const before = parsePolicy(
            JSON.parse(
                git(root, [
                    "show",
                    packet.head + ":config/ci-verification.json",
                ]),
            ),
        );
        assert.deepEqual(
            Object.keys(policy.groups),
            Object.keys(before.groups),
            "New verification groups require an owning workflow job",
        );
        assert.deepEqual(
            policy.unsupportedLanguages,
            before.unsupportedLanguages,
            "Unsupported-language guardrails must remain intact",
        );
        assert.deepEqual(
            policy.documentation,
            before.documentation,
            "Documentation skips must remain intact",
        );
        assert.deepEqual(
            policy.fullVerification,
            before.fullVerification,
            "Conservative fallbacks must remain intact",
        );
        for (const [id, rule] of Object.entries(before.groups)) {
            assert.equal(
                policy.groups[id]!.projectManifests !== undefined,
                rule.projectManifests !== undefined,
                "Project registration guardrails must remain intact",
            );
            assert.ok(
                rule.requires.every((dep) =>
                    policy.groups[id]!.requires.includes(dep),
                ),
                "Cannot remove verification dependencies",
            );
            for (const key of [
                "extensions",
                "filenames",
                "filenamePatterns",
                "pathSuffixes",
                "manifestExtensions",
                "manifestFilenames",
                "dependentPaths",
                "dependentGroups",
            ] as const)
                assert.ok(
                    (rule[key] ?? []).every((item) =>
                        (policy.groups[id]![key] ?? []).includes(item),
                    ),
                    "Cannot remove existing trigger coverage",
                );
            assert.ok(
                policy.groups[id]!.commands.includes("git diff --exit-code"),
                "Generated-file drift verification must remain intact",
            );
        }
    } else {
        for (const file of [...changed, ...untracked]) {
            // Check relative links only in affected documents. Existing old broken
            // links are not silently excused after rewriting a document.
            let text: string;
            try {
                text = await readFile(resolve(root, file), "utf8");
            } catch {
                continue;
            }
            for (const local of localLinkTargets(file, text)) {
                assert.ok(
                    !local.startsWith("../"),
                    "Broken documentation link in " + file,
                );
                await access(resolve(root, local)).catch(() => {
                    throw new Error("Broken documentation link in " + file);
                });
            }
            assert.ok(
                !/[ \t]+$/m.test(text),
                "Trailing whitespace in documentation",
            );
        }
        for (const removed of changed.filter((file) =>
            git(root, [
                "diff",
                "--diff-filter=D",
                "--name-only",
                packet.head,
                "--",
                file,
            ]).trim(),
        ))
            for (const file of tracked(root).filter(documentationTarget)) {
                let text: string;
                try {
                    text = await readFile(resolve(root, file), "utf8");
                } catch {
                    continue;
                }
                for (const target of localLinkTargets(file, text))
                    assert.notEqual(
                        target,
                        removed,
                        "Deleted document retains incoming links",
                    );
            }
    }
    git(root, ["diff", "--check", packet.head]);
    console.log("Validated the scoped " + mode + " patch.");
}
export async function snapshotProposal(
    root: string,
    mode: Mode,
    env = process.env,
): Promise<boolean> {
    const packet = JSON.parse(
        await readFile(resolve(root, outputDirectory, "packet.json"), "utf8"),
    ) as Packet;
    assert.ok(
        authorized(packet.trigger ?? {}, env, mode),
        "Proposal snapshots require an owner-initiated run",
    );
    await validate(root, mode);
    assert.equal(
        git(root, ["rev-parse", "HEAD"]).trim(),
        packet.head,
        "Only the scoped snapshot command may commit maintenance edits",
    );
    const files = [
        ...git(root, ["diff", "--name-only", "-z", packet.head]).split("\0"),
        ...git(root, [
            "ls-files",
            "--others",
            "--exclude-standard",
            "-z",
        ]).split("\0"),
    ].filter(Boolean);
    if (!files.length) {
        console.log(
            "No maintenance edits to snapshot; finish without a pull request.",
        );
        return false;
    }
    const branch = "gh-aw/" + mode + "-" + packet.head.slice(0, 12);
    if (git(root, ["branch", "--show-current"]).trim() !== branch)
        git(root, ["switch", "-c", branch]);
    git(root, ["add", "--", ...files]);
    const message = resolve(root, outputDirectory, "proposal-message.txt");
    await writeFile(
        message,
        (mode === "documentation"
            ? "docs: repair stale documentation"
            : "ci: update verification policy") +
            "\n\nValidation: Scoped maintenance validation passed before packaging.\n\nCo-authored-by: codex <242516109+Codex@users.noreply.github.com>\n",
    );
    git(root, ["commit", "-F", message]);
    console.log(
        "Prepared local proposal snapshot on " +
            branch +
            ". Use this branch in the draft PR safe output.",
    );
    return true;
}
export async function validateProposal(
    root: string,
    mode: Mode,
    artifacts: string,
): Promise<void> {
    await validate(root, mode);
    const output = JSON.parse(
        await readFile(resolve(artifacts, "agent_output.json"), "utf8"),
    );
    assert.ok(Array.isArray(output.items), "Missing safe-output items");
    const requests = output.items.filter(
        (item: { type?: string }) => item.type === "create_pull_request",
    );
    assert.ok(requests.length <= 1, "Only one proposal is allowed");
    if (!requests.length) return;
    const packet = JSON.parse(
        await readFile(resolve(root, outputDirectory, "packet.json"), "utf8"),
    ) as Packet;
    const head = git(root, ["rev-parse", "HEAD"]).trim();
    assert.notEqual(
        head,
        packet.head,
        "Draft PR has no committed maintenance snapshot",
    );
    assert.equal(
        requests[0].branch,
        git(root, ["branch", "--show-current"]).trim(),
        "Draft PR branch differs from the snapshot",
    );
    assert.equal(
        git(root, ["status", "--porcelain"]).trim(),
        "",
        "Uncommitted maintenance edits would be omitted from the draft PR",
    );
    git(root, ["merge-base", "--is-ancestor", packet.head, head]);
    const bundles = (await readdir(artifacts)).filter(
        (file) => file.startsWith("aw-") && file.endsWith(".bundle"),
    );
    assert.equal(
        bundles.length,
        1,
        "Draft PR requires exactly one proposal bundle",
    );
    const bundle = resolve(artifacts, bundles[0]!);
    assert.equal(
        git(root, ["bundle", "list-heads", bundle]).trim(),
        head + " refs/heads/" + requests[0].branch,
        "Proposal bundle omits the validated maintenance snapshot",
    );
    git(root, ["bundle", "verify", bundle]);
    console.log("Validated the committed maintenance proposal bundle.");
}
async function main() {
    const [command, rawMode, artifacts] = process.argv.slice(2);
    assert.ok(
        rawMode === "ci-policy" || rawMode === "documentation",
        "Expected ci-policy or documentation mode",
    );
    if (command === "prepare") {
        const path = process.env[Env.GITHUB_EVENT_PATH.name];
        assert.ok(path, "Missing GitHub event payload");
        await prepare(
            process.cwd(),
            rawMode,
            JSON.parse(await readFile(path, "utf8")),
        );
    } else if (command === "validate") await validate(process.cwd(), rawMode);
    else if (command === "snapshot")
        await snapshotProposal(process.cwd(), rawMode);
    else if (command === "validate-proposal") {
        assert.ok(artifacts, "Expected the gh-aw artifact directory");
        await validateProposal(process.cwd(), rawMode, artifacts);
    } else
        throw new Error(
            "Usage: node scripts/agentic-maintenance.mts prepare|validate|snapshot|validate-proposal ci-policy|documentation [artifacts]",
        );
}
if (
    process.argv[1] &&
    resolve(process.argv[1]) === fileURLToPath(import.meta.url)
)
    main().catch((error) => {
        console.error(
            error instanceof Error ? error.message : "Maintenance failed",
        );
        process.exitCode = 1;
    });
