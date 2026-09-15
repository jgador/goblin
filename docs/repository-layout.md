# Repository layout

Goblin has separate backend and frontend source trees and ships as one ASP.NET
Core application. Run the commands below from the repository root.

```text
backend/
  src/Goblin.Web/          ASP.NET Core host and Codex integration
    Auth/                 Account authentication and API-key verification
    Access/               Workspace access, passwords, and browser sessions
    Http/                 HTTP contracts and public errors
    Codex/                Child process, JSONL transport, and prompt execution
  src/Goblin.Protocol/     Generated Codex protocol models and serialization
  tests/                  .NET tests and the test-only application host
  schemas/codex/           Checked-in inputs to protocol generation
  scripts/                Protocol generator
  Goblin.slnx              .NET solution
  Directory.Build.props   Shared .NET build settings
frontend/
  src/connection/          Connection screen: HTML, TypeScript, and CSS
  src/work/                Work preview: HTML, TypeScript, and CSS
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

## Build and test

| Command | Purpose |
| --- | --- |
| `npm ci` | Install the locked workspace dependencies |
| `npm run build` | Build frontend assets, test tooling, and the .NET solution |
| `npm start` | Build everything and start the application |
| `npm run build:assets` | Build only the frontend |
| `npm run build --workspace frontend` | Run the frontend workspace build directly |
| `npm run build:backend` | Build the .NET solution using any existing frontend output |
| `npm run typecheck` | Check both TypeScript configurations and build .NET |
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
`backend/schemas/codex/`; Goblin HTTP contracts belong to the backend's `Http/`
folder and `frontend/src/api/contracts.ts`. They describe different APIs.
