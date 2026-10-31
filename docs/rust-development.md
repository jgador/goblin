# Rust development

Goblin's Cargo workspace lives at the repository root, with the operator CLI in
`tools/goblinctl` and build and release tasks in `tools/xtask` (run as
`cargo xtask`). The workflow follows `openai/codex` conventions, but Goblin's own
configuration is authoritative. The .NET backend keeps Work lifecycle rules and
SQL migrations.

Rust package versions live in the workspace and crate `Cargo.toml` files. The
[deployment dependency catalog](dependencies.md) selects only the published
goblinctl release and Docker images.

## Tool versions and installation

| Tool | Version/source | Purpose |
| --- | --- | --- |
| Rust and Cargo | 1.95.0 in `rust-toolchain.toml` | Compiler and package manager |
| Clippy, rustfmt, rust-src | Components in `rust-toolchain.toml` | Linting, formatting, standard-library source |
| GNU Make | Ubuntu/WSL `build-essential` and GitHub runners | Repository targets |
| cargo-nextest | 0.9.103 | Rust test runner |
| cargo-insta | 1.46.3 | Snapshot review; Goblin has no Insta snapshots yet |
| DotSlash | 0.5.7 | Checksum-pinned executable manifests; Goblin has none yet |

On Ubuntu 24.04 / WSL2, install the system prerequisites:

```bash
sudo apt update
sudo apt install -y build-essential curl ca-certificates
```

From the repository root, install the toolchain and helpers as your development
user. `make rust-setup` runs `scripts/setup-rust.sh`, which is safe to rerun and
uses the version in `rust-toolchain.toml`. Do not run it through `sudo`:

```bash
make rust-setup
source "$HOME/.cargo/env"
make rust-install
```

The script also adds the `x86_64-unknown-linux-musl` target used for release
builds. Ordinary local builds use the GNU Linux host target. Rust is installed
per Linux user. To check the setup:

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
| `make rust-format-check` | Check the same formatting without changing files |
| `make rust-lint CARGO_ARGS="-p goblinctl"` | Lint a crate and its tests, with warnings denied |
| `make rust-lint` | Run the CI lint checks |
| `make rust-fix CARGO_ARGS="-p goblinctl"` | Apply Clippy's automatic fixes; review the diff |
| `make rust-test CARGO_ARGS="-p goblinctl"` | Run selected tests with nextest, then documentation tests |
| `make rust-test` | Run all Rust tests with nextest, then documentation tests |
| `make rust-test-doc` | Run only documentation tests, which nextest skips |
| `make release-package-local` | Build the musl CLI and a deterministic review archive |

See [the release guide](releases.md) for installer checks and candidate
preparation, and [environment variables](environment-variables.md) for
`GOBLINCTL_BUILD_VERSION`.

`make rust-format` passes `--config imports_granularity=Item` on the rustfmt
command line, because the stable formatter ignores this unstable option in a TOML
file. The hook, Make targets, VS Code settings, and CI use the same rule.

Both crates use the shared workspace Clippy rules in the root `Cargo.toml`; keep
them enabled. `clippy.toml` allows `unwrap`/`expect` in tests and forbids holding
Tokio lock guards across `await`. Production code returns errors with context
instead of calling `unwrap` or `expect`. Use inline format arguments and method
references when clear, and prefer exhaustive matches. Avoid opaque boolean and
`Option` arguments in new APIs; use types that state the caller's intent.

`make rust-test` sets an 8 MiB thread stack and uses the `local` nextest profile
from `.config/nextest.toml`: a 30-second slow-test period with termination after
two periods, one retry, and JUnit output under `.artifacts/nextest/`. These
retries apply only to tests; failed Goblin Work still needs an explicit retry
through its core rules.

`make test` includes the Rust tests. See [test selection](repository-layout.md#choosing-verification)
and [CI selection](github-actions.md#ci-selection-and-required-result).

## Editor and debugging

Open the repository in VS Code through WSL and install the recommended Rust,
TOML, and LLDB extensions. Workspace settings enable Rust format-on-save and
Clippy checks, with a separate rust-analyzer target directory. **Debug goblinctl**
starts with `--help`; edit the arguments for other commands. Administrative
commands still need their usual privileges. **Attach to running goblinctl**
attaches to a process you select.

Development builds use line-table debug information. Set
`CARGO_PROFILE_DEV_DEBUG=full` or `CARGO_PROFILE_TEST_DEBUG=full` to inspect local
variables. Release binaries are stripped.

## Boundaries

Make recipes use Bash and `xtask` uses Rust; operational tooling stays
Python-free. All development tools stay on developer machines and CI runners.
Production receives the compiled `goblinctl` archive described in [the native
tooling guide](goblinctl.md), without Rust, Cargo, Make, nextest, or Python.

## Verification

Run the formatting, lint, and test commands above for the affected crate.
Installer changes also need the offline packaging and deployment checks. Report
only checks you ran; an earlier run or a matching archive checksum does not verify
today's behavior. PostgreSQL and authenticated runtime checks are separate
integration coverage.
