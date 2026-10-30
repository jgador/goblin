# Formatting

The Codex Stop hook in `.codex/config.toml` selects formatters from staged,
unstaged, and non-ignored untracked changes, including earlier turns; deletions
are excluded. Each selected formatter runs on its full language scope:

| Files | Formatter |
| --- | --- |
| `.ts`, `.tsx`, `.mts`, `.cts` | Prettier for repository TypeScript |
| `.rs` | `make rust-format` for both Rust crates |
| `.cs` | .NET whitespace and style passes for the backend solution, excluding generated protocol and EF types |

Other files trigger no formatter. Run the manual commands when changing formatting
configuration alone. After changing the hook, restart Codex and review and trust it
with `/hooks`; the project must also be trusted. Formatter output goes to stderr,
while stdout remains JSON for the [Stop hook contract](https://learn.chatgpt.com/docs/hooks#stop).

Install Node.js 24+, the SDK from `global.json`, and Rust tools via `make rust-setup`
(see [Rust development](rust-development.md)); run `make install` for pinned
Prettier and TypeScript dependencies. Missing Prettier is reported as a skip. If
Cargo is absent from PATH, the Rust hook adds `$CARGO_HOME/bin`, or
`$HOME/.cargo/bin` by default.

```bash
make format
# Or select a language:
make typescript-format
make rust-format
make backend-format
```

Prettier respects `.gitignore` and skips embedded-language formatting. Rust uses
`imports_granularity=Item`; `make rust-format-check` checks without writing. C#
uses `dotnet format whitespace` and `dotnet format style --severity info` on
`backend/Goblin.slnx`; the style pass excludes `IDE0130` and `IDE1006` (see
[Makefile](../Makefile)). The hook does not run tests.

## C# construction and contracts

Use regular constructors for class declarations and readonly fields for retained
dependencies. Constructor signatures and positional record declarations may change
as part of refactoring; do not preserve them solely because they already exist.
Prefer cohesive types and clear construction APIs over long positional or optional
parameter lists. Preserve public property contracts, JSON behavior, and relied-upon
record semantics by default; change them when the task calls for it. Update affected
callers and relevant tests together, and identify any external compatibility impact.

Use classes for boundary DTOs, application projections, commands, configuration
carriers, and persistence/worker snapshots unless they require value or copy
semantics. Put JSON attributes on explicit properties. Small constructors plus
named initialization can make larger contracts clearer; a parameter-count
threshold alone does not justify an arbitrary wrapper.

Retain records where behavior relies on them. `ExecutionTarget` and its repository
grant use value comparisons during claim/restore validation. `SetupFile` values
are compared with `SequenceEqual` to detect changed preparation inputs. Work
decisions/results, connection state, execution observations, and `ProcessIdentity`
use immutable replacement or `with` copies. Protocol request IDs and `ProtocolNull`
have deliberate value semantics. Arrays inside records still have shallow equality;
records do not imply structural array comparison or deep immutability.

Construction changes must preserve required properties, omitted/null defaults,
polymorphism, enum wire names, and serialization order where relied upon. Command
replay fingerprints hash serialized content, so an apparently equivalent JSON
refactor can change replay behavior. Test raw wire shape and relevant equality or
copy behavior when changing these contracts.

### Typed JSON boundaries

Read known production payloads into purpose-specific DTOs with explicit
`JsonPropertyName` properties; ignore upstream fields Goblin does not consume.
Preserve bounded HTTP buffering and object/syntax validation, unknown Kubernetes
readings, Slack event field-presence semantics, and existing response shapes.
Repository workers, Codex results, and workspace inspection use typed contracts.
Dynamic JSON remains appropriate for JSON-RPC envelopes, arbitrary generated
schema values, converters, schema generators, and wire-format tests. Core
lifecycle types must not depend on JSON or runtime protocols.

Separate C# type declarations, methods, constructors, and attributed members with
at least one blank line. Keep attributes on their own lines directly above the
declaration they annotate. Put enum braces and each enum value on separate lines;
separate attributed enum values with a blank line. Adjacent plain fields and
properties may remain grouped. Formatting alone should not change record shape;
refactoring may replace positional declarations with explicit construction APIs
while preserving the required behavior and contracts. These spacing rules also
apply to generated C#; update the generators when their output needs changes.

Generated Codex and Kubernetes classes belong to their schema selections and C#
generators. EF mappings belong to SQL and the scaffolding workflow. Do not manually
format these outputs or revive removed generator implementations. Regenerate and
run the owning drift checks described in [repository verification](repository-layout.md#choosing-verification)
and the [database guide](database.md#reverse-engineer-the-database).

## Deployment assets

Formatting does not rebuild deployment artifacts. After editing a bootstrap or
Bicep source, run `make deploy-generate` to generate review assets in `.artifacts/azure`.
Release preparation generates and verifies the assets it publishes. This requires the standalone Bicep CLI on PATH (version 0.47.16 is used in
CI). Changes to embedded setup assets require a new goblinctl release and a pin
update; see [native tooling](goblinctl.md). The hook does not run tests automatically.
