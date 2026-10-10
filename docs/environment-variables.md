# Environment variables

Environment names are defined once per language at these locations:

| Language | Definition catalog | Reference |
| --- | --- | --- |
| C# | `backend/src/Goblin.Contracts/Configuration/EnvironmentVariables.cs` | `EnvironmentVariables.GoblinPort` |
| Rust | `tools/goblinctl/src/environment.rs` | `goblinctl::environment::GOBLIN_PASSWORD_HASH_FILE` |
| TypeScript | `config/environment.mts` | `environmentVariables.GOBLIN_LOCAL_PASSWORD.name` |

Each catalog documents purpose, value format, fallback, required circumstances,
and sensitivity. C# uses documented constants in the existing adapter contracts
assembly; Rust shares its documented constants between goblinctl and xtask;
TypeScript exports an `as const` metadata object with derived key/name types.
The TypeScript catalog serves Node scripts and test tooling. The frontend does
not currently read environment variables; never bundle secrets into browser code.

These catalogs name variables; they do not read the environment, apply defaults,
validate values, or automatically forward anything. Keep those operations at the
existing configuration boundaries. The Work core continues receiving typed inputs
and has no reference to environment definitions.

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

The C# web entry point reads its process variables directly, preserving its
existing null/default and case-insensitive boolean handling. Hosting configuration
uses the existing .NET providers and their precedence. `ConnectionStrings__Goblin`
and `ConnectionStrings__GoblinAdmin` map to `ConnectionStrings:Goblin` and
`ConnectionStrings:GoblinAdmin`; calls to `GetConnectionString("Goblin")` and
`GetConnectionString("GoblinAdmin")` use configuration section names, not environment
names. Existing runtime JSON configuration remains supported. PostgreSQL development
profiles supply connections directly to child processes; they do not generate
tooling appsettings files. `HOME` and optional `XDG_CONFIG_HOME` locate the user
store. `db run --usage tests` explicitly supplies the test connection variables.

Rust keeps `var` versus `var_os`, its current parsing, and subprocess credential
removal. `env!("CARGO_MANIFEST_DIR")` and `env!("CARGO_PKG_VERSION")` are compiler
inputs whose macros require string literals, not runtime configuration. Their
definitions remain Cargo-owned.

`GOBLINCTL_BUILD_VERSION` is a compile-time release input defined in the Rust
catalog. Preparation validates and supplies it to the isolated goblinctl build;
the executable reports that version consistently. An ordinary build falls back
to the Cargo package version. Changing a running process's environment cannot
change its version. The literal in `option_env!` is required by the compiler.

Release publication receives `CANDIDATE_SHA256` from the sealing job and checks it
against the downloaded manifest. `PREPARED_ATTEMPT` retains the original preparation
attempt when failed jobs are rerun; it is not replaced by the retry attempt.

Codex and GitHub subprocesses keep their explicit allowlists and private homes.
Adding a definition grants no forwarding permission. The catalogs also name
test-only failure controls and negative credential-inheritance fixtures; those
entries are not supported production configuration.

## Shared variables and deployments

| Variable | Shared contract |
| --- | --- |
| `GOBLIN_PASSWORD_HASH_FILE` | Private verifier file path. Web startup requires it. Tooling validates an explicit override without silently replacing it; otherwise dev/local installation uses `.goblin-secrets/owner-password`. |
| `GOBLIN_LOCAL_PASSWORD` | Unattended password input for creation/replacement. An existing verifier wins unless replacement is explicit. Native tooling removes plaintext before launching children. |
| `GOBLIN_PUBLIC_ORIGIN` | Browser-facing origin. Web defaults to `http://localhost:<port>`; installation defaults to `http://<hostname>`. Remote HTTP requires `GOBLIN_ALLOW_INSECURE_HTTP=true`. |
| `ConnectionStrings__Goblin` | Application PostgreSQL connection mapped by .NET to `ConnectionStrings:Goblin`. Rust provisioning writes the same environment name and the HTTP test harness forwards its opt-in test connection to it. |
| `ConnectionStrings__GoblinAdmin` | Schema administrator connection mapped to `ConnectionStrings:GoblinAdmin`; Rust migration Jobs use the same name. |
| `GOBLIN_TEST_POSTGRES_APP` / `GOBLIN_TEST_POSTGRES_ADMIN` | Opt-in real-database test connection strings. These are sensitive and never application defaults. |
| `CODEX_HOME` | Private Codex storage supplied by Goblin, replacing an inherited value. The TypeScript fake runtime observes the same name. |

Declarative Docker, Kubernetes, Bicep, GitHub Actions, and Bash files retain their
literal platform syntax; they cannot import C#, Rust, or TypeScript constants.
Deployment contract tests check template names against the catalogs. Shell-only
operator inputs are documented in the Rust catalog too:
`GOBLIN_IMAGE_PULL_WORKERS`, `GOBLIN_INSTALL_PROGRESS`, and `GOBLIN_INSTALL_STATE`.
Installer inputs are consumed by embedded scripts,
so changing their names or semantics requires checking the shipping installer.

The application template `deploy/auth/sandbox.yaml`, Azure overlays, Docker image
defaults, and PostgreSQL migration environment retain their existing names and
values. A catalog's fallback describes a consumer default; a deployment may supply
an explicit value, such as `GOBLIN_HOST=0.0.0.0` instead of the web host's loopback
default. No installer release or dependency pin is changed by this refactor.

## Adding a variable

1. Add a named entry to each language catalog that consumes the variable. Document
   its exact name, purpose, format, fallback/precedence, required circumstances,
   and sensitivity. Describe consumer-specific differences for shared variables.
2. Reference the definition for every read, set, removal, allowlist, and generated
   environment entry. Interpolate catalog names into generated test executables;
   those temporary files cannot import repository modules.
3. Parse and validate where configuration enters an adapter; pass typed values
   into application and core APIs. Do not add environment access to Work rules.
4. Update affected deployment templates and operator documentation together.
   Recheck explicit subprocess allowlists before forwarding any new value.
5. Run `make test`, `make format-rust-check`, and Clippy. Review real PostgreSQL skips;
   configure the opt-in connections when persistence behavior needs verification.

`tests/deployment/environment.test.ts` checks catalog metadata, matching shared
names, and deployment names. Existing HTTP, credentials, process, and deployment
tests verify defaults, configuration precedence, and environment isolation.
