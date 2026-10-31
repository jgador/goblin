# Formatting

The Codex Stop hook in `.codex/config.toml` picks formatters from staged, unstaged,
and untracked (not ignored) changes; deletions are ignored. Each formatter runs on
its whole language scope:

| Files | Formatter |
| --- | --- |
| `.ts`, `.tsx`, `.mts`, `.cts` | `make typescript-format` (Prettier) |
| `.rs` | `make rust-format` (whole Cargo workspace) |
| `.cs` | `make backend-format`: `dotnet format` whitespace and style, excluding generated protocol and EF files |

Other files trigger no formatter, and the hook runs no tests. After changing the
hook, restart Codex and trust it with `/hooks`; the project must also be trusted.
Formatter output goes to stderr; stdout stays JSON for the
[Stop hook contract](https://learn.chatgpt.com/docs/hooks#stop).

Install Node.js 24+, the .NET SDK from `global.json`, and the Rust tools from
`make rust-setup` (see [Rust development](rust-development.md)); run `make install`
for Prettier. The hook skips TypeScript formatting when Prettier is missing, and
adds `${CARGO_HOME:-$HOME/.cargo}/bin` to PATH when Cargo is not found.

```bash
make format
# Or one language:
make typescript-format
make rust-format
make backend-format
```

Run these manually when you change only formatting configuration. Prettier
respects `.gitignore` and does not format embedded languages. Rust uses
`imports_granularity=Item`; `make rust-format-check` checks without writing. The C#
commands and excluded diagnostics are in the [Makefile](../Makefile).

## C# construction and contracts

Use regular constructors for classes and readonly fields for retained dependencies.
Constructor signatures and positional record declarations may change during
refactoring. Prefer cohesive types and clear construction APIs over long positional
or optional parameter lists. Preserve public property contracts, JSON behavior, and
relied-upon record semantics unless the task calls for a change. Update affected
callers and tests together, and identify any external compatibility impact.

Use classes for boundary DTOs, application projections, commands, configuration
carriers, and persistence or worker snapshots unless they need value or copy
semantics. Put JSON attributes on explicit properties. Small constructors plus
named initialization can make larger contracts clearer; a parameter count alone
does not justify a wrapper type.

Keep records where behavior relies on them. `ExecutionTarget` and its
`GitRepositoryGrant` use value comparison in repository approval and access checks.
`SetupFile` values are compared with `SequenceEqual` to detect changed setup inputs.
Work decisions and results, connection state, and `ProcessIdentity` use `with`
copies. The protocol `RequestId` and `ProtocolNull` have value equality on purpose.
Arrays inside records still compare by reference; records give no deep equality or
immutability.

Construction changes must preserve required properties, omitted and null defaults,
polymorphism, enum wire names, and serialization order where relied upon. Work
command fingerprints hash the serialized command, so a small JSON change can change
replay behavior. Test the raw wire shape and relevant equality or copy behavior
when you change these contracts.

### Typed JSON boundaries

Read known production payloads into purpose-specific DTOs with explicit
`JsonPropertyName` properties; ignore upstream fields Goblin does not use. Preserve
bounded HTTP buffering and object and syntax validation, unknown Kubernetes
readings, Slack event field-presence rules, and existing response shapes. Dynamic
JSON is fine for JSON-RPC envelopes, arbitrary generated schema values, converters,
schema generators, and wire-format tests. Core lifecycle types must not depend on
JSON or runtime protocols.

Separate C# type declarations, methods, constructors, and attributed members with
at least one blank line. Keep attributes on their own lines directly above the
declaration. Put enum braces and each enum value on separate lines; separate
attributed enum values with a blank line. Adjacent plain fields and properties may
stay grouped. These rules also apply to generated C#; change the generators when
their output needs to change.

Do not format generated Codex protocol, Kubernetes, or EF classes by hand.
Regenerate them and run the owning drift checks in
[repository verification](repository-layout.md#choosing-verification) and the
[database guide](database.md#reverse-engineer-the-database).

## Deployment assets

Formatting does not rebuild deployment artifacts. After editing a bootstrap or
Bicep source, run `make deploy-generate` to write review assets to
`.artifacts/azure`. It needs the standalone Bicep CLI on PATH (CI uses 0.47.16).
Setup assets embedded in goblinctl need a new goblinctl release and a pin update;
see [goblinctl](goblinctl.md#releases).
