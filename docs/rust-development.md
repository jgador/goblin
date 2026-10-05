# Rust development

Goblin follows the shared Rust development workflow in `openai/codex`, compared
against local checkout `f0f38b68f7` on 2026-09-27. Its Cargo workspace lives at the
repository root, with the permanent operator CLI in `tools/goblinctl` and build
tasks in `tools/xtask`. The .NET backend remains responsible for Work lifecycle
rules and SQL migrations.

Rust package versions live in the workspace and crate `Cargo.toml` files; tool
versions use the files described below. The [deployment dependency
catalog](dependencies.md) selects only the published goblinctl release and Docker
images.

## Tool versions and installation

| Tool | Version/source | Purpose |
| --- | --- | --- |
| Rust and Cargo | 1.95.0 in `rust-toolchain.toml`, matching Codex | Compiler and package manager |
| Clippy, rustfmt, rust-src | Components of that toolchain, matching Codex | Linting, formatting, standard-library source navigation |
| GNU Make | Supplied by Ubuntu/WSL build-essential and GitHub runners | Repository development targets |
| cargo-nextest | 0.9.103, matching Codex CI | Rust test runner |
| cargo-insta | 1.46.3, matching Codex's locked `insta` library | Review snapshot tests when used |
| DotSlash | 0.5.7 | Run checksum-pinned executable manifests when used |

Codex's installation guide leaves `cargo-insta` and DotSlash CLI versions
unpinned. Goblin pins those installations for reproducibility; DotSlash 0.5.7 was
the current published version when this setup was added. Neither helper is a
production dependency. Goblin currently has no Insta snapshots or DotSlash
manifests, so its required checks use Make and nextest.

On Ubuntu 24.04 / WSL2, install the system prerequisites:

```bash
sudo apt update
sudo apt install -y build-essential curl ca-certificates
```

From the repository root, install the toolchain and helpers as your development
user. The setup script is safe to rerun and uses the version in
`rust-toolchain.toml`; do not run it through `sudo`:

```bash
make setup-rust
source "$HOME/.cargo/env"
make install-rust
```

The script also installs `x86_64-unknown-linux-musl`, which matches Codex's Linux
x86-64 release target. Ordinary local builds use the GNU Linux host target.
Rust is installed per Linux user. To check the setup:

```bash
rustup show
rustup component list --installed
make --version
cargo nextest --version
cargo insta --version
dotslash --version
```

## Build, format, lint, and test

| Command | Behavior |
| --- | --- |
| `make build-native` | Build both Rust crates using the lockfile |
| `make goblinctl ARGS=--help` | Run the operator CLI from source |
| `make format-rust` | Format Rust, with one imported item per `use` |
| `make format-rust-check` | Check the same formatting without modifying files |
| `make lint-rust CARGO_ARGS="-p goblinctl"` | Lint a crate and its tests |
| `make lint-rust` | Run the CI lint checks |
| `make fix-rust CARGO_ARGS="-p goblinctl"` | Apply Clippy's automatic fixes; inspect the resulting diff |
| `make test-rust CARGO_ARGS="-p goblinctl"` | Run selected tests with nextest and then documentation tests |
| `make test-rust` | Run all Rust tests with nextest and then documentation tests |
| `make test-rust-doc` | Run Rust documentation tests, which nextest excludes |
| `make release-local` | Build the musl CLI and create the deterministic review archive |
| `cargo xtask release check-installer` | Check installer contracts and compiler input coverage after a native build |
| `cargo xtask release prepare --source-branch release/0.1` | In Actions, prepare an unpublished Goblin/goblinctl candidate from checked release-branch source |

Coordinated preparation supplies `GOBLINCTL_BUILD_VERSION` only to the isolated
native build. All installer version reporting uses that compiled value, defaulting
to the Cargo package version for ordinary builds. Packaging records the selected
version, original source SHA, shipping inputs, and archive checksum. It reads
license documents from the selected source, including when tooling runs from a
newer master commit. See [the release guide](releases.md).

`make format-rust` passes `--config imports_granularity=Item` directly to rustfmt, exactly
as Codex does. Keeping this option on the command line avoids the pinned stable
formatter ignoring it as an unstable TOML configuration setting. The hook,
Make targets, editor, and CI use the same formatting rule.

Both crates inherit Codex's general workspace Clippy rules. `clippy.toml` permits
`unwrap`/`expect` in tests and prohibits holding Tokio lock guards across awaits.
Production paths propagate contextual errors instead of calling `unwrap` or
`expect`. Codex's UI-color and SQLite-specific disallowed methods do not apply to
Goblin's Rust crates.

The nextest configuration matches Codex's default/local profile: an 8 MiB Rust
thread stack, a 30-second slow-test period with termination after two periods,
one retry, and JUnit output under `target/nextest/`. These retries apply only to
tests. Failed Goblin Work still requires an explicit retry through its core rules.
The pinned nextest version inherits custom profiles from `default` automatically;
Goblin omits Codex's explicit `inherits` key because 0.9.103 warns that it is unknown.
Avoid `--all-features` for routine local runs. Review snapshot differences before
using `cargo insta accept`; the tool is installed for tests that need it.

`make test` builds the full application and runs nextest plus Rust documentation
tests, generated-protocol checks, .NET tests, HTTP tests, and deployment tests.
Real PostgreSQL checks remain opt-in. `.github/workflows/checks.yml` runs the
Rust checks for pull requests and `master`; the release workflow uses the same
toolchain/helper setup action.

## Editor and debugging

Open the repository in a VS Code WSL window. Install its recommended extensions:

```bash
code --install-extension rust-lang.rust-analyzer
code --install-extension tamasfe.even-better-toml
code --install-extension vadimcn.vscode-lldb
```

These are Codex's Rust, TOML, and debugging recommendations. Their extension
versions are not pinned by Codex. Workspace settings enable Rust format-on-save,
Clippy checks including tests, and a separate `target/rust-analyzer` build
directory. TOML formatting preserves array order.

The **Debug goblinctl** launch configuration builds with full debug information
and starts with the harmless `--help` argument. Edit its arguments to debug a
specific command; administrative commands retain their existing privilege
requirements. **Attach to running goblinctl** supports inspecting a process you
own.

Development builds use Codex's line-table debug information. Set
`CARGO_PROFILE_DEV_DEBUG=full` or `CARGO_PROFILE_TEST_DEBUG=full` to inspect local
variables. The `dev-small`, `profiling`, and `ci-test` profiles follow Codex's
profile structure. Goblin retains its existing development optimization for the
600,000-iteration password KDF and its stripped, deterministic release binaries.

## Boundaries

The shared workflow uses Bash for Make recipes and Rust for `xtask`, preserving
Goblin's Python-free tooling. Codex's Python formatter wrapper, Bazel build graph,
custom nightly argument-comment compiler plugin, voice/V8 build dependencies,
and platform-specific test overrides belong to Codex's application. They are not
required by Goblin's Cargo workspace. In particular, Codex's Zig 0.14.0,
musl-tools, libcap, audio headers, and C++ tooling support native dependencies
that Goblin does not compile; Goblin's musl build is checked directly in CI.

All development tools stay on developer machines and CI runners. Production
receives the compiled `goblinctl` archive described in [the native tooling
guide](goblinctl.md), without Rust, Cargo, Make, nextest, or Python.

## Verification — 2026-09-27

The setup script installed all listed versions and a second run left them in
place. The WSL editor extensions were installed, and the debugger configuration
was checked against CodeLLDB's configuration schema. Formatting, Clippy, all
seven nextest cases, the documentation-test command, and the complete Stop hook
passed. The full `make test` suite, four setup browser journeys, and 14 focused
checks against the refreshed musl archive also passed. Repeated packaging produced
identical archive bytes, and the archive checksum agrees with the release pin
and both ARM templates.

Live PostgreSQL coverage remains unconfigured: 39 .NET database-dependent tests
and one HTTP test were skipped. GitHub workflows were not run, and no release was
published. See [the migration verification record](goblinctl.md#migration-verification--2026-09-27)
for the application/deployment checks and their remaining limits.
