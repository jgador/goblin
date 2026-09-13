# Deploy Goblin to Azure

Deploy an Ubuntu VM, a static public IP with an Azure-managed hostname,
single-node K3s, the Agent Sandbox controller, and the Goblin application.
After provisioning, open **`goblinUrl`** in the deployment outputs and enter the
Goblin password you chose during setup.

## Deploy

Start with the **Deploy to Azure** button in the [root README](../../README.md).

1. Select your subscription, a dedicated resource group (e.g. `rg-goblin-prod`),
   and region. Keep the remaining defaults unless needed.
2. Enter and confirm a **Goblin password**. There is no minimum length or
   character-mix requirement. Save it in your password manager; use it to open
   Goblin.
3. Under VM credentials, generate a new SSH key pair or select an existing public key.
4. Select **Review + create → Create**. For a new key, select **Download private
   key and create resource** and save the file; it can only be downloaded once.

Provisioning builds the application from this repository, imports its image into
K3s, and routes the assigned Azure hostname to Goblin through Traefik. A custom
**Public hostname prefix** is picked up automatically. The build can take several
minutes; deployment waits until the application responds through that route.

For example, open `http://goblin-prod.southeastasia.cloudapp.azure.com`.
There is no port number or workspace-token lookup. HTTPS and certificates remain
a separate setup step; HTTP traffic, including passwords and sessions, is unencrypted.

## Verify

After deployment succeeds, open **Outputs** for resource names, public IP,
hostname, and administration commands. In the VM, open **Run command →
RunShellScript** and run:

```bash
cat /var/lib/goblin/bootstrap-status
```

Expect `ready`. The URL is also saved in `/var/lib/goblin/public-url`.
For failures, check `/var/log/goblin-bootstrap.log`. See the
[reference](reference.md) for troubleshooting, naming, SSH access, and maintenance.

For an existing VM provisioned by the infrastructure-only template, redeploy the
updated template with the same resource names and Goblin password. It installs
the application and route while retaining the application's PVC, if present.

## Cost and removal

The default VM is `Standard_D4s_v5` (4 vCPUs, 16 GiB RAM) with a 128 GiB Standard
SSD OS disk. Azure charges apply, including for resources left after a failed deployment.
To remove the deployment, delete its dedicated resource group. **This deletes
all resources and data in that group.**
