import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";

export const imageRepository = "ghcr.io/jgador/goblin-website";

export function validateImage(image) {
    assert.match(
        image ?? "",
        /^ghcr\.io\/jgador\/goblin-website@sha256:[a-f0-9]{64}$/,
        "Use the website image's immutable SHA-256 digest.",
    );
    return image;
}

export function validateEnvironment(environment, policies) {
    assert.equal(environment.name, "website-production");
    assert.equal(
        environment.can_admins_bypass,
        false,
        "Disable administrator bypass for website-production.",
    );
    assert.ok(
        environment.protection_rules?.some(
            (rule) =>
                rule.type === "required_reviewers" && rule.reviewers?.length,
        ),
        "website-production requires a deployment reviewer.",
    );
    assert.equal(
        environment.deployment_branch_policy?.custom_branch_policies,
        true,
        "Select master explicitly as the deployment branch.",
    );
    assert.equal(
        environment.deployment_branch_policy?.protected_branches,
        false,
    );
    assert.equal(
        policies.branch_policies?.length,
        1,
        "Allow only the master branch.",
    );
    assert.equal(policies.branch_policies[0].name, "master");
    assert.equal(policies.branch_policies[0].type, "branch");
}

export function render(image, kubectl = "kubectl") {
    validateImage(image);
    const source = execFileSync(
        kubectl,
        ["kustomize", fileURLToPath(new URL("production/", import.meta.url))],
        { encoding: "utf8" },
    );
    const placeholder = `${imageRepository}:pending`;
    assert.equal(
        source.split(placeholder).length,
        2,
        "Expected exactly one website image in the overlay.",
    );
    return source.replace(placeholder, image);
}

export function deploy(
    image,
    kubeconfig,
    deploymentUid,
    execute = execFileSync,
) {
    validateImage(image);
    assert.ok(kubeconfig, "Pass the deployment kubeconfig path explicitly.");
    assert.match(
        deploymentUid ?? "",
        /^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/,
        "Set the WEBSITE_DEPLOYMENT_UID environment variable after administrator bootstrap.",
    );
    const command = (args) =>
        execute(
            "kubectl",
            [
                "--kubeconfig",
                kubeconfig,
                "--namespace",
                "goblin",
                "--request-timeout=30s",
                ...args,
            ],
            { encoding: "utf8" },
        );
    const before = JSON.parse(
        command(["get", "deployment", "website", "-o", "json"]),
    );
    assert.equal(
        before.metadata.uid,
        deploymentUid,
        "The kubeconfig targets a different Deployment. Check the cluster and bootstrap UID.",
    );
    assert.equal(before.spec.template.spec.containers.length, 1);
    assert.equal(before.spec.template.spec.containers[0].name, "website");
    // Atomic UID/resourceVersion tests prevent a stale release from patching a
    // recreated or concurrently changed Deployment. HPA replicas are untouched.
    const patch = JSON.stringify([
        { op: "test", path: "/metadata/uid", value: deploymentUid },
        {
            op: "test",
            path: "/metadata/resourceVersion",
            value: before.metadata.resourceVersion,
        },
        {
            op: "replace",
            path: "/spec/template/spec/containers/0/image",
            value: image,
        },
    ]);
    command([
        "patch",
        "deployment",
        "website",
        "--type=json",
        "--patch",
        patch,
    ]);
    const status = command([
        "rollout",
        "status",
        "deployment/website",
        "--timeout=180s",
    ]);
    const after = JSON.parse(
        command(["get", "deployment", "website", "-o", "json"]),
    );
    assert.equal(
        after.spec.template.spec.containers[0].image,
        image,
        "Another release changed the image during rollout.",
    );
    return status;
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
    const [action, image] = process.argv.slice(2);
    if (action === "render") {
        process.stdout.write(render(image));
    } else if (action === "check-public") {
        validateImage(image);
        const response = await fetch(
            "https://ghcr.io/token?service=ghcr.io&scope=repository:jgador/goblin-website:pull",
            { signal: AbortSignal.timeout(15000) },
        );
        assert.equal(
            response.status,
            200,
            "Make the goblin-website container package Public in GitHub package settings, then rerun this job.",
        );
        const { token } = await response.json();
        assert.ok(
            token,
            "The registry did not return an anonymous pull token.",
        );
        const manifest = await fetch(
            `https://ghcr.io/v2/jgador/goblin-website/manifests/${image.split("@")[1]}`,
            {
                method: "HEAD",
                headers: {
                    Authorization: `Bearer ${token}`,
                    Accept: "application/vnd.oci.image.index.v1+json, application/vnd.oci.image.manifest.v1+json, application/vnd.docker.distribution.manifest.v2+json",
                },
                signal: AbortSignal.timeout(15000),
            },
        );
        assert.equal(
            manifest.status,
            200,
            "The exact release image must be anonymously pullable before deployment.",
        );
        console.log(
            "The exact release image can be pulled without registry credentials.",
        );
    } else if (action === "check-environment") {
        const api = (path) =>
            JSON.parse(
                execFileSync(
                    "gh",
                    [
                        "api",
                        `repos/jgador/goblin/environments/website-production${path}`,
                    ],
                    { encoding: "utf8" },
                ),
            );
        validateEnvironment(api(""), api("/deployment-branch-policies"));
        console.log(
            "website-production requires review and permits only master; administrator bypass is disabled.",
        );
    } else if (action === "deploy") {
        const [, kubeconfig, deploymentUid] = process.argv.slice(3);
        process.stdout.write(deploy(image, kubeconfig, deploymentUid));
    } else {
        throw new Error(
            "Usage: node website/release.mjs {render IMAGE|check-public IMAGE|check-environment|deploy IMAGE KUBECONFIG DEPLOYMENT_UID}",
        );
    }
}
