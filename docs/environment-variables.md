# Environment variables

Define each variable name once in each language that uses it:

| Consumer | Authoritative catalog |
| --- | --- |
| C# adapters | [EnvironmentVariables.cs](../backend/src/Goblin.Contracts/Configuration/EnvironmentVariables.cs) |
| goblinctl and xtask | [environment.rs](../tools/goblinctl/src/environment.rs) |
| Node scripts and tests | [environment.mts](../config/environment.mts) |

Entries describe purpose, format, fallback, precedence, requirements, and sensitivity.
The browser reads no environment variables; never bundle secrets into browser code.

## Reading and forwarding

Catalogs name variables; they do not read, validate, apply defaults, or grant forwarding
permission. Parse where configuration enters an adapter and pass typed values to
application and core APIs. Work rules never access the environment.

Use catalog definitions for reads, sets, removals, allowlists, and generated environment
entries. Preserve `var` versus `var_os` behavior in Rust. Compile-time Rust macros
require literal names.

[ApplicationOptionsConfiguration](../backend/src/Goblin.Web/Hosting/ApplicationOptionsConfiguration.cs)
owns executable defaults and parsing;
[WebRuntimeConfiguration](../backend/src/Goblin.Web/Hosting/WebRuntimeConfiguration.cs)
uses standard .NET configuration providers. Double underscores in environment names map
to configuration colons: `ConnectionStrings__Goblin` supplies
`ConnectionStrings:Goblin`. PostgreSQL profiles pass connections to child processes
without editing appsettings; see [database development](database.md#daily-development).

Codex and GitHub subprocesses use explicit allowlists and private homes. Adding a
catalog entry does not authorize forwarding. Credentials, deployment controls, and test
failure switches must stay within their intended consumers. Test-only variables are not
supported application configuration. gh-aw's API proxy holds its OpenAI key; agent
shells and runtime subprocesses never receive it.

## Deployment contracts

Docker, Kubernetes, Bicep, workflows, and shell assets use literal names because they
cannot import catalogs. The
[deployment environment tests](../tests/deployment/environment.test.ts) check registered
Goblin names. Catalog fallbacks describe consumers; manifests may override them. Read
deployed values from the manifests, not a copied default table.

Installation shell inputs are embedded in goblinctl. A changed name or meaning requires
a new installer build. Explicit public-origin and ChatGPT-enable values apply during
[k3s deployment](../deploy/k3s/README.md#deploy-application-edits); unset values retain
the installed settings.

## Adding a variable

1. Add its definition and sensitivity to every consuming language catalog.
2. Use the definition at each environment operation, including generated fixtures.
3. Parse and validate at the boundary; review subprocess allowlists before forwarding.
4. Update deployment assets and operator instructions if supported configuration changes.
5. Run `make test`, `make rust-format-check`, and `make rust-lint`, with additional
   affected checks from [test selection](repository-layout.md#choosing-verification).
   Report PostgreSQL skips and run opt-in integration checks when persistence changes.
