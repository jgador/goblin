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
npm ci
npm start
```

Open http://localhost:8787 and enter the workspace access code stored in
`.goblin-auth/owner-token`. See the [preview guide](docs/authentication-preview.md)
for sign-in, persistence checks, and deployment to your VM.

The backend, browser UI, tests, and support scripts are written in TypeScript.
`npm start` builds the app into `dist/` before launching it. Use
`npm run typecheck` to check types without producing build files, or `npm test`
to build and run the unit tests.

The official artwork, exports, and packaged fonts are organized in
[`assets/branding/`](assets/branding/README.md). The UI uses
`public/assets/branding/icon.svg`, copied from the light-background, icon-only
SVG, for both the favicon and header. Keep this asset as a vector and size the
header logo with CSS to preserve its proportions and sharpness on high-density
screens.
