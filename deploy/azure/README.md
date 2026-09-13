# Deploy Goblin infrastructure to Azure

Deploy an Ubuntu VM, a static public IP with an Azure-managed hostname,
single-node K3s, and the Agent Sandbox controller. **The Goblin application is
not installed yet.**

## Deploy

[![Deploy to Azure](https://aka.ms/deploytoazurebutton)](https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2Fjgador%2Fgoblin%2Fmaster%2Fdeploy%2Fazure%2Fazuredeploy.portal.json/createUIDefinitionUri/https%3A%2F%2Fraw.githubusercontent.com%2Fjgador%2Fgoblin%2Fmaster%2Fdeploy%2Fazure%2FcreateUiDefinition.json)

1. Select your subscription, a dedicated resource group (e.g. `rg-goblin-prod`),
   and region. Keep the remaining defaults unless needed.
2. Enter and confirm a **Goblin password**. There is no minimum length or
   character-mix requirement. Save it in your password manager; use it to open
   Goblin before connecting your ChatGPT account.
3. Under VM credentials, generate a new SSH key pair or select an existing public key.
4. Select **Review + create → Create**. For a new key, select **Download private
   key and create resource** and save the file; it can only be downloaded once.

Provisioning saves a password hash for the [authentication preview](../../docs/authentication-preview.md#run-in-the-provisioned-agent-sandbox-cluster).
After installing that preview, use the password you chose here. You do not need
to retrieve a workspace access token from the VM. Application installation and
public HTTPS setup remain separate steps.

## Verify

After deployment succeeds, open **Outputs** for resource names, public IP,
hostname, and administration commands. In the VM, open **Run command →
RunShellScript** and run:

```bash
cat /var/lib/goblin/bootstrap-status
```

Expect `ready`. For failures, check `/var/log/goblin-bootstrap.log`. See the
[reference](reference.md) for troubleshooting, naming, SSH access, and maintenance.

## Cost and removal

The default VM is `Standard_D4s_v5` (4 vCPUs, 16 GiB RAM) with a 128 GiB Standard
SSD OS disk. Azure charges apply, including for resources left after a failed deployment.
To remove the deployment, delete its dedicated resource group. **This deletes
all resources and data in that group.**
