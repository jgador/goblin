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
        purpose: "Repository selected by release publication.",
        format: "owner/name",
        fallback: "No fallback",
        required: "Required for release publication",
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
        purpose: "Trigger the offline installer fixture build timeout.",
        format: "true or false",
        fallback: "Harness uses false",
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
            "Web: http://localhost:<GOBLIN_PORT>; installer: http://<hostname>; local test sets its configured origin",
        required: "Optional",
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
            "External credential used as a negative inheritance fixture; Goblin authenticates through private credential storage.",
        format: "Secret text",
        fallback: "Not forwarded to isolated runtimes or GitHub CLI",
        required: "Never required by Goblin environment configuration",
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
