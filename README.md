# Goblin

Your self-hosted AI coworker.

Goblin aims to turn everyday team conversations into completed, reviewable work.

Codex is the current primary coding agent integration. Goblin's architecture is
being prepared for other agents, including Claude and GitHub Copilot, with
Goblin owning durable Work, history, and decisions across executions. Those
additional integrations are future work. See the
[architecture and rewrite plan](docs/architecture-refactoring-plan.md) for the
four agreed rewrite areas and the first durable Work milestone.

The repository separates application source into [`backend/`](backend/) (C# and
Codex integration) and [`frontend/`](frontend/) (browser UI). Application-wide
tests live in [`tests/`](tests/), deployment files in [`deploy/`](deploy/), and
original artwork in [`assets/`](assets/). See the
[repository layout](docs/repository-layout.md) for ownership and build commands.

Deploy Goblin to your own Linux VM in Azure:

[![Deploy to Azure](https://aka.ms/deploytoazurebutton)](https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2Fjgador%2Fgoblin%2Fmaster%2Fdeploy%2Fazure%2Fazuredeploy.portal.json/createUIDefinitionUri/https%3A%2F%2Fraw.githubusercontent.com%2Fjgador%2Fgoblin%2Fmaster%2Fdeploy%2Fazure%2FcreateUiDefinition.json)

Deployment support for Google Cloud Platform, AWS, and other providers will follow later.

The [Azure deployment guide](deploy/azure/README.md) includes an ARM template with
customer-specific names, optional resource-name overrides, a static public IP,
an Azure DNS hostname, single-node Kubernetes, cert-manager, and the Agent Sandbox controller.
The guided installer uses Azure's native custom-template flow, with configuration
and the intended SSH-key download inside Azure Portal. The new key-generation
flow is awaiting live portal verification.

If a downloaded SSH key fails on Windows with `Load key ...: Permission denied`
or a warning that permissions are too open, see
[Windows SSH private-key permissions](deploy/azure/reference.md#windows-ssh-private-key-permissions).

**Current status:** Azure provisioning starts a lightweight setup page at
`http://<Azure-assigned-hostname>` while installation continues in the background.
Open the deployment's `goblinUrl` output to follow progress. The same URL opens
Goblin when ready; enter the password chosen during setup. The
[authentication preview](docs/authentication-preview.md) lets you test
ChatGPT and OpenAI API-key login, then automatically verifies model access and
shows the connection status, locally or in an Agent Sandbox pod. GitHub integration, repository tasks,
custom domains, and automatic HTTPS certificates remain future work.

The background installer builds the application on the VM and configures Traefik using the
public IP resource's actual DNS hostname, including a custom prefix. HTTP is
enabled explicitly for this deployment; traffic is unencrypted until you add HTTPS.

To test the full installation directly in WSL/Ubuntu, use the [local installer](deploy/local/README.md):

```bash
npm run install:local -- start
```

Open **http://localhost:8788** in Windows to follow installation and enter Goblin
at the same address when ready. On first start, choose and confirm a Goblin password
in the terminal. Local runs share the verifier in `.goblin-secrets/owner-password`.

Kubernetes installations also include [Headlamp](docs/cluster-view.md) at
`/headlamp/` on Goblin's address. **Settings → Cluster → Open cluster** opens a
read-only view of workloads, logs, events, and storage using your Goblin login.

To try authentication first:

```bash
# Requires .NET 10 SDK, Node.js 24+, and Python 3.
npm ci
npm start
```

On first start, choose and confirm your password in the terminal. Open
http://localhost:8787, enter that password, and click **Open workspace**.
Both local launch paths use Azure's password hasher and the same application login.
Only the verifier is saved locally; `.goblin-secrets/` is ignored except for its
empty `.gitkeep`. Subsequent starts reuse it. See the [preview guide](docs/authentication-preview.md)
for sign-in, persistence checks, and deployment to your VM.

The backend is C#/.NET 10 with ASP.NET Core Minimal APIs. It spawns the official
Rust `codex app-server`; the browser UI remains TypeScript. Protocol models are
generated from the checked-in schemas using `System.Text.Json`.
`npm start` builds the browser assets and .NET solution, then starts the C# host.
Use `npm run typecheck` for TypeScript checks and a .NET build, or `npm test`
for schema drift checks (Python 3), .NET tests, and HTTP integration tests.
See the [App Server migration notes](docs/app-server-migration.md) for the current
Codex integration, model regeneration, and direct .NET commands.

The [Work screen](docs/work-experience-preview.md) is available at
http://localhost:8787/work or through **Open work** on the connection page.
Create and assign Work, execute with Codex, answer decisions, review outcomes,
and approve completion. History and approvals are stored in PostgreSQL; failures
require attention and explicit recovery.

Durable Work requires a configured, migrated PostgreSQL database. The installer
sets this up before starting Goblin. For direct local development, follow the
[database guide](docs/database.md) first, then run `npm start`. Set
`GOBLIN_WORK_ENABLED=false` to run only the connection screen without PostgreSQL.
[Execution hosting](docs/execution-hosting.md) covers repository sandboxes,
GitHub sign-in, credentials, recovery, and current validation limits.

To check for API keys and other secrets before committing, install the free local
Gitleaks CLI and run `npm run secrets:setup` once per checkout. Run
`npm run secrets:scan` for a manual check of the index and working tree, or
`npm run secrets:history` to check existing commits before a push. See the
[secret-scanning guide](docs/secret-scanning.md) for installation and scan scope.

The repository-local Codex hook formats TypeScript, Python, and C# after each
completed turn. See [formatting setup](docs/formatting.md) for tool installation,
manual commands, and the required hook trust step.

The official artwork, exports, and packaged fonts are organized in
[`assets/branding/`](assets/branding/README.md). The UI uses
`frontend/public/assets/branding/icon.svg`, copied from the light-background, icon-only
SVG, for both the favicon and interface branding. Keep this asset as a vector and size
logos with CSS to preserve their proportions and sharpness on high-density
screens.
