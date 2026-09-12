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

**Current status:** this repository provisions the infrastructure foundation.
The Goblin application, administrator onboarding, custom-domain setup, and
automatic HTTPS certificates are not implemented yet.
