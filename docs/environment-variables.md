# Environment variables

Each language defines environment variable names once:

| Language | Definition catalog | Reference |
| --- | --- | --- |
| C# | `backend/src/Goblin.Contracts/Configuration/EnvironmentVariables.cs` | `EnvironmentVariables.GoblinPort` |
| Rust | `tools/goblinctl/src/environment.rs` | `goblinctl::environment::GOBLIN_PASSWORD_HASH_FILE` |
| TypeScript | `config/environment.mts` | `environmentVariables.GOBLIN_LOCAL_PASSWORD.name` |

Each entry documents purpose, format, fallback, when it is required, and whether it
is sensitive. goblinctl and xtask share the Rust catalog. The TypeScript catalog
serves Node scripts and test tooling; the frontend reads no environment variables,
and secrets must never be bundled into browser code.

Catalogs only name variables. They do not read the environment, apply defaults,
validate values, or forward anything; keep that at the existing configuration
boundaries. The Work core receives typed inputs and never references these catalogs.

## Reading and forwarding

```csharp
using Env = Goblin.Contracts.Configuration.EnvironmentVariables;

string portValue = Environment.GetEnvironmentVariable(Env.GoblinPort) ?? "8787";
// Keep the existing port parsing and range check here.
string? executionNamespace = builder.Configuration[Env.GoblinExecutionNamespace];
```

```rust
use goblinctl::environment;

let path = std::env::var_os(environment::GOBLIN_PASSWORD_HASH_FILE);
command.env(environment::GOBLIN_PASSWORD_HASH_FILE, verifier_path);
command.env_remove(environment::GOBLIN_LOCAL_PASSWORD);
```

```typescript
// Built .ts consumers import .mjs; native Node .mts consumers import .mts.
import { environmentVariables as Env } from "../config/environment.mts";

const password = process.env[Env.GOBLIN_LOCAL_PASSWORD.name];
const childEnvironment = {
    ...process.env,
    [Env.GOBLIN_PASSWORD_HASH_FILE.name]: verifierPath,
};
```

The web entry point reads its process variables directly and keeps its existing
defaults and case-insensitive boolean handling. Hosting configuration uses the
standard .NET providers and precedence. `ConnectionStrings__Goblin` and
`ConnectionStrings__GoblinAdmin` map to `ConnectionStrings:Goblin` and
`ConnectionStrings:GoblinAdmin`; `GetConnectionString("Goblin")` uses the
configuration name, not the environment name. PostgreSQL development profiles pass
connections directly to child processes; `HOME` and optional `XDG_CONFIG_HOME`
locate the profile store, and `db run --usage tests` also supplies the test
connection variables. See [PostgreSQL](database.md#daily-development).

Rust keeps its current `var` versus `var_os` use, parsing, and removal of
credentials from subprocesses. `env!("CARGO_MANIFEST_DIR")`, `env!("CARGO_PKG_VERSION")`,
and `option_env!("GOBLINCTL_BUILD_VERSION")` need string literals and are
compile-time inputs. `GOBLINCTL_BUILD_VERSION` is set by release preparation for the
goblinctl build; ordinary builds fall back to the Cargo package version.

The TypeScript catalog also names CI, release, and agentic maintenance variables
such as `CANDIDATE_SHA256`, `PREPARED_ATTEMPT`, and GitHub's event and output
variables; these are not production configuration. See
[CI selection](github-actions.md#ci-selection-and-required-result) and
[agentic maintenance](agentic-maintenance.md). `OPENAI_API_KEY` is used only by
gh-aw's isolated API proxy; agent shells and runtime subprocesses never receive it.

Codex and GitHub subprocesses keep their explicit allowlists and private homes.
Adding a definition does not allow forwarding it. Catalog entries for test-only
failure controls and credential-inheritance fixtures are not supported configuration.

## Shared variables and deployments

| Variable | Shared contract |
| --- | --- |
| `GOBLIN_PASSWORD_HASH_FILE` | Private verifier file path. Web startup requires it. Tooling validates an explicit value without replacing it; otherwise dev and local installation use `.goblin-secrets/owner-password`. |
| `GOBLIN_LOCAL_PASSWORD` | Unattended password input for creation or replacement. An existing verifier wins unless replacement is explicit. Native tooling removes it before starting child processes. |
| `GOBLIN_PUBLIC_ORIGIN` | Browser-facing origin. Web defaults to `http://localhost:<port>`; public installation defaults to `https://<hostname>`; local installation uses loopback HTTP. Remote HTTP requires `GOBLIN_ALLOW_INSECURE_HTTP=true`. |
| `GOBLIN_CHATGPT_ENABLED` | Optional `true`/`false` flag for embedded OpenIddict and MCP. Unset or false disables both; the Azure HTTPS installer sets true. Requires durable Work and an HTTPS public origin. Never forwarded to execution runtimes. See [ChatGPT setup](chatgpt-integration.md). |
| `ConnectionStrings__Goblin` | Application PostgreSQL connection (`ConnectionStrings:Goblin`). Rust profiles set it, and the HTTP test harness forwards its opt-in test connection to it. |
| `ConnectionStrings__GoblinAdmin` | Schema administrator connection (`ConnectionStrings:GoblinAdmin`); the Rust migration Job uses the same name. |
| `GOBLIN_TEST_POSTGRES_APP` / `GOBLIN_TEST_POSTGRES_ADMIN` | Opt-in real-database test connection strings. Sensitive; never application defaults. |
| `CODEX_HOME` | Private Codex storage supplied by Goblin, replacing any inherited value. The TypeScript fake runtime reads the same name. |

Docker, Kubernetes, Bicep, GitHub Actions, and Bash files use literal names because
they cannot import the catalogs. `tests/deployment/environment.test.ts` checks that
the `GOBLIN_*` names in the Kubernetes manifests, Dockerfile, and installer scripts
exist in the catalogs. Shell-only installer inputs are also in the Rust
catalog: `GOBLIN_IMAGE_PULL_WORKERS`, `GOBLIN_INSTALL_PROGRESS`, and
`GOBLIN_INSTALL_STATE`. Embedded installer scripts read them, so changing their
names or meaning requires a new installer build.

A catalog fallback is the consumer's default. Deployments may set explicit values,
for example the Docker image sets `GOBLIN_HOST=0.0.0.0` instead of the web host's
loopback default. See [Kubernetes names](kubernetes-names.md) for component and
storage defaults.

The Node-only Chisel deployment adapter defines `AK3S_KUBECONFIG`, `CHISEL_AUTH`,
and `CHISEL_SERVER_KEY`. All three are sensitive GitHub environment secrets; the
adapter writes them to private temporary files and never forwards them to kubectl
or Goblin runtimes. Chisel's local client reads its own `AUTH` variable as
described in [the deployment guide](../deploy/chisel/README.md).

## Adding a variable

1. Add an entry to each language catalog that uses the variable, with its exact
   name, purpose, format, fallback and precedence, when it is required, and whether
   it is sensitive. Note differences between consumers of a shared variable.
2. Use the definition for every read, set, removal, allowlist, and generated
   environment entry. Generated test executables cannot import repository modules,
   so insert the catalog name into them.
3. Parse and validate where configuration enters an adapter, and pass typed values
   to application and core APIs. Do not add environment access to Work rules.
4. Update affected deployment templates and operator documentation together.
   Check explicit subprocess allowlists before forwarding a new value.
5. Run `make test`, `make rust-format-check`, and `make rust-lint`. Review skipped
   PostgreSQL tests, and set the opt-in connections when persistence behavior
   needs checking.
