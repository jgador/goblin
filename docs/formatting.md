# Formatting

The Codex Stop hook in `.codex/config.toml` selects formatters from staged,
unstaged, and non-ignored untracked changes, including changes from earlier
turns; deletions are ignored. Each selected formatter runs on its full language
scope:

| Files | Formatter |
| --- | --- |
| `.ts`, `.tsx`, `.mts`, `.cts` | `make typescript-format` (Prettier) |
| `.rs` | `make rust-format` for both Rust crates |
| `.cs` | `make backend-format`: .NET whitespace and style passes, excluding generated protocol and EF types |

Other files trigger no formatter. Run the commands manually when you change only
formatting configuration. After changing the hook, restart Codex, then review and
trust it with `/hooks`; the project must also be trusted. Formatter output goes to
stderr, while stdout stays JSON for the [Stop hook contract](https://learn.chatgpt.com/docs/hooks#stop).
The hook does not run tests.

Install Node.js 24+, the SDK from `global.json`, and the Rust tools from
`make rust-setup` (see [Rust development](rust-development.md)); run
`make install` for the pinned Prettier and TypeScript. The hook skips TypeScript
formatting when Prettier is missing. If Cargo is not on PATH, it adds
`$CARGO_HOME/bin`, or `$HOME/.cargo/bin` by default.

```bash
make format
# Or select a language:
make typescript-format
make rust-format
make backend-format
```

Prettier respects `.gitignore` and does not format embedded languages. Rust uses
`imports_granularity=Item`; `make rust-format-check` checks without writing. C#
uses `dotnet format whitespace` and `dotnet format style --severity info` on
`backend/Goblin.slnx`; the style pass excludes `IDE0130` and `IDE1006` (see the
[Makefile](../Makefile)).

## C# construction and contracts

Use regular constructors for class declarations and readonly fields for retained
dependencies. Constructor signatures and positional record declarations may change
during refactoring; do not keep them only because they exist. Prefer cohesive
types and clear construction APIs over long positional or optional parameter
lists. Preserve public property contracts, JSON behavior, and relied-upon record
semantics unless the task calls for a change. Update affected callers and tests
together, and identify any external compatibility impact.

Use classes for boundary DTOs, application projections, commands, configuration
carriers, and persistence/worker snapshots unless they need value or copy
semantics. Put JSON attributes on explicit properties. Small constructors plus
named initialization can make larger contracts clearer; a parameter count alone
does not justify a wrapper type.

Keep records where behavior relies on them. `ExecutionTarget` and its
`GitRepositoryGrant` use value comparison in repository approval and access checks.
`SetupFile` values are compared with `SequenceEqual` to detect changed setup
inputs. Work decisions and results, connection state, and `ProcessIdentity` use
`with` copies. The protocol `RequestId` and `ProtocolNull` have deliberate value
semantics. Arrays inside records still compare by reference; records do not give
structural array comparison or deep immutability.

Construction changes must preserve required properties, omitted/null defaults,
polymorphism, enum wire names, and serialization order where relied upon. Work
command fingerprints hash the serialized command, so a JSON change that looks
equivalent can change replay behavior. Test the raw wire shape and relevant
equality or copy behavior when you change these contracts.

### Typed JSON boundaries

Read known production payloads into purpose-specific DTOs with explicit
`JsonPropertyName` properties; ignore upstream fields Goblin does not use.
Preserve bounded HTTP buffering and object/syntax validation, unknown Kubernetes
readings, Slack event field-presence semantics, and existing response shapes.
Dynamic JSON stays appropriate for JSON-RPC envelopes, arbitrary generated schema
values, converters, schema generators, and wire-format tests. Core lifecycle types
must not depend on JSON or runtime protocols.

Separate C# type declarations, methods, constructors, and attributed members with
at least one blank line. Keep attributes on their own lines directly above the
declaration. Put enum braces and each enum value on separate lines; separate
attributed enum values with a blank line. Adjacent plain fields and properties may
stay grouped. These spacing rules also apply to generated C#; change the
generators when their output needs changes.

Generated Codex and Kubernetes classes belong to their schema selections and C#
generators. EF mappings belong to SQL and the scaffolding workflow. Do not format
these outputs by hand. Regenerate them and run the owning drift checks described
in [repository verification](repository-layout.md#choosing-verification) and the
[database guide](database.md#reverse-engineer-the-database).

## Deployment assets

Formatting does not rebuild deployment artifacts. After editing a bootstrap or
Bicep source, run `make deploy-generate` to write review assets to
`.artifacts/azure`. This needs the standalone Bicep CLI on PATH (CI uses 0.47.16).
Release preparation generates and verifies the assets it publishes. Changes to
setup assets embedded in goblinctl need a new goblinctl release and a pin update;
see [native tooling](goblinctl.md).
