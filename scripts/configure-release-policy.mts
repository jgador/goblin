import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";

// Administrative setup is separate from workflow credentials. GITHUB_TOKEN cannot
// change repository rulesets. Existing unrelated rulesets remain untouched.
const repo = process.env.GITHUB_REPOSITORY ?? "jgador/goblin";
function api<T>(path: string, method = "GET", body?: unknown): T {
    const args = ["api", path, "--method", method];
    if (body !== undefined) args.push("--input", "-");
    const value = execFileSync("gh", args, {
        encoding: "utf8",
        input: body === undefined ? undefined : JSON.stringify(body),
    });
    return value.trim() ? (JSON.parse(value) as T) : (undefined as T);
}

const policy = JSON.parse(
    readFileSync(".github/goblinctl-ruleset.json", "utf8"),
) as { name: string };
const mode = process.argv[2];
if (mode === "prepare") {
    const actor = api<{ id: number; login: string }>("user");
    api(`repos/${repo}/environments/goblinctl-release`, "PUT", {
        wait_timer: 0,
        prevent_self_review: false, // A single-maintainer repo still gets an explicit publication checkpoint.
        reviewers: [{ type: "User", id: actor.id }],
        deployment_branch_policy: null,
    });
    api(`repos/${repo}/actions/permissions/workflow`, "PUT", {
        default_workflow_permissions: "read",
        can_approve_pull_request_reviews: true, // GitHub uses this setting for bot PR creation too; workflows never approve PRs.
    });
    console.log(
        `Configured publication approval for ${actor.login} and enabled bot PR creation. Master enforcement is not activated yet.`,
    );
} else if (mode === "activate") {
    const master = api<{ object: { sha: string } }>(
        `repos/${repo}/git/ref/heads/master`,
    ).object.sha;
    const file = api<{ content: string }>(
        `repos/${repo}/contents/deploy/goblinctl-release.json?ref=${master}`,
    );
    const pin = JSON.parse(
        Buffer.from(file.content, "base64").toString("utf8"),
    ) as { schemaVersion?: number; sourceDirty?: boolean };
    if (pin.schemaVersion !== 1 || pin.sourceDirty !== false)
        throw new Error(
            "Master has no verified release baseline. Publish and merge the initial release pin first.",
        );
    const checks = api<{
        check_runs: { name: string; conclusion: string; app: { id: number } }[];
    }>(`repos/${repo}/commits/${master}/check-runs?per_page=100`);
    if (
        !checks.check_runs.some(
            (check) =>
                check.name === "goblinctl-release-ready" &&
                check.app.id === 15368 &&
                check.conclusion === "success",
        )
    ) {
        throw new Error(
            "The current master commit must pass goblinctl-release-ready before enforcement is activated.",
        );
    }
    const existing = api<{ id: number; name: string }[]>(
        `repos/${repo}/rulesets?per_page=100`,
    ).find((rule) => rule.name === policy.name);
    api(
        `repos/${repo}/rulesets${existing ? `/${existing.id}` : ""}`,
        existing ? "PUT" : "POST",
        policy,
    );
    console.log(
        `Activated ${policy.name}: master requires PRs and the GitHub Actions release check, with no bypass actors.`,
    );
} else if (mode === "show") {
    console.log(JSON.stringify(policy, null, 2));
} else {
    throw new Error(
        "Usage: node scripts/configure-release-policy.mts show|prepare|activate",
    );
}
