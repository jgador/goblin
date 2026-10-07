# Formatting

The repository-local Codex Stop hook in `.codex/config.toml` checks Git changes
after each completed turn and runs only the relevant language formatters. It
includes staged, unstaged, and untracked files, excluding ignored untracked files
and deletions. This covers all pending changes in the working tree, including
changes from earlier turns.

| Changed files | Formatter |
| --- | --- |
| `.ts`, `.tsx`, `.mts`, `.cts` | Prettier for all repository TypeScript |
| `.rs` | `make format-rust` for both Rust crates |
| `.cs`, excluding generated protocol types and EF mappings | .NET whitespace and style passes for the backend solution |

Changes confined to other files skip all formatters. Each selected formatter still
formats its existing full scope; Git changes select languages, not individual files.
Run the manual commands below when changing formatting configuration alone.

Restart Codex and use `/hooks` to review and trust the hook after changing its
definition. The project must also be trusted. Formatter output goes to stderr;
stdout remains JSON as required by the [OpenAI Docs for Stop hooks](https://learn.chatgpt.com/docs/hooks#stop).

Install Node.js 24+, the .NET SDK from `global.json`, and the tools in the
[Rust development guide](rust-development.md) using `make setup-rust`.
`rust-toolchain.toml` pins Rust, rustfmt, Clippy, and rust-src; `Cargo.lock` pins dependencies.
Run `make install` to install the pinned Prettier and TypeScript dependencies.
If Prettier is unavailable, the hook reports a skip when TypeScript files change.
When Rust formatting is needed and Cargo is absent from its inherited
`PATH`, the hook adds rustup's tool directory
(`$CARGO_HOME/bin`, or `$HOME/.cargo/bin` by default). This also covers Codex
processes started before Rust was installed.

```bash
make format
# Or select a language:
make format-typescript
make format-rust
make format-backend
```

Prettier formats TypeScript and respects `.gitignore`. Embedded-language formatting
is disabled to preserve HTML/CSS/JavaScript strings in UI and test fixtures.
`make format-rust` formats both Rust crates with Codex's
`imports_granularity=Item` setting. `make format-rust-check` verifies
the same rules without writing files.

When C# files change, the hook runs `dotnet format whitespace` and `dotnet format
style --severity info` on `backend/Goblin.slnx`, excluding generated protocol types
and EF mappings.
The style pass excludes `IDE0130` and `IDE1006`, as recorded in the
[generated C# formatting audit](generated-csharp-formatting.md).

Separate C# type declarations, methods, constructors, and attributed members with
at least one blank line. Keep attributes on their own lines directly above the
declaration they annotate. Put enum braces and each enum value on separate lines;
separate attributed enum values with a blank line. Adjacent plain fields and
properties may remain grouped. Formatting alone should not change record shape;
refactoring may replace positional declarations with explicit construction APIs
while preserving the required behavior and contracts. These spacing rules also
apply to generated C#; update the generators when their output needs changes.

Formatting does not rebuild deployment artifacts. After editing a bootstrap or
Bicep source, run `make deploy-generate` to generate review assets in `.artifacts/azure`.
Release preparation generates and verifies the assets it publishes. This requires the standalone Bicep CLI on PATH (version 0.47.16 is used in
CI). Changes to embedded setup assets require a new goblinctl release and a pin
update; see [native tooling](goblinctl.md). The hook does not run tests automatically.
