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
ChatGPT and OpenAI API-key login locally or in an Agent Sandbox pod. It is not
automatically installed by the Azure template. GitHub integration, agent tasks,
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
