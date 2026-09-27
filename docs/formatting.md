# Formatting

The repository-local Codex Stop hook in `.codex/config.toml` checks Git changes
after each completed turn and runs only the relevant language formatters. It
includes staged, unstaged, and untracked files, excluding ignored untracked files
and deletions. This covers all pending changes in the working tree, including
changes from earlier turns.

| Changed files | Formatter |
| --- | --- |
| `.ts`, `.tsx`, `.mts`, `.cts` | Prettier for all repository TypeScript |
| `.rs`, `justfile` | `just fmt` for the justfile and both Rust crates |
| `.cs`, excluding generated protocol types and EF mappings | .NET whitespace and style passes for the backend solution |

Changes confined to other files skip all formatters. Each selected formatter still
formats its existing full scope; Git changes select languages, not individual files.
Run the manual commands below when changing formatting configuration alone.

Restart Codex and use `/hooks` to review and trust the hook after changing its
definition. The project must also be trusted. Formatter output goes to stderr;
stdout remains JSON as required by the [OpenAI Docs for Stop hooks](https://learn.chatgpt.com/docs/hooks#stop).

Install Node.js 24+, the .NET SDK from `global.json`, and the tools in the
[Rust development guide](rust-development.md) using `bash scripts/setup-rust.sh`.
`rust-toolchain.toml` pins Rust, rustfmt, Clippy, and rust-src; `Cargo.lock` pins dependencies.
Run `npm ci` to install the pinned Prettier and TypeScript dependencies.
If Prettier is unavailable, the hook reports a skip when TypeScript files change.
When Rust formatting is needed and Cargo or just is absent from its inherited
`PATH`, the hook adds rustup's tool directory
(`$CARGO_HOME/bin`, or `$HOME/.cargo/bin` by default). This also covers Codex
processes started before Rust was installed.

```bash
npm run format:typescript
npm run format:rust
```

Prettier formats TypeScript and respects `.gitignore`. Embedded-language formatting
is disabled to preserve HTML/CSS/JavaScript strings in UI and test fixtures.
`npm run format:rust` invokes `just fmt`, which formats the justfile and both Rust
crates with Codex's `imports_granularity=Item` setting. `just fmt-check` verifies
the same rules without writing files.

When C# files change, the hook runs `dotnet format whitespace` and `dotnet format
style --severity info` on `backend/Goblin.slnx`, excluding generated protocol types
and EF mappings.
The style pass excludes `IDE0130` and `IDE1006`, as recorded in the
[generated C# formatting audit](generated-csharp-formatting.md).

Formatting does not rebuild deployment artifacts. After editing a bootstrap or
Bicep source, run `cargo xtask azure`; check drift with `cargo xtask azure --check`.
These commands require the standalone Bicep CLI on PATH (version 0.47.16 is used in
CI). Changes to embedded setup assets require a new goblinctl release and a pin
update; see [native tooling](goblinctl.md). The hook does not run tests automatically.
