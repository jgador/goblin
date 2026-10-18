import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { appendFile, readFile } from "node:fs/promises";
import { basename, extname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { environmentVariables as Env } from "../config/environment.mts";

interface Matchers {
    extensions: string[];
    filenames: string[];
    filenamePatterns?: string[];
    pathSuffixes?: string[];
}
interface Group extends Matchers {
    label: string;
    requires: string[];
    commands: string[];
    projectManifests?: string[];
    manifestExtensions?: string[];
    manifestFilenames?: string[];
    dependentPaths?: string[];
    dependentGroups?: string[];
}
export interface Policy {
    version: number;
    documentation: Matchers;
    fullVerification: Matchers;
    unsupportedLanguages: Record<string, Matchers>;
    groups: Record<string, Group>;
}
export interface Selection {
    selected: boolean;
    reasons: string[];
}
export interface Plan {
    version: number;
    event: string;
    comparison: string;
    changedFiles: number;
    documentationOnly: boolean;
    groups: Record<string, Selection>;
    errors: string[];
}
interface Comparison {
    base?: string;
    head: string;
    mergeBase: boolean;
    full: boolean;
}

function object(value: unknown, label: string): Record<string, unknown> {
    assert.ok(
        value !== null && typeof value === "object" && !Array.isArray(value),
        label + " must be an object",
    );
    return value as Record<string, unknown>;
}
function strings(value: unknown, label: string): string[] {
    assert.ok(
        Array.isArray(value) &&
            value.every((item) => typeof item === "string" && item.length > 0),
        label + " must be a string array",
    );
    return value;
}
function matchers(value: unknown, label: string): Matchers {
    const entry = object(value, label);
    const result: Matchers = {
        extensions: strings(entry.extensions, label + ".extensions"),
        filenames: strings(entry.filenames, label + ".filenames"),
    };
    assert.ok(
        result.extensions.every((item) => /^\.[a-z0-9]+$/.test(item)),
        label + " has an invalid extension",
    );
    assert.ok(
        result.filenames.every((item) => !/[\\/\r\n]/.test(item)),
        label + " has an invalid filename",
    );
    for (const key of ["filenamePatterns", "pathSuffixes"] as const)
        if (entry[key] !== undefined)
            result[key] = strings(entry[key], label + "." + key);
    return result;
}
export function parsePolicy(value: unknown): Policy {
    const entry = object(value, "CI policy");
    assert.equal(entry.version, 1, "Unsupported CI policy version");
    const groups: Record<string, Group> = {};
    for (const [id, raw] of Object.entries(object(entry.groups, "groups"))) {
        assert.ok(
            /^[a-z]+(?:_[a-z]+)?$/.test(id) &&
                !["changes", "test_results", "__proto__"].includes(id),
            "Invalid verification group: " + id,
        );
        const group = object(raw, id);
        assert.ok(
            typeof group.label === "string" && group.label.length > 0,
            id + " requires a label",
        );
        groups[id] = {
            ...matchers(raw, id),
            label: group.label,
            requires: strings(group.requires, id + ".requires"),
            commands: strings(group.commands, id + ".commands"),
        };
        for (const key of [
            "projectManifests",
            "manifestExtensions",
            "manifestFilenames",
            "dependentPaths",
            "dependentGroups",
        ] as const)
            if (group[key] !== undefined)
                groups[id]![key] = strings(group[key], id + "." + key);
        assert.ok(
            groups[id]!.commands.length > 0,
            id + " requires verification commands",
        );
    }
    assert.ok(
        Object.keys(groups).length > 0,
        "At least one verification group is required",
    );
    for (const [id, group] of Object.entries(groups))
        assert.ok(
            [...group.requires, ...(group.dependentGroups ?? [])].every(
                (required) =>
                    Object.hasOwn(groups, required) && required !== id,
            ),
            id + " has an unknown or self dependency",
        );
    const unsupportedLanguages: Record<string, Matchers> = {};
    for (const [language, raw] of Object.entries(
        object(entry.unsupportedLanguages, "unsupportedLanguages"),
    ))
        unsupportedLanguages[language] = matchers(raw, language);
    return {
        version: 1,
        documentation: matchers(entry.documentation, "documentation"),
        fullVerification: matchers(entry.fullVerification, "fullVerification"),
        unsupportedLanguages,
        groups,
    };
}
export async function loadPolicy(root: string): Promise<Policy> {
    return parsePolicy(
        JSON.parse(
            await readFile(
                resolve(root, "config/ci-verification.json"),
                "utf8",
            ),
        ),
    );
}
function matches(file: string, rule: Matchers): boolean {
    const name = basename(file).toLowerCase();
    return (
        rule.extensions.includes(extname(name)) ||
        rule.filenames.some((item) => item.toLowerCase() === name) ||
        (rule.pathSuffixes ?? []).some(
            (suffix) => file === suffix || file.endsWith("/" + suffix),
        ) ||
        (rule.filenamePatterns ?? []).some((pattern) => {
            const escaped = pattern
                .toLowerCase()
                .split("*")
                .map((part) => part.replace(/[.*+?^$()|[\]\\{}]/g, "\\$&"))
                .join(".*");
            return new RegExp("^" + escaped + "$").test(name);
        })
    );
}
export function select(
    policy: Policy,
    files: string[],
    full = false,
    currentFiles?: string[],
): Plan {
    const groups: Record<string, Selection> = Object.fromEntries(
        Object.keys(policy.groups).map((id) => [
            id,
            { selected: false, reasons: [] },
        ]),
    );
    const errors: string[] = [];
    function enable(id: string, reason: string) {
        const group = groups[id]!;
        group.selected = true;
        // Bound the Actions output while retaining selection and representative reasons.
        if (!group.reasons.includes(reason) && group.reasons.length < 8)
            group.reasons.push(reason);
    }
    function enableAll(reason: string) {
        for (const id of Object.keys(groups)) enable(id, reason);
    }
    const changed = [...new Set(files)];
    const present =
        currentFiles === undefined ? undefined : new Set(currentFiles);
    let documentation = 0;
    for (const file of changed) {
        assert.ok(
            file.length > 0 &&
                !file.startsWith("/") &&
                !file.split("/").includes(".."),
            "Invalid changed-file path",
        );
        const matched = Object.entries(policy.groups).filter(([, rule]) =>
            matches(file, rule),
        );
        if (matched.length) {
            for (const [id] of matched)
                enable(id, "Matched " + file.slice(0, 200));
            for (const [, rule] of matched) {
                if ((rule.dependentPaths ?? []).includes(file))
                    for (const required of rule.dependentGroups ?? [])
                        enable(
                            required,
                            "Consumes shared input " + file.slice(0, 200),
                        );
                const manifest =
                    (rule.manifestExtensions ?? []).includes(
                        extname(file).toLowerCase(),
                    ) ||
                    (rule.manifestFilenames ?? []).some(
                        (name) =>
                            name.toLowerCase() === basename(file).toLowerCase(),
                    );
                if (
                    manifest &&
                    (present?.has(file) ?? true) &&
                    !(rule.projectManifests ?? []).includes(file)
                ) {
                    if (errors.length < 8)
                        errors.push(
                            "Unregistered " +
                                rule.label +
                                " project " +
                                file.slice(0, 200) +
                                ". Add its build/test command and project manifest to the CI policy.",
                        );
                    enableAll(
                        "Unregistered project: full existing verification",
                    );
                }
            }
            continue;
        }
        const unsupported = Object.entries(policy.unsupportedLanguages).find(
            ([, rule]) => matches(file, rule),
        );
        if (unsupported) {
            if ((present?.has(file) ?? true) && errors.length < 8)
                errors.push(
                    unsupported[0] +
                        " verification is not configured for " +
                        file.slice(0, 200) +
                        ". Register its triggers, commands, toolchain, and CI job.",
                );
            enableAll("Unconfigured language: full existing verification");
        } else if (matches(file, policy.fullVerification)) {
            enableAll("Shared input " + file.slice(0, 200));
        } else if (matches(file, policy.documentation)) {
            if (file.startsWith(".github/workflows/"))
                enableAll("Executable agentic workflow " + file.slice(0, 200));
            else documentation++;
        } else {
            enableAll("Unclassified input " + file.slice(0, 200));
        }
    }
    if (full) enableAll("Full verification requested");
    let updated = true;
    while (updated) {
        updated = false;
        for (const [id, rule] of Object.entries(policy.groups)) {
            if (!groups[id]!.selected) continue;
            for (const required of rule.requires) {
                if (!groups[required]!.selected) updated = true;
                enable(required, "Required by " + rule.label);
            }
        }
    }
    return {
        version: 1,
        event: "",
        comparison: "",
        changedFiles: changed.length,
        documentationOnly:
            !full && changed.length > 0 && documentation === changed.length,
        groups,
        errors,
    };
}
function sha(value: unknown): string {
    assert.ok(
        typeof value === "string" && /^[a-f0-9]{40}$/.test(value),
        "Missing or invalid comparison commit SHA",
    );
    return value;
}
export function comparison(
    event: string,
    payload: unknown,
    currentSha: string,
): Comparison {
    const value = object(payload, "GitHub event");
    if (event === "workflow_dispatch")
        return { head: sha(currentSha), mergeBase: false, full: true };
    if (event === "pull_request") {
        const pr = object(value.pull_request, "pull_request");
        return {
            base: sha(object(pr.base, "pull_request.base").sha),
            head: sha(object(pr.head, "pull_request.head").sha),
            mergeBase: true,
            full: false,
        };
    }
    if (event === "merge_group") {
        const merge = object(value.merge_group, "merge_group");
        return {
            base: sha(merge.base_sha),
            head: sha(merge.head_sha),
            mergeBase: false,
            full: false,
        };
    }
    if (event === "push") {
        const before = sha(value.before),
            after = sha(value.after);
        assert.notEqual(
            after,
            "0".repeat(40),
            "A deleted branch cannot be verified",
        );
        return {
            base: before === "0".repeat(40) ? undefined : before,
            head: after,
            mergeBase: false,
            full: before === "0".repeat(40),
        };
    }
    throw new Error("Unsupported CI event: " + event);
}
function git(root: string, args: string[]): string {
    const result = spawnSync("git", args, {
        cwd: root,
        encoding: "utf8",
        maxBuffer: 32 * 1024 * 1024,
    });
    assert.ifError(result.error);
    assert.equal(result.status, 0, "Git comparison failed: git " + args[0]);
    return result.stdout;
}
export function changedFiles(root: string, range: Comparison): string[] {
    git(root, ["cat-file", "-e", range.head + "^{commit}"]);
    if (range.base) git(root, ["cat-file", "-e", range.base + "^{commit}"]);
    if (range.full)
        return git(root, ["ls-tree", "-r", "--name-only", "-z", range.head])
            .split("\0")
            .filter(Boolean);
    assert.ok(range.base, "Comparison requires a base commit");
    // No rename detection: both sides must be classified when source becomes .md.
    const revisions = range.mergeBase
        ? [range.base + "..." + range.head]
        : [range.base, range.head];
    return git(root, [
        "diff",
        "--no-ext-diff",
        "--no-renames",
        "--name-only",
        "-z",
        ...revisions,
        "--",
    ])
        .split("\0")
        .filter(Boolean);
}
export function parsePlan(policy: Policy, value: unknown): Plan {
    const plan = object(value, "Selection plan");
    assert.equal(plan.version, 1, "Unsupported selection plan version");
    const rawGroups = object(plan.groups, "Selection groups");
    assert.deepEqual(
        Object.keys(rawGroups).sort(),
        Object.keys(policy.groups).sort(),
        "Selection groups do not match configured jobs",
    );
    const groups: Record<string, Selection> = {};
    for (const [id, raw] of Object.entries(rawGroups)) {
        const group = object(raw, id);
        assert.equal(
            typeof group.selected,
            "boolean",
            "Invalid selection for " + id,
        );
        groups[id] = {
            selected: group.selected as boolean,
            reasons: strings(group.reasons, id + ".reasons"),
        };
        assert.ok(
            !groups[id]!.selected || groups[id]!.reasons.length > 0,
            "Selected " + id + " requires a reason",
        );
    }
    assert.equal(typeof plan.event, "string");
    assert.equal(typeof plan.comparison, "string");
    assert.ok(
        Number.isSafeInteger(plan.changedFiles) &&
            (plan.changedFiles as number) >= 0,
        "Invalid changed-file count",
    );
    assert.equal(typeof plan.documentationOnly, "boolean");
    assert.ok(
        !plan.documentationOnly ||
            !Object.values(groups).some((group) => group.selected),
        "Documentation-only plan selected runtime verification",
    );
    for (const [id, rule] of Object.entries(policy.groups))
        assert.ok(
            !groups[id]!.selected ||
                rule.requires.every((required) => groups[required]!.selected),
            id + " is missing required verification",
        );
    return {
        version: 1,
        event: plan.event as string,
        comparison: plan.comparison as string,
        changedFiles: plan.changedFiles as number,
        documentationOnly: plan.documentationOnly as boolean,
        groups,
        errors: strings(plan.errors, "Selection errors"),
    };
}
function cell(value: string): string {
    return value.replace(/[&<>|\\\x60\r\n]/g, (character) => {
        const escapes: Record<string, string> = {
            "&": "&amp;",
            "<": "&lt;",
            ">": "&gt;",
            "|": "&#124;",
            "\\": "&#92;",
            "\x60": "&#96;",
            "\r": " ",
            "\n": " ",
        };
        return escapes[character]!;
    });
}
export function report(
    policy: Policy,
    input: unknown,
): { summary: string; errors: string[] } {
    const needs = object(input, "CI job results");
    const detection = object(needs.changes, "changes result");
    const errors: string[] = [];
    let plan: Plan | undefined;
    if (detection.result !== "success")
        errors.push("Change detection did not succeed.");
    else {
        try {
            const outputs = object(detection.outputs, "changes outputs");
            assert.equal(
                typeof outputs.plan,
                "string",
                "Selection output is missing",
            );
            plan = parsePlan(policy, JSON.parse(outputs.plan as string));
            errors.push(...plan.errors);
            for (const [id, group] of Object.entries(plan.groups))
                assert.equal(
                    outputs[id],
                    String(group.selected),
                    "Selection output disagrees for " + id,
                );
        } catch (error) {
            errors.push(
                "Invalid change selection: " +
                    (error instanceof Error ? error.message : "unknown error"),
            );
        }
    }
    const rows: string[] = [];
    for (const [id, group] of Object.entries(policy.groups)) {
        const selected = plan?.groups[id]?.selected;
        const raw = needs[id];
        const result =
            raw && typeof raw === "object"
                ? (raw as Record<string, unknown>).result
                : undefined;
        const valid = ["success", "failure", "cancelled", "skipped"].includes(
            result as string,
        );
        if (!valid) errors.push("Missing or invalid result for " + id + ".");
        else if (selected === true && result !== "success")
            errors.push("Selected " + id + " ended with " + result + ".");
        else if (selected === false && result !== "skipped")
            errors.push(
                "Unselected " + id + " unexpectedly ended with " + result + ".",
            );
        const reason =
            plan?.groups[id]?.reasons.join("; ") ||
            (plan?.documentationOnly
                ? "Documentation only"
                : "No matching changes");
        rows.push(
            "| " +
                cell(group.label) +
                " | " +
                (selected === undefined ? "Unknown" : selected ? "Yes" : "No") +
                " | " +
                cell(reason) +
                " | " +
                (result === "skipped" && selected === false
                    ? "Intentionally skipped"
                    : valid
                      ? result
                      : "Missing") +
                " |",
        );
    }
    const summary = [
        "## CI verification",
        "",
        plan
            ? cell(plan.event) +
              ": " +
              plan.changedFiles +
              " changed file(s). " +
              cell(plan.comparison)
            : "Change detection could not produce a valid selection.",
        "",
        ...(plan?.documentationOnly
            ? [
                  "Documentation-only change: no builds or runtime tests were required.",
                  "",
              ]
            : []),
        "| Group | Selected | Reason | Result |",
        "| --- | --- | --- | --- |",
        ...rows,
        "",
        "**Required check test_results: " +
            (errors.length === 0 ? "Passed" : "Failed") +
            ".**",
        "",
        ...errors.map((error) => "- " + cell(error)),
        "",
    ].join("\n");
    return { summary, errors };
}
function requiredEnvironment(name: string): string {
    const value = process.env[name];
    assert.ok(value, "Missing " + name);
    return value;
}
export async function main(
    args: string[],
    root = process.cwd(),
): Promise<void> {
    const policy = await loadPolicy(root);
    if (args[0] === "detect" && args.length === 1) {
        const event = requiredEnvironment(Env.GITHUB_EVENT_NAME.name);
        const payload = JSON.parse(
            await readFile(
                requiredEnvironment(Env.GITHUB_EVENT_PATH.name),
                "utf8",
            ),
        );
        const range = comparison(
            event,
            payload,
            requiredEnvironment(Env.GITHUB_SHA.name),
        );
        const inventory = git(root, [
            "ls-tree",
            "-r",
            "--name-only",
            "-z",
            range.head,
        ])
            .split("\0")
            .filter(Boolean);
        const plan = select(
            policy,
            changedFiles(root, range),
            range.full,
            inventory,
        );
        plan.event = event;
        plan.comparison = range.base
            ? range.base.slice(0, 12) +
              (range.mergeBase ? "..." : "..") +
              range.head.slice(0, 12)
            : "Full snapshot " + range.head.slice(0, 12);
        const outputs =
            [
                "plan=" + JSON.stringify(plan),
                ...Object.entries(plan.groups).map(
                    ([id, group]) => id + "=" + group.selected,
                ),
            ].join("\n") + "\n";
        await appendFile(requiredEnvironment(Env.GITHUB_OUTPUT.name), outputs);
        console.log(
            "Selected " +
                Object.values(plan.groups).filter((group) => group.selected)
                    .length +
                " verification group(s) for " +
                plan.changedFiles +
                " changed file(s).",
        );
        // Keep unsupported-language errors in the plan so existing checks run and
        // test_results still fails even when every configured suite succeeds.
    } else if (args[0] === "run" && args.length === 2) {
        const group = policy.groups[args[1]!];
        assert.ok(group, "Unknown verification group");
        for (const command of group.commands) {
            console.log("Running " + command);
            const result = spawnSync(
                "bash",
                ["-e", "-u", "-o", "pipefail", "-c", command],
                { cwd: root, stdio: "inherit" },
            );
            assert.ifError(result.error);
            assert.equal(
                result.status,
                0,
                group.label + " verification command failed",
            );
        }
    } else if (args[0] === "report" && args.length === 1) {
        const result = report(
            policy,
            JSON.parse(requiredEnvironment(Env.CI_JOB_RESULTS.name)),
        );
        const summaryPath = process.env[Env.GITHUB_STEP_SUMMARY.name];
        if (summaryPath) await appendFile(summaryPath, result.summary);
        console.log(result.summary);
        assert.equal(
            result.errors.length,
            0,
            "CI verification failed; see the job summary",
        );
    } else
        throw new Error(
            "Usage: node scripts/ci.mts detect | run GROUP | report",
        );
}
if (
    process.argv[1] &&
    resolve(process.argv[1]) === fileURLToPath(import.meta.url)
)
    main(process.argv.slice(2)).catch((error) => {
        console.error(
            error instanceof Error ? error.message : "CI verification failed",
        );
        process.exitCode = 1;
    });
