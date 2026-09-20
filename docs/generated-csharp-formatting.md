# Generated C# formatting audit

## Current generator status

The constructor refactor applies the protocol generator follow-up described
below and emits regular constructors for all generated wrapper classes.
Protocol regeneration now preserves the audited formatting. EF scaffolding also
formats its output with the repository's current settings, which prefer regular
constructors. The historical primary-constructor change below is superseded.

## Original audit

Audited on 2026-09-20 with .NET SDK 10.0.301 and the repository's
`.editorconfig`. The scope was the 1,145 checked-in protocol C# files and eight
EF Core context/entity files.

Whitespace formatting required no changes. Style formatting changed 22 files:
17 protocol files and five EF files. No schema, JSON attribute, database mapping
attribute, or SQL migration changed.

## Changes and generator support

| Output | Formatter change | Can generation produce it? |
| --- | --- | --- |
| Protocol union converters | 53 reader copies use `Utf8JsonReader candidate` instead of `var candidate`. | Yes: change the reader declaration emitted by `Generator.union()`. |
| Protocol union converters | 53 deserialized values declare their concrete type instead of `var`. | Yes: `Generator.union()` already knows the type as `inner`. |
| Protocol union converters | 39 separate null checks become `Deserialize<T>(...) ?? throw new JsonException(...)`. | Yes: reuse the generator's existing distinction between nullable reference results and value/enum results. Keep the same exception message and update `reader` only after successful deserialization. |
| `RequestIdJsonConverter` | `TryGetInt64(out var number)` becomes `TryGetInt64(out long number)`. | Yes: change the literal template in `Generator.request_id()`. |
| Four EF entities | Seven `new List<T>()` navigation-property initializers become `[]`. | Yes: customize EF Core's entity scaffolding template, or format the generated output after scaffolding. |
| `GoblinDbContext` | The constructor becomes `GoblinDbContext(DbContextOptions<GoblinDbContext> options) : DbContext(options)`. | Yes: customize EF Core's context scaffolding template, or format the generated output after scaffolding. |

For example, inside a generated union branch's `try` block:

```csharp
GranularAskForApproval value = JsonSerializer.Deserialize<GranularAskForApproval>(ref candidate, options)
    ?? throw new JsonException("Expected a non-null union value.");
reader = candidate;
return value;
```

The example is wrapped for readability; `dotnet format` leaves the declaration
and throw expression on one line.

## Generator follow-up

The protocol changes belong in
[`generate-protocol.py`](../backend/scripts/generate-protocol.py), specifically
`union()` and `request_id()`. A trial edit to those two methods reproduced all
1,145 formatted protocol files byte for byte and left `manifest.json` unchanged.
The trial patch was prepared for review; it has not been applied to the generator.

The EF output comes from `dotnet ef dbcontext scaffold`, invoked by
[`scaffold-database.sh`](../backend/scripts/scaffold-database.sh). Its existing
postprocessing normalizes only encoding and line endings. EF Core's built-in
templates are external to this repository. Custom T4 scaffolding templates would
produce these styles directly; a formatter step scoped to generated output is
another option. Neither approach requires database or SQL changes.

The checked-in generator and scaffolding script remain unchanged for this audit.
Regenerating now would undo these style edits. `npm run protocol:check` therefore
reports drift in the 17 protocol files until the protocol generator is updated.

## Verification method

The initial solution-level formatting passes with generated-file selection
reported no changes. To avoid treating that result as proof of style compliance,
the audit used compilable temporary copies with the protocol generated header
removed and `.g.cs` filenames changed to `.cs`. It retained the repository's
style configuration, target framework, and EF package versions, and restored
dependencies before running the style fixes. The original headers and filenames
were preserved when applying the output. Formatting the EF project directly
produced the same EF changes.

Style checks used `--severity info`, excluding `IDE0130` and `IDE1006` as in the
earlier handwritten-code formatting pass. Namespace alignment and naming changes
are outside this audit. Silent style hints and third-party analyzer fixes were
not included. Final style verification of the temporary copies passed, and
whitespace verification passed for both actual projects.

`dotnet test backend/Goblin.slnx --nologo` built the solution and passed all 112
enabled .NET tests, including 37 protocol tests. The 18 opt-in PostgreSQL tests
were skipped because test connections are not configured. All six Python
generator tests passed. `npm run protocol:check` then failed on the expected
17-file output drift described above. The full `npm test` workflow was not run
because it requires that drift check to pass. `git diff --check` passed.
