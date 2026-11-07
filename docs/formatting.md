# Formatting

[Makefile](../Makefile) owns formatter commands and exclusions. Run `make format` or a
language target from `make help`. [.editorconfig](../.editorconfig) owns shared style
settings.

The [Codex Stop hook](../.codex/config.toml) selects formatters from pending changes. It
formats a whole language scope and runs no tests. Configuration-only changes may need
manual formatting. After hook edits, restart Codex and trust the hook and project. Keep
stdout valid for the hook response and diagnostics on stderr.

## C# construction and contracts

Use regular constructors for classes and readonly fields for retained dependencies.
Constructor signatures and positional record declarations may change during refactoring.
Prefer cohesive types and clear construction APIs over long positional or optional
parameter lists. Preserve public property contracts, JSON behavior, and relied-upon
record semantics unless the task calls for a change. Update affected callers and tests
together, and identify any external compatibility impact.

Use classes for boundary DTOs, application projections, commands, configuration
carriers, and persistence or worker snapshots unless they need value or copy semantics.
Put JSON attributes on explicit properties. Small constructors plus named initialization
can make larger contracts clearer; a parameter count alone does not justify a wrapper
type.

Keep records where equality or `with` copy behavior is relied upon. Arrays inside
records still compare by reference; records provide neither deep equality nor automatic
immutability. Test relevant value and copy behavior when changing types.

Construction changes must preserve required properties, omitted and null defaults,
polymorphism, enum wire names, and serialization order where relied upon. Work command
fingerprints hash the serialized command, so a small JSON change can change replay
behavior. Test the raw wire shape and relevant equality or copy behavior when you change
these contracts.

### Typed JSON boundaries

Read known production payloads into purpose-specific DTOs with explicit
`JsonPropertyName` properties; ignore upstream fields Goblin does not use. Preserve
bounded HTTP buffering and object and syntax validation, unknown Kubernetes readings,
Slack event field-presence rules, and existing response shapes. Dynamic JSON is fine for
JSON-RPC envelopes, arbitrary generated schema values, converters, schema generators,
and wire-format tests. Core lifecycle types must not depend on JSON or runtime
protocols.

Separate C# type declarations, methods, constructors, and attributed members with at
least one blank line. Keep attributes on their own lines directly above the declaration.
Put enum braces and each enum value on separate lines; separate attributed enum values
with a blank line. Adjacent plain fields and properties may stay grouped. These rules
also apply to generated C#; change the generators when their output needs to change.

Do not format generated Codex protocol, Kubernetes, or EF classes by hand. Regenerate
them and run the owning drift checks in
[repository verification](repository-layout.md#choosing-verification) and the
[database guide](database.md#reverse-engineer-the-database).

## Deployment assets

Formatting does not rebuild deployment artifacts. After editing a bootstrap or Bicep
source, run `make deploy-generate` to write review assets to `.artifacts/azure`. It
needs the standalone Bicep CLI on PATH; the
[verification setup](../.github/actions/setup-verification/action.yml) owns its CI pin.
Setup assets embedded in goblinctl need a new goblinctl release and a pin update; see
[goblinctl](goblinctl.md#source-and-release-boundaries).
