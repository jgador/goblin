# Repository layout

Goblin has separate backend and frontend source trees and ships as one ASP.NET
Core application. Run the commands below from the repository root.

```text
backend/
  src/Goblin.Core/         Work lifecycle rules and Goblin-owned execution types
  src/Goblin.Contracts/   Public account and runtime capability contracts
  src/Goblin.Application/ Durable commands, conversations, dispatch, and recovery
  src/Goblin.Execution/   Text workers and isolated repository sandboxes
  src/Goblin.Integrations.Codex/   Codex account, transport, and Work adapter
  src/Goblin.Integrations.GitHub/  GitHub device sign-in and private credentials
  src/Goblin.Web/          ASP.NET Core composition and HTTP adapters
    Access/               Workspace access, passwords, and browser sessions
    Http/                 HTTP contracts and public errors
  src/Goblin.Protocol/     Generated Codex protocol models and serialization
  src/Goblin.Persistence/  Database-first EF Core context, entities, and registration
  database/migrations/    Ordered SQL schema changes
  tools/Goblin.Database/  SQL migration runner and EF tooling host
  tests/                  .NET tests and the test-only application host
  schemas/codex/           Checked-in inputs to protocol generation
  scripts/                Protocol generator
  Goblin.slnx              .NET solution
  Directory.Build.props   Shared .NET build settings
frontend/
  src/connection/          Connection screen: HTML, TypeScript, and CSS
  src/work/                Persisted Work UI: HTML, TypeScript, and CSS
  src/api/contracts.ts     Goblin HTTP types used by the UI and application tests
  public/assets/           Static assets copied into the browser build
  scripts/                 Browser build helpers
  package.json             Frontend npm workspace and TypeScript dependency
  tsconfig.json            Browser compiler settings; no Node.js types
tests/
  integration/             HTTP tests against the C# test host
  e2e/                     Playwright browser journeys
  deployment/              Azure bootstrap and installation tests
  support/                 Backend launcher and browser test server
  fixtures/                Fake Codex process used by .NET and application tests
deploy/                    Azure infrastructure and Kubernetes manifests
assets/branding/           Original artwork, exports, and packaged fonts
docs/                      Setup, architecture, and development documentation
scripts/                   Repository checks and test-tool build helpers
```

The root `package.json` coordinates builds and application tests and pins the
Codex runtime distribution. npm workspaces use one root `package-lock.json`;
`npm ci` installs both repository and frontend dependencies. The root
`tsconfig.json` compiles Node.js test tooling, while `frontend/tsconfig.json`
compiles browser source independently. `global.json` stays at the root so the
pinned .NET SDK applies to commands run from either the root or `backend/`.

Node.js 24 or newer runs the `.mts` build helpers, secret-scanning scripts, and
fake Codex fixture directly. `.mts` is TypeScript with explicit ES module
semantics, including when a test copies a script outside the repository. These
scripts do not depend on compiled output or an installed TypeScript runner.
`tsconfig.scripts.json` checks them without emitting files and permits only
erasable TypeScript syntax. Native Node execution strips types without checking
them; `npm run typecheck:scripts` performs that check as part of the full build
and `npm run typecheck`.

The installer page in `deploy/azure/setup/app.js` remains JavaScript because the
Python-only setup bundler packages it directly for the browser. Moving its source
to TypeScript would require generating and checking the packaged JavaScript.

## Build and test

| Command | Purpose |
| --- | --- |
| `npm ci` | Install the locked workspace dependencies |
| `npm run build` | Build frontend assets, test tooling, and the .NET solution |
| `npm start` | Build, provision the local password on first run, and start the application |
| `npm run setup:password` | Choose and confirm a password; save only its verifier in `.goblin-secrets/` |
| `npm run build:assets` | Build only the frontend |
| `npm run build --workspace frontend` | Run the frontend workspace build directly |
| `npm run build:backend` | Build the .NET solution using any existing frontend output |
| `npm run typecheck` | Check browser source, test tooling, and direct Node scripts; build .NET |
| `npm run typecheck:scripts` | Check directly executed TypeScript scripts without building |
| `npm test` | Build, check protocol generation, and run .NET, HTTP, and deployment tests |
| `npm run test:browser` | Build and run Playwright journeys |
| `npm run test:codex` | Build and check the pinned Codex binary with isolated test credentials |
| `npm run protocol:generate` | Regenerate C# models from the checked-in schemas |
| `npm run protocol:check` | Verify that generated models match the schemas |

After building frontend assets, publish with:

```sh
dotnet publish backend/src/Goblin.Web -c Release -o .artifacts/publish
```

## Build output and browser routes

The frontend build writes JavaScript, HTML, CSS, and static assets to
`frontend/dist/`. The backend project copies this directory into its output and
published `wwwroot/`. Browser URLs are mapped explicitly in
`backend/src/Goblin.Web/GoblinApplication.cs`: `/`, `/app.js`, and `/styles.css`
serve the `connection/` files; `/work` and `/work/*` serve the `work/` files.
Runtime assets keep their `/assets/*` URLs.

The root `dist/` holds compiled test tooling. It is separate from the frontend
build and is not included in the application image. Both output directories,
.NET `bin/` and `obj/`, `.artifacts/`, and local runtime data remain ignored by Git.
The root Dockerfile builds frontend assets before publishing the backend, using
the repository root as its build context.

Keep original artwork in `assets/branding/` and copy only the assets used by the
browser into `frontend/public/assets/`. Codex schemas belong under
`backend/schemas/codex/`; Goblin account/runtime contracts belong to `Goblin.Contracts`; Work command/view
contracts belong to `Goblin.Application/Work`. Browser types describe these public
Goblin APIs, including `frontend/src/api/contracts.ts` for connection setup. They describe different APIs.

For PostgreSQL setup, SQL changes, and regenerating the EF Core classes after
adding tables, see the [database guide](database.md).

The [Work lifecycle](work-lifecycle.md) is used by the live application. Core and
public contracts are independent of runtime protocols and HTTP. Orchestration
depends on the shared execution host contract; composition selects the concrete
Codex/text/sandbox adapters. [Execution hosting](execution-hosting.md) documents
configuration, reservations, recovery, and validation limits.
