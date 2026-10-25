/** Metadata describes existing boundaries; it does not read or parse process.env. */
export interface EnvironmentVariableDefinition {
    readonly name: string;
    readonly purpose: string;
    readonly format: string;
    readonly fallback: string;
    readonly required: string;
    readonly sensitive: boolean;
}

/** See docs/environment-variables.md before adding or forwarding a variable. */
export const environmentVariables = {
    GITHUB_ACTOR: {
        name: "GITHUB_ACTOR",
        purpose:
            "Authenticate the original actor of owner-only maintenance runs.",
        format: "GitHub login supplied by Actions.",
        fallback: "Maintenance refuses missing or non-owner actors.",
        required: "Required for owner-only agentic maintenance.",
        sensitive: false,
    },
    GITHUB_TRIGGERING_ACTOR: {
        name: "GITHUB_TRIGGERING_ACTOR",
        purpose: "Authenticate the actor dispatching or rerunning maintenance.",
        format: "GitHub login supplied by Actions.",
        fallback: "Maintenance refuses missing or non-owner rerun actors.",
        required: "Required for owner-only agentic maintenance.",
        sensitive: false,
    },
    GITHUB_REF: {
        name: "GITHUB_REF",
        purpose:
            "Identify the branch selected for owner-only manual maintenance.",
        format: "Fully qualified refs/heads/ branch reference.",
        fallback: "Maintenance refuses missing references.",
        required: "Supplied by Actions for owner-only maintenance.",
        sensitive: false,
    },
    CI_JOB_RESULTS: {
        name: "CI_JOB_RESULTS",
        purpose:
            "Completed CI dependency results supplied to the required merge check.",
        format: "GitHub Actions toJSON(needs) object.",
        fallback: "The CI reporter fails when absent or invalid.",
        required: "Required for the test_results job only.",
        sensitive: false,
    },
    GITHUB_EVENT_NAME: {
        name: "GITHUB_EVENT_NAME",
        purpose: "Select the Git comparison appropriate to the CI trigger.",
        format: "pull_request, push, merge_group, or workflow_dispatch.",
        fallback: "CI detection fails for an absent or unsupported event.",
        required: "Supplied by GitHub Actions to CI detection.",
        sensitive: false,
    },
    GITHUB_EVENT_PATH: {
        name: "GITHUB_EVENT_PATH",
        purpose:
            "GitHub event payload containing exact comparison commit identities.",
        format: "Path to the GitHub Actions JSON event payload.",
        fallback: "CI detection fails when absent.",
        required: "Supplied by GitHub Actions to CI detection.",
        sensitive: false,
    },
    GITHUB_SHA: {
        name: "GITHUB_SHA",
        purpose: "Commit identity used for a manually requested full CI run.",
        format: "Full Git commit SHA.",
        fallback: "CI detection fails when absent.",
        required: "Supplied by GitHub Actions to CI detection.",
        sensitive: false,
    },
    GITHUB_OUTPUT: {
        name: "GITHUB_OUTPUT",
        purpose:
            "GitHub Actions output file for the verified CI selection plan.",
        format: "File path.",
        fallback: "CI detection fails rather than losing selection outputs.",
        required: "Supplied by GitHub Actions to CI detection.",
        sensitive: false,
    },
    CANDIDATE_SHA256: {
        name: "CANDIDATE_SHA256",
        purpose:
            "Digest of the sealed release manifest selected for publication approval.",
        format: "Lowercase SHA-256 hex",
        fallback:
            "No fallback; publication rejects a missing or different digest",
        required: "Required for coordinated release publication",
        sensitive: false,
    },
    GOBLIN_TEST_POSTGRES_ADMIN: {
        name: "GOBLIN_TEST_POSTGRES_ADMIN",
        purpose:
            "Opt-in local PostgreSQL schema administrator connection for integration tests.",
        format: "Npgsql connection string.",
        fallback: "Database-dependent tests are skipped.",
        required: "Only when running real PostgreSQL tests.",
        sensitive: true,
    },
    TMPDIR: {
        name: "TMPDIR",
        purpose:
            "Keep setup and test subprocess temporary files in a private task directory.",
        format: "Absolute directory path.",
        fallback: "Operating-system temporary directory.",
        required:
            "Set for isolated Slack setup and task-local integration verification.",
        sensitive: false,
    },
    BUILDX_BUILDER: {
        name: "BUILDX_BUILDER",
        purpose:
            "Docker/Buildx host setting that the installer removes to avoid inheriting personal daemon configuration.",
        format: "Docker/Buildx setting text",
        fallback:
            "Removed from installer child environment; fixture verifies absence",
        required: "Never required by the installer",
        sensitive: false,
    },
    BUILDX_CONFIG: {
        name: "BUILDX_CONFIG",
        purpose:
            "Docker/Buildx host setting that the installer removes to avoid inheriting personal daemon configuration.",
        format: "Filesystem path",
        fallback:
            "Removed from installer child environment; fixture verifies absence",
        required: "Never required by the installer",
        sensitive: false,
    },
    CODEX_API_KEY: {
        name: "CODEX_API_KEY",
        purpose:
            "External credential used as a negative inheritance fixture; Goblin authenticates through private credential storage.",
        format: "Secret text",
        fallback: "Not forwarded to isolated runtimes or GitHub CLI",
        required: "Never required by Goblin environment configuration",
        sensitive: true,
    },
    CODEX_HOME: {
        name: "CODEX_HOME",
        purpose: "Isolated Codex configuration and credential directory.",
        format: "Private directory path",
        fallback:
            "Goblin supplies its private Codex directory; inherited values are replaced",
        required: "Supplied for Codex; required by the fake runtime fixture",
        sensitive: true,
    },
    ConnectionStrings__Goblin: {
        name: "ConnectionStrings__Goblin",
        purpose:
            "Application PostgreSQL connection; .NET maps __ to ConnectionStrings:Goblin.",
        format: "Npgsql connection string",
        fallback:
            "Existing appsettings/configuration providers; environment overrides JSON under the standard .NET host",
        required: "Required for durable Work",
        sensitive: true,
    },
    ConnectionStrings__GoblinAdmin: {
        name: "ConnectionStrings__GoblinAdmin",
        purpose:
            "Schema administrator connection supplied by PostgreSQL profiles.",
        format: "Npgsql connection string",
        fallback: "Environment overrides standard .NET configuration",
        required: "Schema migrations and explicit administrator commands",
        sensitive: true,
    },
    HOME: {
        name: "HOME",
        purpose: "User home directory for the PostgreSQL profile store.",
        format: "Absolute path",
        fallback: "Required when XDG_CONFIG_HOME is unset",
        required: "Developer profile commands",
        sensitive: false,
    },
    XDG_CONFIG_HOME: {
        name: "XDG_CONFIG_HOME",
        purpose: "User configuration base for PostgreSQL profiles.",
        format: "Absolute path",
        fallback: "HOME/.config",
        required: "Optional",
        sensitive: false,
    },
    SUDO_UID: {
        name: "SUDO_UID",
        purpose: "Original sudo caller user ID.",
        format: "Unsigned integer",
        fallback: "No caller ownership override",
        required: "Provided by sudo; cleared in isolated profile tests",
        sensitive: false,
    },
    SUDO_GID: {
        name: "SUDO_GID",
        purpose: "Original sudo caller group ID.",
        format: "Unsigned integer",
        fallback: "No caller ownership override",
        required: "Provided by sudo; cleared in isolated profile tests",
        sensitive: false,
    },
    DOCKER_CERT_PATH: {
        name: "DOCKER_CERT_PATH",
        purpose:
            "Docker/Buildx host setting that the installer removes to avoid inheriting personal daemon configuration.",
        format: "Filesystem path",
        fallback:
            "Removed from installer child environment; fixture verifies absence",
        required: "Never required by the installer",
        sensitive: true,
    },
    DOCKER_CONFIG: {
        name: "DOCKER_CONFIG",
        purpose: "Docker CLI configuration directory.",
        format: "Directory path, potentially containing credentials",
        fallback:
            "Installer replaces inherited value with its private configuration directory",
        required: "Supplied by installer",
        sensitive: true,
    },
    DOCKER_CONTEXT: {
        name: "DOCKER_CONTEXT",
        purpose:
            "Docker/Buildx host setting that the installer removes to avoid inheriting personal daemon configuration.",
        format: "Docker/Buildx setting text",
        fallback:
            "Removed from installer child environment; fixture verifies absence",
        required: "Never required by the installer",
        sensitive: false,
    },
    DOCKER_HOST: {
        name: "DOCKER_HOST",
        purpose: "Docker daemon endpoint.",
        format: "Docker endpoint URL",
        fallback: "Installer forces unix:///var/run/docker.sock",
        required: "Supplied by installer",
        sensitive: false,
    },
    DOCKER_TLS: {
        name: "DOCKER_TLS",
        purpose:
            "Docker/Buildx host setting that the installer removes to avoid inheriting personal daemon configuration.",
        format: "Docker/Buildx setting text",
        fallback:
            "Removed from installer child environment; fixture verifies absence",
        required: "Never required by the installer",
        sensitive: false,
    },
    DOCKER_TLS_VERIFY: {
        name: "DOCKER_TLS_VERIFY",
        purpose:
            "Docker/Buildx host setting that the installer removes to avoid inheriting personal daemon configuration.",
        format: "Docker/Buildx setting text",
        fallback:
            "Removed from installer child environment; fixture verifies absence",
        required: "Never required by the installer",
        sensitive: false,
    },
    EXTERNAL_SERVICE_API_KEY: {
        name: "EXTERNAL_SERVICE_API_KEY",
        purpose:
            "Fictional external-service credential used only to test subprocess environment isolation.",
        format: "Dummy test marker",
        fallback: "Not forwarded to isolated runtimes or GitHub CLI",
        required: "Never required by Goblin environment configuration",
        sensitive: true,
    },
    GITHUB_REPOSITORY: {
        name: "GITHUB_REPOSITORY",
        purpose:
            "Repository selected by release publication and checked for owner-only maintenance.",
        format: "owner/name",
        fallback: "No fallback",
        required: "Required for release publication and owner-only maintenance",
        sensitive: false,
    },
    GITHUB_RUN_ID: {
        name: "GITHUB_RUN_ID",
        purpose: "GitHub Actions run identity embedded in release provenance.",
        format: "Decimal run ID text",
        fallback: "No fallback; release preparation requires GitHub Actions",
        required: "Required for release preparation",
        sensitive: false,
    },
    GITHUB_STEP_SUMMARY: {
        name: "GITHUB_STEP_SUMMARY",
        purpose: "GitHub Actions Markdown summary file.",
        format: "File path",
        fallback: "No summary file is written when unset",
        required: "Optional; supplied by GitHub Actions",
        sensitive: false,
    },
    GOBLIN_BOOTSTRAP_APP_STATE: {
        name: "GOBLIN_BOOTSTRAP_APP_STATE",
        purpose:
            "Application failure/readiness scenario for the offline installer fixture.",
        format: "Scenario text accepted by the fixture (ready, cert-failed, etc.)",
        fallback: "Harness uses ready",
        required: "Offline deployment tests only",
        sensitive: false,
    },
    GOBLIN_BOOTSTRAP_DOCKER_INSTALLED: {
        name: "GOBLIN_BOOTSTRAP_DOCKER_INSTALLED",
        purpose:
            "Initial Docker installation scenario for the offline installer fixture.",
        format: "true or false",
        fallback: "Harness uses true",
        required: "Offline deployment tests only",
        sensitive: false,
    },
    GOBLIN_BOOTSTRAP_NATIVE_DOWNLOAD: {
        name: "GOBLIN_BOOTSTRAP_NATIVE_DOWNLOAD",
        purpose:
            "Native CLI download failure scenario for the offline installer fixture.",
        format: "failed, corrupt, wrong-version, or empty",
        fallback: "Harness supplies empty for successful download",
        required: "Offline deployment tests only",
        sensitive: false,
    },
    GOBLIN_BOOTSTRAP_NODE_STATE: {
        name: "GOBLIN_BOOTSTRAP_NODE_STATE",
        purpose: "Node readiness scenario for the offline installer fixture.",
        format: "ready, delayed, missing, or not-ready",
        fallback: "Harness uses ready",
        required: "Offline deployment tests only",
        sensitive: false,
    },
    GOBLIN_BOOTSTRAP_PULL_DELAY: {
        name: "GOBLIN_BOOTSTRAP_PULL_DELAY",
        purpose:
            "Artificial image-pull delay in the offline installer fixture.",
        format: "Milliseconds as numeric text",
        fallback: "Harness supplies 0",
        required: "Offline deployment tests only",
        sensitive: false,
    },
    GOBLIN_BOOTSTRAP_SLOW_BUILD: {
        name: "GOBLIN_BOOTSTRAP_SLOW_BUILD",
        purpose:
            "Artificial application-build delay in the offline installer fixture.",
        format: "Milliseconds as numeric text",
        fallback: "Harness supplies 0",
        required: "Offline deployment tests only",
        sensitive: false,
    },
    GOBLIN_BOOTSTRAP_TEST_DIR: {
        name: "GOBLIN_BOOTSTRAP_TEST_DIR",
        purpose: "Root of the offline installer fixture filesystem.",
        format: "Directory path",
        fallback: "Harness supplies a temporary directory",
        required: "Offline deployment tests only",
        sensitive: false,
    },
    GOBLIN_EXECUTION_NAMESPACE: {
        name: "GOBLIN_EXECUTION_NAMESPACE",
        purpose:
            "Namespace for isolated repository executions and the repository listener.",
        format: "Kubernetes namespace",
        fallback:
            "Unset: local text execution only; monitoring independently defaults to agents",
        required: "Required for isolated repository execution",
        sensitive: false,
    },
    GOBLIN_LOCAL_PASSWORD: {
        name: "GOBLIN_LOCAL_PASSWORD",
        purpose:
            "Plaintext password input for unattended initial setup or explicit password replacement.",
        format: "Non-blank text, at most 128 UTF-16 code units, no control characters",
        fallback:
            "Prompt with hidden confirmation when a new verifier is needed; existing verifier wins unless replacing",
        required:
            "Required only for unattended creation or replacement; removed from child environments",
        sensitive: true,
    },
    GOBLIN_MIGRATION_TEST: {
        name: "GOBLIN_MIGRATION_TEST",
        purpose: "Root of the offline schema migration fixture.",
        format: "Directory path",
        fallback: "Harness supplies a temporary directory",
        required: "Offline deployment tests only",
        sensitive: false,
    },
    GOBLIN_MIGRATION_WAIT: {
        name: "GOBLIN_MIGRATION_WAIT",
        purpose: "Simulate schema migration wait failure.",
        format: "fail or ok",
        fallback: "Harness uses ok",
        required: "Offline deployment tests only",
        sensitive: false,
    },
    GOBLIN_PASSWORD_HASH_FILE: {
        name: "GOBLIN_PASSWORD_HASH_FILE",
        purpose:
            "File containing the owner password verifier; never a plaintext password.",
        format: "Path to a private PBKDF2-SHA256 verifier file",
        fallback:
            "Web requires an explicit file; dev/install tooling uses .goblin-secrets/owner-password and validates explicit overrides without fallback",
        required: "Required by the web host; optional override for tooling",
        sensitive: true,
    },
    GOBLIN_TEST_WINDOWS_EXPORT: {
        name: "GOBLIN_TEST_WINDOWS_EXPORT",
        purpose:
            "Run isolated Windows filesystem and ACL checks through WSL interop.",
        format: "true enables",
        fallback: "Windows export test skips",
        required: "Explicit local verification only",
        sensitive: false,
    },
    GOBLIN_POSTGRES_CERT_FAILED: {
        name: "GOBLIN_POSTGRES_CERT_FAILED",
        purpose: "Simulate a failed PostgreSQL certificate wait.",
        format: "true or unset",
        fallback: "Unset permits certificate readiness",
        required: "Offline deployment tests only",
        sensitive: false,
    },
    GOBLIN_POSTGRES_SETUP_DENY: {
        name: "GOBLIN_POSTGRES_SETUP_DENY",
        purpose:
            "Simulate a denied administrator role in the PostgreSQL setup fixture.",
        format: "true or unset",
        fallback: "Unset permits setup",
        required: "Offline deployment tests only",
        sensitive: false,
    },
    GOBLIN_POSTGRES_SETUP_TEST: {
        name: "GOBLIN_POSTGRES_SETUP_TEST",
        purpose: "Root of the offline PostgreSQL setup fixture.",
        format: "Directory path",
        fallback: "Harness supplies a temporary directory",
        required: "Offline deployment tests only",
        sensitive: false,
    },
    GOBLIN_PUBLIC_ORIGIN: {
        name: "GOBLIN_PUBLIC_ORIGIN",
        purpose:
            "Browser-facing origin used for origin validation and deployment.",
        format: "Absolute HTTPS origin, or loopback HTTP; remote HTTP requires explicit opt-in",
        fallback:
            "Web: http://localhost:<GOBLIN_PORT>; public installer: https://<hostname>; local installation uses its loopback HTTP origin",
        required: "Optional",
        sensitive: false,
    },
    GOBLIN_RELAY_TEST_ROOT: {
        name: "GOBLIN_RELAY_TEST_ROOT",
        purpose:
            "Root of the offline SSH, Kubernetes, and HTTP relay fixtures.",
        format: "Directory path",
        fallback: "Fixture subprocess does nothing when unset",
        required: "Offline development relay tests only",
        sensitive: false,
    },
    GOBLIN_SECRET: {
        name: "GOBLIN_SECRET",
        purpose:
            "Synthetic secret used to verify that unrelated environment entries are not inherited.",
        format: "Test-only text",
        fallback: "Never forwarded",
        required: "Tests only",
        sensitive: true,
    },
    GOBLIN_TEST_HEADLAMP_URL: {
        name: "GOBLIN_TEST_HEADLAMP_URL",
        purpose:
            "Private Headlamp origin used by the opt-in proxy smoke check.",
        format: "Internal HTTP origin",
        fallback: "Smoke check fails with setup instructions when unset",
        required: "Required only for the Headlamp smoke check",
        sensitive: false,
    },
    GOBLIN_TEST_LOG_MARKER: {
        name: "GOBLIN_TEST_LOG_MARKER",
        purpose: "Marker searched for by the logs smoke check.",
        format: "Log text",
        fallback: "No marker assertion when unset",
        required: "Optional",
        sensitive: false,
    },
    GOBLIN_TEST_POSTGRES_APP: {
        name: "GOBLIN_TEST_POSTGRES_APP",
        purpose:
            "Opt-in real PostgreSQL test application connection, forwarded as ConnectionStrings__Goblin by the HTTP harness.",
        format: "Npgsql connection string",
        fallback: "Database/HTTP/browser Work tests skip when absent",
        required: "Required only for real PostgreSQL tests",
        sensitive: true,
    },
    GOBLIN_TEST_VICTORIALOGS_URL: {
        name: "GOBLIN_TEST_VICTORIALOGS_URL",
        purpose:
            "Private VictoriaLogs origin used by the opt-in logs smoke check.",
        format: "Internal HTTP origin with upstream -http.pathPrefix=/logs",
        fallback: "Smoke check fails with setup instructions when unset",
        required: "Required only for the logs smoke check",
        sensitive: false,
    },
    GOBLINCTL: {
        name: "GOBLINCTL",
        purpose: "Native CLI executable used by embedded provisioning scripts.",
        format: "Executable path",
        fallback:
            "Script-specific discovery when unset; Rust database tooling supplies current executable",
        required: "Supplied by installation tooling",
        sensitive: false,
    },
    GOBLINCTL_TEST_BINARY: {
        name: "GOBLINCTL_TEST_BINARY",
        purpose:
            "Override the native CLI binary exercised by TypeScript tests.",
        format: "Executable path",
        fallback: "target/debug/goblinctl",
        required: "Optional",
        sensitive: false,
    },
    KUBECONFIG: {
        name: "KUBECONFIG",
        purpose: "kubectl cluster credentials/configuration path.",
        format: "File path or OS-separated file paths",
        fallback:
            "Installer supplies the local cluster kubeconfig; offline fixture accepts unset",
        required: "Required for cluster administration",
        sensitive: true,
    },
    OPENAI_API_KEY: {
        name: "OPENAI_API_KEY",
        purpose:
            "OpenAI inference credential supplied only to gh-aw's isolated API proxy; also a negative inheritance fixture in runtime tests.",
        format: "Secret text",
        fallback: "Not forwarded to isolated runtimes or GitHub CLI",
        required:
            "Required as a repository secret for Codex agentic maintenance; never a Goblin application input.",
        sensitive: true,
    },
    PATH: {
        name: "PATH",
        purpose:
            "Executable search path for application tools and subprocesses.",
        format: "OS path-separator-delimited directories",
        fallback:
            "Inherited when allowed; GitHub/sandbox workers use /usr/bin:/bin if absent; Codex lookup uses empty path if absent",
        required: "Required when executables are not absolute paths",
        sensitive: false,
    },
    PREPARED_ATTEMPT: {
        name: "PREPARED_ATTEMPT",
        purpose: "Preparation attempt whose release record is verified.",
        format: "Decimal attempt number text",
        fallback: "No fallback; comparisons fail if absent",
        required: "Required for prepared release verification",
        sensitive: false,
    },
    RELEASE_SOURCE: {
        name: "RELEASE_SOURCE",
        purpose:
            "Expected immutable source revision checked by release scripts.",
        format: "Git commit SHA",
        fallback: "No fallback; comparisons fail if absent",
        required: "Required for release verification",
        sensitive: false,
    },
    SERVICE_RESULT: {
        name: "SERVICE_RESULT",
        purpose: "systemd service result used by installer recovery.",
        format: "systemd result text such as success or signal",
        fallback:
            "systemd supplies it; offline harness uses success unless recovering",
        required: "Required for service recovery classification",
        sensitive: false,
    },
} as const satisfies Record<string, EnvironmentVariableDefinition>;

export type EnvironmentVariableKey = keyof typeof environmentVariables;
export type EnvironmentVariableName =
    (typeof environmentVariables)[EnvironmentVariableKey]["name"];
