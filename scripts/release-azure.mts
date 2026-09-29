/// <reference lib="dom" />
// Live installation verification. Invoked only by the explicitly enabled Actions job.
import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { createHash, randomBytes } from "node:crypto";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { setTimeout as delay } from "node:timers/promises";
import { promisify } from "node:util";
import { chromium, expect } from "@playwright/test";

const execute = promisify(execFile);
const repository = "jgador/goblin";
export function groupName(run: string, attempt: string) {
    assert.match(run, /^[1-9]\d*$/);
    assert.match(attempt, /^[1-9]\d*$/);
    return `goblin-release-${run}-${attempt}`;
}
export function ownedGroup(group: any, run?: string): boolean {
    return (
        /^goblin-release-[1-9]\d*-[1-9]\d*$/.test(group?.name ?? "") &&
        group?.tags?.goblinReleaseRepo === repository &&
        /^[1-9]\d*$/.test(group?.tags?.goblinReleaseRunId ?? "") &&
        group.name.startsWith(
            `goblin-release-${group.tags.goblinReleaseRunId}-`,
        ) &&
        (run === undefined || group.tags.goblinReleaseRunId === run)
    );
}
export function expiredGroup(group: any, now: number): boolean {
    const expires = Number(group?.tags?.goblinReleaseExpiresAt);
    return (
        ownedGroup(group) &&
        Number.isFinite(expires) &&
        expires > 0 &&
        expires < now
    );
}
function setting(name: string) {
    const value = process.env[name];
    assert.ok(value, `Configure ${name} for Azure release verification`);
    return value;
}
async function az(args: string[], timeout = 600_000): Promise<any> {
    try {
        const { stdout } = await execute(
            "az",
            [
                ...args,
                "--subscription",
                setting("AZURE_SUBSCRIPTION_ID"),
                "--only-show-errors",
                "--output",
                "json",
            ],
            { timeout, maxBuffer: 4 * 1024 * 1024 },
        );
        return stdout.trim() ? JSON.parse(stdout) : null;
    } catch {
        // CLI errors can contain protected deployment inputs. Keep them out of public artifacts.
        throw new Error(
            `Azure ${args.slice(0, 3).join(" ")} failed. Inspect the test resource group in Azure.`,
        );
    }
}
async function removeGroup(name: string, run?: string) {
    if (!(await az(["group", "exists", "--name", name]))) return;
    const group = await az(["group", "show", "--name", name]);
    assert.ok(
        ownedGroup(group, run),
        "Refusing to delete a resource group not owned by release verification",
    );
    await az(["group", "delete", "--name", name, "--yes", "--no-wait"]);
    await az(
        [
            "group",
            "wait",
            "--name",
            name,
            "--deleted",
            "--interval",
            "10",
            "--timeout",
            "900",
        ],
        930_000,
    );
}
async function remote(vm: string, script: string): Promise<string> {
    const result = await az([
        "vm",
        "run-command",
        "invoke",
        "--ids",
        vm,
        "--command-id",
        "RunShellScript",
        "--scripts",
        `set -eu\n${script}\nprintf '\\nGOBLIN_RELEASE_CHECK_OK\\n'`,
    ]);
    const output = result.value
        .filter(
            (entry: any) => entry.code === "ComponentStatus/StdOut/succeeded",
        )
        .map((entry: any) => entry.message)
        .join("\n");
    assert.ok(
        output.endsWith("GOBLIN_RELEASE_CHECK_OK\n") ||
            output.endsWith("GOBLIN_RELEASE_CHECK_OK"),
        "Remote verification failed",
    );
    return output
        .slice(0, output.lastIndexOf("GOBLIN_RELEASE_CHECK_OK"))
        .trim();
}
async function waitReady(url: string, timeout: number) {
    const deadline = Date.now() + timeout;
    while (Date.now() < deadline) {
        try {
            const status = await fetch(`${url}/setup/status`, {
                signal: AbortSignal.timeout(5_000),
            });
            if (status.ok) {
                const body = (await status.json()) as any;
                assert.notEqual(
                    body.status,
                    "failed",
                    "Goblin installation failed; explicit investigation is required",
                );
            }
            const result = await fetch(`${url}/readyz`, {
                signal: AbortSignal.timeout(5_000),
            });
            if (result.ok && ((await result.json()) as any).ready === true)
                return;
        } catch (error) {
            if (error instanceof assert.AssertionError) throw error;
            // Poll availability during the current installation; never restart a failed installer.
        }
        await delay(5_000);
    }
    throw new Error(
        "Goblin did not become ready within the installation time limit",
    );
}
async function installation(directory: string) {
    const record = JSON.parse(
        await readFile(join(directory, "release.json"), "utf8"),
    );
    const run = setting("GITHUB_RUN_ID");
    const attempt = setting("GITHUB_RUN_ATTEMPT");
    assert.equal(record.runId, run);
    assert.equal(record.runAttempt, attempt);
    const name = groupName(run, attempt);
    const region = setting("AZURE_RELEASE_REGION");
    const vmSize = process.env.AZURE_RELEASE_VM_SIZE || "Standard_D4s_v5";
    const template = resolve(directory, "azuredeploy.portal.json");
    const report = {
        schemaVersion: 1,
        sourceRevision: record.sourceRevision,
        installerVersion: record.installer.version,
        installerSha256: record.installer.sha256,
        templateSha256: createHash("sha256")
            .update(await readFile(template))
            .digest("hex"),
        runId: run,
        runAttempt: attempt,
        region,
        vmSize,
        resourceGroup: name,
        installation: "failed",
        cleanup: "failed",
        checks: {} as Record<string, string>,
    };
    assert.equal(
        report.templateSha256,
        record.assets["azuredeploy.portal.json"],
    );
    const secretDir = await mkdtemp(join(tmpdir(), "goblin-azure-test-"));
    let browser;
    let failure: unknown;
    try {
        assert.equal(
            await az(["group", "exists", "--name", name]),
            false,
            "Test resource group already exists",
        );
        await az([
            "group",
            "create",
            "--name",
            name,
            "--location",
            region,
            "--tags",
            `goblinReleaseRepo=${repository}`,
            `goblinReleaseRunId=${run}`,
            `goblinReleaseExpiresAt=${Date.now() + 3 * 60 * 60_000}`,
        ]);
        const password = randomBytes(32).toString("base64url");
        console.log(`::add-mask::${password}`);
        await execute("ssh-keygen", [
            "-q",
            "-t",
            "ed25519",
            "-N",
            "",
            "-f",
            join(secretDir, "ssh"),
        ]);
        const parameters = Object.fromEntries(
            Object.entries({
                companyName: "Goblin",
                environment: "test",
                goblinSourceRef: record.sourceRevision,
                goblinPassword: password,
                adminSshPublicKey: (
                    await readFile(join(secretDir, "ssh.pub"), "utf8")
                ).trim(),
                dnsLabel: name,
                vmSize,
            }).map(([key, value]) => [key, { value }]),
        );
        const parameterFile = join(secretDir, "parameters.json");
        await writeFile(parameterFile, JSON.stringify({ parameters }), {
            mode: 0o600,
        });
        const deployment = await az(
            [
                "deployment",
                "group",
                "create",
                "--resource-group",
                name,
                "--name",
                "goblin",
                "--template-file",
                template,
                "--parameters",
                `@${parameterFile}`,
            ],
            35 * 60_000,
        );
        const url = deployment.properties.outputs.goblinUrl.value;
        const vm = deployment.properties.outputs.virtualMachineResourceId.value;
        assert.match(
            url,
            /^http:\/\/goblin-release-[0-9-]+\.[a-z0-9]+\.cloudapp\.azure\.com$/,
        );
        assert.ok(
            vm.startsWith(
                `/subscriptions/${setting("AZURE_SUBSCRIPTION_ID")}/resourceGroups/${name}/`,
            ),
        );
        await waitReady(url, 35 * 60_000);
        report.checks.ready = "passed";
        const state = JSON.parse(
            await remote(vm, "cat /var/lib/goblin/install/status.json"),
        );
        assert.equal(state.status, "ready");
        for (const [step, check] of [
            ["database", "postgres"],
            ["migrate", "migrations"],
        ]) {
            assert.ok(
                state.steps.some(
                    (item: any) =>
                        item.id === step && item.status === "complete",
                ),
                `${step} did not complete`,
            );
            report.checks[check!] = "passed";
        }
        assert.equal(
            await remote(vm, "cat /var/lib/goblin/install/private/source-ref"),
            record.sourceRevision,
        );
        assert.equal(
            await remote(vm, "/opt/goblin/bin/goblinctl --version"),
            `goblinctl ${record.installer.version}`,
        );
        browser = await chromium.launch();
        const context = await browser.newContext();
        const page = await context.newPage();
        await page.goto(url);
        await expect(
            page.getByLabel("Goblin password", { exact: true }),
        ).toBeVisible();
        report.checks.handoff = "passed";
        await page
            .getByLabel("Goblin password", { exact: true })
            .fill(password);
        await page.getByRole("button", { name: "Open workspace" }).click();
        await expect
            .poll(
                async () =>
                    (
                        await (
                            await context.request.get(`${url}/api/session`)
                        ).json()
                    ).authenticated,
            )
            .toBe(true);
        report.checks.login = "passed";
        const request = async (path: string, data?: unknown) => {
            const response =
                data === undefined
                    ? await context.request.get(`${url}${path}`)
                    : await context.request.post(`${url}${path}`, {
                          data,
                          headers: { Origin: url },
                      });
            assert.equal(
                response.status(),
                200,
                `Application verification failed: ${path}`,
            );
            return response.json();
        };
        const { ids } = await request("/api/identities", {
            kinds: ["Work", "Command"],
        });
        const created = await request("/api/work/commands", {
            commandId: ids[1],
            workId: ids[0],
            action: "Create",
            text: "Release installation verification",
        });
        assert.equal(created.work.id, ids[0]);
        assert.deepEqual(
            (await request(`/api/work/${ids[0]}`)).work,
            created.work,
        );
        report.checks.persistence = "passed";
        await remote(
            vm,
            "k3s kubectl delete pod -n goblin -l app=goblin-auth --wait=true\nk3s kubectl wait --for=condition=Ready sandbox/app -n goblin --timeout=300s",
        );
        await waitReady(url, 5 * 60_000);
        // A restart may invalidate the in-memory login session; authenticate again.
        await request("/api/session", { password });
        assert.deepEqual(
            (await request(`/api/work/${ids[0]}`)).work,
            created.work,
        );
        report.checks.restart = "passed";
        report.installation = "passed";
    } catch (error) {
        failure = error;
    } finally {
        try {
            await browser?.close();
        } catch (error) {
            failure ??= error;
        }
        try {
            await removeGroup(name, run);
            report.cleanup = "passed";
        } catch (error) {
            failure ??= error;
        }
        await rm(secretDir, { recursive: true, force: true });
        await mkdir(".artifacts/azure-test", { recursive: true });
        await writeFile(
            ".artifacts/azure-test/result.json",
            `${JSON.stringify(report, null, 2)}\n`,
        );
    }
    if (failure) throw failure;
}
async function main() {
    assert.equal(
        process.env.GITHUB_ACTIONS,
        "true",
        "Live Azure checks run only in GitHub Actions",
    );
    assert.equal(process.env.GITHUB_REPOSITORY, repository);
    assert.equal(
        process.env.AZURE_RELEASE_TESTS_ENABLED,
        "true",
        "Azure release tests have not been explicitly configured and enabled",
    );
    const [action, directory = ".artifacts/goblin"] = process.argv.slice(2);
    switch (action) {
        case "install":
            await installation(directory);
            break;
        case "cleanup":
            await removeGroup(
                groupName(
                    setting("GITHUB_RUN_ID"),
                    setting("GITHUB_RUN_ATTEMPT"),
                ),
                setting("GITHUB_RUN_ID"),
            );
            break;
        case "sweep":
            for (const group of await az(["group", "list"]))
                if (expiredGroup(group, Date.now()))
                    await removeGroup(group.name);
            break;
        default:
            throw new Error("Expected install, cleanup, or sweep");
    }
}
if (import.meta.main) await main();
