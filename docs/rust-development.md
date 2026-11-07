# Rust development

The root [Cargo workspace](../Cargo.toml) contains the operator CLI in `tools/goblinctl`
and developer tasks in `tools/xtask`. Work rules and SQL migrations remain in .NET.
Operational tooling stays Python-free; development tools are excluded from production.

## Tool versions and installation

[rust-toolchain.toml](../rust-toolchain.toml) owns the compiler and components.
[setup-rust.sh](../scripts/setup-rust.sh) pins helper tools and release targets. Install
the Ubuntu/WSL prerequisites:

```bash
sudo apt update
sudo apt install -y build-essential curl ca-certificates
make rust-setup
source "$HOME/.cargo/env"
make rust-install
```

Run setup as the development user, without sudo. It is safe to rerun. Ordinary builds
use the host target; release packaging uses the script's musl target.

## Build, format, lint, and test

`make help` lists the Rust targets and `cargo xtask --help` lists developer operations.
Scope verification to a changed crate:

```bash
make rust-format
make rust-lint CARGO_ARGS="-p goblinctl"
make rust-test CARGO_ARGS="-p goblinctl"
```

Omit `CARGO_ARGS` for the workspace. Rust tests include documentation tests, which
nextest does not run itself. The [nextest configuration](../.config/nextest.toml) and
[Makefile](../Makefile) own profiles, timeouts, test retries, stack size, and report
paths. Test-runner retries never authorize retries of failed Work.

The formatter uses one imported item per `use`; the command-line option is shared by
Make, the editor, and the Stop hook because stable rustfmt ignores it in TOML. Workspace
Clippy rules and [clippy.toml](../clippy.toml) are authoritative. Production code
returns errors with context instead of `unwrap` or `expect`. Prefer exhaustive matches
and meaningful types over opaque boolean arguments.

## Editor and debugging

Open VS Code through WSL with the recommended extensions and checked-in
[settings](../.vscode/settings.json) and
[launch configurations](../.vscode/launch.json). Administrative commands still need
their normal privileges.

Development builds use line-table debug information. Set `CARGO_PROFILE_DEV_DEBUG=full`
or `CARGO_PROFILE_TEST_DEBUG=full` to inspect local variables; release binaries are
stripped.

Installer changes also need
[packaging and deployment checks](repository-layout.md#choosing-verification). See
[native tooling](goblinctl.md) and [releases](releases.md) for distribution, approval,
and embedded-asset boundaries.
