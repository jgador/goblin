# Goblin

Your self-hosted AI coworker.

Goblin aims to turn everyday team conversations into completed, reviewable work.

The first deployment target is a customer-owned Linux VM in Azure. The
[Azure deployment guide](deploy/azure/README.md) includes an ARM template with
customer-specific names, optional resource-name overrides, a static public IP,
an Azure DNS hostname, single-node Kubernetes, and the Agent Sandbox controller.
The guided installer uses Azure's native custom-template flow, with configuration
and the intended SSH-key download inside Azure Portal. The new key-generation
flow is awaiting live portal verification.

**Current status:** the Azure deployment provisions the infrastructure foundation.
A separate [authentication preview](docs/authentication-preview.md) lets you test
ChatGPT and OpenAI API-key login, then send a short prompt using the connected
account, locally or in an Agent Sandbox pod. It is not automatically installed
by the Azure template. GitHub integration, repository tasks,
public administrator onboarding, custom domains, and automatic HTTPS certificates
remain future work.

To try authentication first:

```bash
# Requires .NET 10 SDK and Node.js 22+.
npm ci
npm start
```

Open http://localhost:8787 and enter the workspace access code stored in
`.goblin-auth/owner-token`. See the [preview guide](docs/authentication-preview.md)
for sign-in, persistence checks, and deployment to your VM.

The backend is C#/.NET 10 with ASP.NET Core Minimal APIs. It spawns the official
Rust `codex app-server`; the browser UI remains TypeScript. Protocol models are
generated from the checked-in schemas using `System.Text.Json`.
`npm start` builds the browser assets and .NET solution, then starts the C# host.
Use `npm run typecheck` for TypeScript checks and a .NET build, or `npm test`
for schema drift checks (Python 3), .NET tests, and HTTP integration tests.
See the [App Server migration notes](docs/app-server-migration.md) for architecture,
model regeneration, and direct .NET commands.

To check for API keys and other secrets before committing, install the free local
Gitleaks CLI and run `npm run secrets:setup` once per checkout. Run
`npm run secrets:scan` for a manual check of the index and working tree, or
`npm run secrets:history` to check existing commits before a push. See the
[secret-scanning guide](docs/secret-scanning.md) for installation and scan scope.

The official artwork, exports, and packaged fonts are organized in
[`assets/branding/`](assets/branding/README.md). The UI uses
`public/assets/branding/icon.svg`, copied from the light-background, icon-only
SVG, for both the favicon and header. Keep this asset as a vector and size the
header logo with CSS to preserve its proportions and sharpness on high-density
screens.
