# Wolverine storage integrations

Goblin compiles these maintained integration projects directly through project
references. They come from Wolverine 6.39.1 at
[`5a7aa99b5c4f2b29b56300cc5f69919d54a370e7`](https://github.com/JasperFx/wolverine/tree/5a7aa99b5c4f2b29b56300cc5f69919d54a370e7).
The package repository metadata identifies this revision. The MIT license and
copyright notice are retained in [LICENSE](LICENSE); the parent Goblin Apache
license does not replace them. The source projects explicitly override inherited
package license metadata with MIT. Their license is copied to output and publish
directories as `licenses/wolverine/LICENSE`, and into the application image.

## Maintained scope

The complete source of `src/Persistence/Wolverine.RDBMS`,
`src/Persistence/Wolverine.Postgresql`, and
`src/Persistence/Wolverine.EntityFrameworkCore` is retained. These projects use no
linked shared source outside their project directories. Their assembly attributes,
source-generated JSON contexts, schema definitions, and runtime code-generation
frames are included. Upstream test projects, samples, generated `bin`/`obj` files,
central package management, test build targets, packaging artwork, and development
analyzers are excluded. The local `Directory.Build.props` replaces upstream build
configuration, imports Goblin's target framework, enables upstream implicit usings
and C# 12, retains the 6.39.1 version, and supplies the MIT metadata/license asset.

Imported source has trailing whitespace and excess final blank lines normalized.
Behavioral source changes are limited to canonical singular SQL identifiers:

| Constant | Identifier |
| --- | --- |
| `IncomingTable` | `wolverine_incoming_envelope` |
| `OutgoingTable` | `wolverine_outgoing_envelope` |
| `DeadLetterTable` | `wolverine_dead_letter` |
| `ControlQueueTableName` | `wolverine_control_queue` |
| `NodeTableName` | `wolverine_node` |
| `NodeRecordTableName` | `wolverine_node_record` |
| `NodeAssignmentsTableName` | `wolverine_node_assignment` |
| `AgentRestrictionsTableName` | `wolverine_agent_restriction` |

Optional retained implementations use `wolverine_tenant`, `wolverine_listener`,
`wolverine_recurring_message`, and `wolverine_tenant_partition`.
`wolverine_deduplication` is already singular. These features remain disabled and
add no tables to Goblin's baseline. The PostgreSQL queue transport uses singular
`wolverine_queue_<name>` tables but is not enabled by Goblin. Schema names and
column/CLR collection names are outside this rename.

No naming extension in the pinned source overrides all storage paths. Changing
schema configuration alone does not rename tables. `DatabaseSettings` renders
canonical constants into SQL without any name translation. RDBMS constants feed
schema objects, foreign keys, indexes, node registration/heartbeat/history,
assignments/restrictions, control transport, envelope batching, conflict detection,
recovery, pruning, and dead-letter replay. PostgreSQL's `SERIAL` declarations derive
`wolverine_node_record_id_seq` and `wolverine_node_node_number_seq` from the singular
tables. EF mapping and generated transactional inbox/outbox persistence consume
the same constants. All three projects must be rebuilt because C# constants may
be inlined into consumers.

## Dependencies and reproduction

`WolverineFx` and `WolverineFx.RuntimeCompilation` remain pinned to 6.39.1.
Weasel Core, PostgreSQL, and EF integrations remain pinned to the upstream 9.32.0.
EF Core packages use Goblin's compatible 10.0.12 pin (upstream net10 used 10.0.4).
EF design-time assets are private to the integration project and do not flow
into the application publish output. The RDBMS project's upstream core project reference is replaced with its published
`WolverineFx` package. The three affected integration packages are never referenced
through NuGet. Project references rebuild them for ordinary solution builds and
publishing. Docker copies this source before publishing. There are no patched
NuGet caches, unpublished packages, precompiled integration binaries, runtime
reflection patches, or SQL interception.

To reproduce and review the import, put all downloaded material under the task
artifact directory:

```bash
mkdir -p .artifacts/postgres-singular/upstream
curl --fail --location \
  https://codeload.github.com/JasperFx/wolverine/tar.gz/5a7aa99b5c4f2b29b56300cc5f69919d54a370e7 \
  -o .artifacts/postgres-singular/upstream/source.tar.gz
tar -xzf .artifacts/postgres-singular/upstream/source.tar.gz \
  -C .artifacts/postgres-singular/upstream
```

Compare the three directories listed above against this maintained source,
excluding build output. Replace only the complete SQL identifiers listed here;
keep ordinary plural C# collections. Retain the root upstream LICENSE. The local
project files replace versionless package references with explicit pins, retain
integration-to-RDBMS project references, remove the unused net9-only dependency
block, give the nonpackable projects distinct local package identities, and use
the local build props instead of upstream central build inputs.
This is an audit procedure, not an automatic overwrite of maintained source.

Build from a clean package directory to demonstrate independence from global
NuGet caches:

```bash
dotnet publish backend/src/Goblin.Web -c Release \
  -p:RestorePackagesPath="$PWD/.artifacts/postgres-singular/nuget" \
  -o .artifacts/postgres-singular/publish/web
dotnet publish backend/tools/Goblin.Database -c Release \
  -p:RestorePackagesPath="$PWD/.artifacts/postgres-singular/nuget" \
  -o .artifacts/postgres-singular/publish/database
```

Review the restored graph and published `.deps.json`: `Wolverine.RDBMS`,
`Wolverine.Postgresql`, and `Wolverine.EntityFrameworkCore` must be project libraries,
with no `WolverineFx.RDBMS`, `WolverineFx.Postgresql`, or
`WolverineFx.EntityFrameworkCore` package libraries. Upstream runtime and Weasel
packages remain unchanged. The application tests assert the integration boundary
and exercise EF transactions and actual durable handler consumption.

Keep this implementation aligned with
[`0001_initial.sql`](../../database/migrations/0001_initial.sql). Startup retains
`AutoCreate.None`; the administrator applies SQL with the independent migration
runner. Recreate only deliberately disposable development databases after changes
to the unreleased baseline. Never bypass migration checksums or use runtime DDL.

Verification uses the affected database, persistence, and application tests plus
`make check`, a real PostgreSQL suite, twice-reproducible EF scaffolding, clean
Release publishes, and the application container build. Database-gated tests need
explicit certificate-authenticated configuration. See the
[database guide](../../../docs/database.md) for profiles and browser checks.
