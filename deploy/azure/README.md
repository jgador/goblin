# Deploy Goblin to Azure

Deploy an Ubuntu VM, a static public IP with an Azure-managed hostname,
and a small installation status page. A background service installs single-node
K3s, cert-manager, Agent Sandbox, and Goblin. Open **`goblinUrl`** in the deployment
outputs to follow progress; the same page opens Goblin when ready.

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

For SSH from Windows, see [Windows SSH private-key permissions](reference.md#windows-ssh-private-key-permissions)
if the downloaded `.pem` file cannot be read or OpenSSH rejects its permissions.
The guide includes PowerShell commands and **Properties → Security** steps.

Azure provisioning finishes once the status page responds and the background
installer has launched. Kubernetes and the application are installed afterward;
this can take several minutes. The page shows each step as waiting, running,
complete, or failed. Closing the browser does not interrupt installation.

If opened through another address during setup, the page redirects to the
configured Goblin hostname so its progress checks and automatic handoff keep
working when Kubernetes takes over.

The installer builds the application from this repository, imports it into K3s,
and checks its internal route before transferring the public URL to Traefik.
After a successful handoff, the setup UI and installer are disabled. A brief
reconnection during the switch is normal. The small setup bundle, status, and
logs remain on the VM for repair.

Cert-manager is installed as an independent cluster add-on. It does not create
certificates, install PostgreSQL, or change database connection strings,
passwords, or authentication. After installation, run
`bash deploy/postgres/setup.sh` from the checkout to enable
[PostgreSQL certificate authentication](../../docs/database.md).

For example, open `http://goblin-prod.southeastasia.cloudapp.azure.com`.
Enter your Goblin password to open the workspace. HTTPS and certificates remain
a separate setup step; HTTP traffic, including passwords and sessions, is unencrypted.

## Verify

After deployment succeeds, open **Outputs** for resource names, public IP,
hostname, and administration commands. In the VM, open **Run command →
RunShellScript** and run:

```bash
cat /var/lib/goblin/bootstrap-status
```

Expect `setup-ready`: this means the status UI started and the installer launched,
not that Goblin has finished installing. Inspect background progress with:

```bash
cat /var/lib/goblin/install/status.json
```

Its overall status becomes `ready` when Goblin is available. The URL is saved in
`/var/lib/goblin/public-url`. Bootstrap failures appear in Azure and in
`/var/log/goblin-bootstrap.log`; background failures appear on the status page and
in `/var/log/goblin-installer.log`. To retry after correcting a failure:

```bash
sudo systemctl start goblin-installer.service
```

See the
[reference](reference.md) for troubleshooting, naming, SSH access, and maintenance.

For an existing VM provisioned by the infrastructure-only template, redeploy the
updated template with the same resource names and Goblin password. The background installer updates
the application and route while retaining the application's PVC, if present.

## Test locally

The [local WSL/Ubuntu installer](../local/README.md) installs the same bundle from your current
checkout and keeps the live setup page and resulting Goblin installation available
for manual testing, including from a Windows host through WSL.

## Cost and removal

The default VM is `Standard_D4s_v5` (4 vCPUs, 16 GiB RAM) with a 128 GiB Standard
SSD OS disk. Azure charges apply, including for resources left after a failed deployment.
To remove the deployment, delete its dedicated resource group. **This deletes
all resources and data in that group.**
