//! Environment definitions for goblinctl, xtask, and their embedded installers.
//! Values are still read, parsed, and validated at configuration boundaries.
//! See docs/environment-variables.md for contributor and deployment guidance.

/// Application PostgreSQL connection; .NET maps __ to ConnectionStrings:Goblin.
/// Format: Npgsql connection string.
/// Default/fallback: Existing appsettings/configuration providers; environment overrides JSON under the standard .NET host.
/// Required: Required for durable Work. Sensitive: yes.
pub const CONNECTIONSTRINGS_GOBLIN: &str = "ConnectionStrings__Goblin";

/// Schema administrator PostgreSQL connection; .NET maps __ to ConnectionStrings:GoblinAdmin.
/// Format: Npgsql connection string.
/// Default/fallback: Existing database tooling appsettings/configuration providers; environment overrides JSON.
/// Required: Required for schema migration. Sensitive: yes.
pub const CONNECTIONSTRINGS_GOBLINADMIN: &str = "ConnectionStrings__GoblinAdmin";

/// GitHub Actions output file for release preparation results.
/// Format: File path.
/// Default/fallback: No output file is written when unset.
/// Required: Optional; supplied by GitHub Actions. Sensitive: no.
pub const GITHUB_OUTPUT: &str = "GITHUB_OUTPUT";

/// GitHub Actions attempt identity embedded in release provenance.
/// Format: Decimal attempt number text.
/// Default/fallback: No fallback.
/// Required: Required for release preparation. Sensitive: no.
pub const GITHUB_RUN_ATTEMPT: &str = "GITHUB_RUN_ATTEMPT";

/// GitHub Actions run identity embedded in release provenance.
/// Format: Decimal run ID text.
/// Default/fallback: No fallback; release preparation requires GitHub Actions.
/// Required: Required for release preparation. Sensitive: no.
pub const GITHUB_RUN_ID: &str = "GITHUB_RUN_ID";

/// GitHub Actions Markdown summary file.
/// Format: File path.
/// Default/fallback: No summary file is written when unset.
/// Required: Optional; supplied by GitHub Actions. Sensitive: no.
pub const GITHUB_STEP_SUMMARY: &str = "GITHUB_STEP_SUMMARY";

/// Repository/configuration root used by PostgreSQL setup.
/// Format: Directory path.
/// Default/fallback: Current working directory in setup.sh; Rust tooling supplies repository root.
/// Required: Optional. Sensitive: no.
pub const GOBLIN_CONFIG_ROOT: &str = "GOBLIN_CONFIG_ROOT";

/// Concurrent image download workers in the installation script.
/// Format: Integer from 1 to 8.
/// Default/fallback: 4; invalid values fail installation.
/// Required: Optional. Sensitive: no.
pub const GOBLIN_IMAGE_PULL_WORKERS: &str = "GOBLIN_IMAGE_PULL_WORKERS";

/// Forward PostgreSQL setup progress to the native installation state.
/// Format: true enables progress.
/// Default/fallback: false.
/// Required: Optional. Sensitive: no.
pub const GOBLIN_INSTALL_PROGRESS: &str = "GOBLIN_INSTALL_PROGRESS";

/// Installation status file used by PostgreSQL progress reporting.
/// Format: File path.
/// Default/fallback: /var/lib/goblin/install/status.json.
/// Required: Optional when progress is enabled. Sensitive: no.
pub const GOBLIN_INSTALL_STATE: &str = "GOBLIN_INSTALL_STATE";

/// Plaintext password input for unattended initial setup or explicit password replacement.
/// Format: Non-blank text, at most 128 UTF-16 code units, no control characters.
/// Default/fallback: Prompt with hidden confirmation when a new verifier is needed; existing verifier wins unless replacing.
/// Required: Required only for unattended creation or replacement; removed from child environments. Sensitive: yes.
pub const GOBLIN_LOCAL_PASSWORD: &str = "GOBLIN_LOCAL_PASSWORD";

/// Root for the native CLI subprocess contract-test fixture.
/// Format: Directory path.
/// Default/fallback: Fixture subprocess does nothing when unset.
/// Required: Tests only. Sensitive: no.
pub const GOBLIN_NATIVE_TEST_ROOT: &str = "GOBLIN_NATIVE_TEST_ROOT";

/// File containing the owner password verifier; never a plaintext password.
/// Format: Path to a private PBKDF2-SHA256 verifier file.
/// Default/fallback: Web requires an explicit file; dev/install tooling uses .goblin-secrets/owner-password and validates explicit overrides without fallback.
/// Required: Required by the web host; optional override for tooling. Sensitive: yes.
pub const GOBLIN_PASSWORD_HASH_FILE: &str = "GOBLIN_PASSWORD_HASH_FILE";

/// Write application PostgreSQL configuration during database setup.
/// Format: true enables configuration.
/// Default/fallback: true; installer sets false before applying its own configuration.
/// Required: Optional. Sensitive: no.
pub const GOBLIN_POSTGRES_CONFIGURE_APP: &str = "GOBLIN_POSTGRES_CONFIGURE_APP";

/// Browser-facing origin used for origin validation and deployment.
/// Format: Absolute HTTPS origin, or loopback HTTP; remote HTTP requires explicit opt-in.
/// Default/fallback: Web: http://localhost:<GOBLIN_PORT>; installer: http://<hostname>; local test sets its configured origin.
/// Required: Optional. Sensitive: no.
pub const GOBLIN_PUBLIC_ORIGIN: &str = "GOBLIN_PUBLIC_ORIGIN";

/// Opt-in real PostgreSQL test administrator connection.
/// Format: Npgsql connection string.
/// Default/fallback: Database tests skip when the required test connections are absent.
/// Required: Required only for real PostgreSQL tests. Sensitive: yes.
pub const GOBLIN_TEST_POSTGRES_ADMIN: &str = "GOBLIN_TEST_POSTGRES_ADMIN";

/// Opt-in real PostgreSQL test application connection, forwarded as ConnectionStrings__Goblin by the HTTP harness.
/// Format: Npgsql connection string.
/// Default/fallback: Database/HTTP/browser Work tests skip when absent.
/// Required: Required only for real PostgreSQL tests. Sensitive: yes.
pub const GOBLIN_TEST_POSTGRES_APP: &str = "GOBLIN_TEST_POSTGRES_APP";

/// Native CLI executable used by embedded provisioning scripts.
/// Format: Executable path.
/// Default/fallback: Script-specific discovery when unset; Rust database tooling supplies current executable.
/// Required: Supplied by installation tooling. Sensitive: no.
pub const GOBLINCTL: &str = "GOBLINCTL";

/// Local CLI binary used instead of downloading during installer development.
/// Format: Executable path.
/// Default/fallback: Unset uses the selected published CLI release.
/// Required: Supplied only for a local install. Sensitive: no.
pub const GOBLINCTL_LOCAL_BINARY: &str = "GOBLINCTL_LOCAL_BINARY";

/// Expected digest of the local installer binary.
/// Format: 64 hexadecimal SHA-256 characters.
/// Default/fallback: Rust computes the local binary checksum; no independent fallback.
/// Required: Required with GOBLINCTL_LOCAL_BINARY. Sensitive: no.
pub const GOBLINCTL_LOCAL_SHA256: &str = "GOBLINCTL_LOCAL_SHA256";

/// Executable search path for application tools and subprocesses.
/// Format: OS path-separator-delimited directories.
/// Default/fallback: Inherited when allowed; GitHub/sandbox workers use /usr/bin:/bin if absent; Codex lookup uses empty path if absent.
/// Required: Required when executables are not absolute paths. Sensitive: no.
pub const PATH: &str = "PATH";

/// Original sudo caller group ID for ownership of private developer files.
/// Format: Unsigned integer.
/// Default/fallback: Ownership override ignored unless running as root and both sudo IDs parse.
/// Required: Optional; provided by sudo. Sensitive: no.
pub const SUDO_GID: &str = "SUDO_GID";

/// Original sudo caller user ID for ownership of private developer files.
/// Format: Unsigned integer.
/// Default/fallback: Ownership override ignored unless running as root and both sudo IDs parse.
/// Required: Optional; provided by sudo. Sensitive: no.
pub const SUDO_UID: &str = "SUDO_UID";
