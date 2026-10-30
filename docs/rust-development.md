# Rust development

Goblin uses a pinned Rust development workflow influenced by `openai/codex`.
Goblin's own configuration is authoritative. Its Cargo workspace lives at the
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
make rust-setup
source "$HOME/.cargo/env"
make rust-install
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
| `make rust-build` | Build both Rust crates using the lockfile |
| `make goblinctl-run ARGS=--help` | Run the operator CLI from source |
| `make rust-format` | Format Rust, with one imported item per `use` |
| `make rust-format-check` | Check the same formatting without modifying files |
| `make rust-lint CARGO_ARGS="-p goblinctl"` | Lint a crate and its tests |
| `make rust-lint` | Run the CI lint checks |
| `make rust-fix CARGO_ARGS="-p goblinctl"` | Apply Clippy's automatic fixes; inspect the resulting diff |
| `make rust-test CARGO_ARGS="-p goblinctl"` | Run selected tests with nextest and then documentation tests |
| `make rust-test` | Run all Rust tests with nextest and then documentation tests |
| `make rust-test-doc` | Run Rust documentation tests, which nextest excludes |
| `make release-package-local` | Build the musl CLI and deterministic review archive |

See [the release guide](releases.md) for installer checks and coordinated candidate preparation, verification, and authentication.

Coordinated preparation supplies `GOBLINCTL_BUILD_VERSION` only to the isolated
native build. All installer version reporting uses that compiled value, defaulting
to the Cargo package version for ordinary builds. Packaging records the selected
version, original source SHA, shipping inputs, and archive checksum. It reads
license documents from the selected source, including when tooling runs from a
newer master commit. See [the release guide](releases.md).

`make rust-format` passes `--config imports_granularity=Item` directly to rustfmt, exactly
as Codex does. Keeping this option on the command line avoids the pinned stable
formatter ignoring it as an unstable TOML configuration setting. The hook,
Make targets, editor, and CI use the same formatting rule.

Both crates inherit Codex's general workspace Clippy rules. `clippy.toml` permits
`unwrap`/`expect` in tests and prohibits holding Tokio lock guards across awaits.
Production paths propagate contextual errors instead of calling `unwrap` or
`expect`. Codex's UI-color and SQLite-specific disallowed methods do not apply to
Goblin's Rust crates.

Keep the shared Clippy rules enabled. Use inline format arguments and method
references when clear, and prefer exhaustive matches. Avoid opaque boolean and
`Option` arguments in new APIs; express the caller's intent with cohesive types.

The nextest configuration matches Codex's default/local profile: an 8 MiB Rust
thread stack, a 30-second slow-test period with termination after two periods,
one retry, and JUnit output under `.artifacts/nextest/`. These retries apply only to
tests. Failed Goblin Work still requires an explicit retry through its core rules.
The pinned nextest version inherits custom profiles from `default` automatically;
Goblin omits Codex's explicit `inherits` key because 0.9.103 warns that it is unknown.
Avoid `--all-features` for routine local runs. Review snapshot differences before
using `cargo insta accept`; the tool is installed for tests that need it.

`make test` builds the full application and runs nextest plus Rust documentation
tests, generated-protocol checks, .NET tests, HTTP tests, and deployment tests.
Real PostgreSQL checks remain opt-in. `.github/workflows/ci.yml` selects Rust
formatting/lints/tests for Rust inputs and retains integration, installer, and
installation-browser checks for language changes. Documentation-only runs skip
runtime verification; shared/unclassified inputs select all groups. See
[CI selection](github-actions.md#ci-selection-and-required-result). Release CI uses the same
toolchain/helper setup action.

## Editor and debugging

Open the repository in VS Code through WSL and use the recommended Rust,
TOML, and LLDB extensions. Workspace settings enable Rust format-on-save and
Clippy checks, with a separate rust-analyzer target directory. **Debug goblinctl**
starts with `--help`; edit arguments for other commands. Administrative commands
still require their usual privileges. The attach configuration targets a process
you own.

Development builds use line-table debug information. Set
`CARGO_PROFILE_DEV_DEBUG=full` or `CARGO_PROFILE_TEST_DEBUG=full` to inspect local
variables. Release binaries remain stripped and deterministic.

## Boundaries

Make recipes use Bash and `xtask` uses Rust; operational tooling remains
Python-free. Codex-specific tools and native dependencies are not requirements
for Goblin's Cargo workspace. The musl build is checked directly in CI.

All development tools stay on developer machines and CI runners. Production
receives the compiled `goblinctl` archive described in [the native tooling
guide](goblinctl.md), without Rust, Cargo, Make, nextest, or Python.

## Verification

Run the formatting, lint, and nextest/documentation-test commands above for the
affected crate. Installer changes also need offline packaging and deployment
checks. Report checks actually performed; historical counts and a matching
archive checksum do not verify today's executable behavior. PostgreSQL and
authenticated runtime checks remain separate integration coverage.
