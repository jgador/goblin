import assert from "node:assert/strict";
import test from "node:test";
import {
    deploy,
    render,
    validateEnvironment,
    validateImage,
} from "./release.mjs";

const image = `ghcr.io/jgador/goblin-website@sha256:${"a".repeat(64)}`;
test("releases reject mutable tags and images from other repositories", () => {
    assert.equal(validateImage(image), image);
    for (const invalid of [
        "ghcr.io/jgador/goblin-website:latest",
        image.replace("goblin-website", "other"),
        image.slice(0, -1),
        "",
    ]) {
        assert.throws(() => validateImage(invalid));
    }
});

test("missing reviewers, bypasses, and extra deployment refs fail closed", () => {
    const environment = {
        name: "website-production",
        can_admins_bypass: false,
        protection_rules: [
            {
                type: "required_reviewers",
                reviewers: [{ reviewer: { login: "owner" } }],
            },
        ],
        deployment_branch_policy: {
            protected_branches: false,
            custom_branch_policies: true,
        },
    };
    const policies = { branch_policies: [{ name: "master", type: "branch" }] };
    validateEnvironment(environment, policies);
    assert.throws(() =>
        validateEnvironment(
            { ...environment, can_admins_bypass: true },
            policies,
        ),
    );
    assert.throws(() =>
        validateEnvironment({ ...environment, protection_rules: [] }, policies),
    );
    assert.throws(() =>
        validateEnvironment(environment, {
            branch_policies: [
                ...policies.branch_policies,
                { name: "*", type: "branch" },
            ],
        }),
    );
    assert.throws(() =>
        validateEnvironment(environment, {
            branch_policies: [{ name: "master", type: "tag" }],
        }),
    );
});

test("production overlay retains scaling ownership and exposes only the website host", () => {
    const manifest = render(image);
    assert.ok(manifest.includes(image));
    assert.ok(manifest.includes("imagePullPolicy: IfNotPresent"));
    assert.ok(manifest.includes("host: goblinboard.com"));
    assert.ok(manifest.includes("ingressClassName: nginx"));
    assert.ok(manifest.includes("letsencrypt-production"));
    assert.ok(manifest.includes("minReplicas: 1"));
    assert.ok(manifest.includes("maxReplicas: 3"));
    assert.doesNotMatch(manifest, /^\s+replicas:/m);
    assert.doesNotMatch(
        manifest,
        /kind: Namespace|name: platform|imagePullPolicy: Never/,
    );
});

test("deployment rejects the wrong cluster before any mutation", () => {
    let calls = 0;
    const uid = "00000000-0000-4000-8000-000000000001";
    assert.throws(
        () =>
            deploy(image, "/explicit/kubeconfig", uid, () => {
                calls++;
                return JSON.stringify({
                    metadata: { uid: "different-deployment" },
                });
            }),
        /different Deployment/,
    );
    assert.equal(calls, 1);
});

test("image deployment preserves autoscaling and stops on concurrent changes", () => {
    const uid = "00000000-0000-4000-8000-000000000001";
    const state = {
        metadata: { uid, resourceVersion: "42" },
        spec: {
            replicas: 3,
            template: {
                spec: { containers: [{ name: "website", image: "previous" }] },
            },
        },
    };
    const calls = [];
    const execute = (_, args) => {
        assert.deepEqual(args.slice(0, 6), [
            "--kubeconfig",
            "/explicit/kubeconfig",
            "--namespace",
            "goblin",
            "--request-timeout=30s",
            args[5],
        ]);
        const command = args.slice(5);
        calls.push(command);
        if (command[0] === "get") return JSON.stringify(state);
        if (command[0] === "patch") {
            const patch = JSON.parse(command.at(-1));
            assert.deepEqual(patch.slice(0, 2), [
                { op: "test", path: "/metadata/uid", value: uid },
                { op: "test", path: "/metadata/resourceVersion", value: "42" },
            ]);
            assert.deepEqual(patch.slice(2), [
                {
                    op: "replace",
                    path: "/spec/template/spec/containers/0/image",
                    value: image,
                },
            ]);
            state.spec.template.spec.containers[0].image = image;
            return "patched";
        }
        return "rolled out\n";
    };
    assert.equal(
        deploy(image, "/explicit/kubeconfig", uid, execute),
        "rolled out\n",
    );
    assert.equal(state.spec.replicas, 3);
    assert.deepEqual(
        calls.map((call) => call[0]),
        ["get", "patch", "rollout", "get"],
    );
    let mutations = 0;
    assert.throws(
        () =>
            deploy(image, "/explicit/kubeconfig", uid, (_, args) => {
                if (args[5] === "get") return JSON.stringify(state);
                mutations++;
                throw new Error("resourceVersion test failed");
            }),
        /resourceVersion test failed/,
    );
    assert.equal(mutations, 1);
});
