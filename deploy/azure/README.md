# Deploy Goblin to Azure

Deploy an Ubuntu VM, a static public IP with an Azure-managed hostname,
and a small installation status page. A background service installs single-node
K3s, F5 NGINX Ingress Controller, cert-manager, Agent Sandbox, and Goblin. Open
**`setupUrl`** in the deployment outputs to follow read-only progress. The page opens
the HTTPS **`goblinUrl`** after certificate issuance and application verification.

## Deploy

Azure installation is manual. Repository workflows publish installation assets
without signing in to Azure or managing cloud resources.

Start with the **Deploy to Azure** button in the [root README](../../README.md).

1. Choose **Goblin version** on the [website installation page](https://goblinboard.com/install/), then
   **Deploy to Azure**.
   The recommended release is selected by default. In Azure, select your
   subscription, a dedicated resource group (e.g. `rg-goblin-prod`), and region.
   The form displays the selected Goblin version.
2. Enter and confirm a **Goblin password**. There is no minimum length or
   character-mix requirement. Save it in your password manager; use it to open
   Goblin.
3. Supply the **HTTPS certificate contact email** for Let’s Encrypt. Azure’s
   generated hostname needs no manual DNS record.
4. Under VM credentials, generate a new SSH key pair or select an existing public key.
5. Select **Review + create → Create**. For a new key, select **Download private
   key and create resource** and save the file; it can only be downloaded once.

For SSH from Windows, see [Windows SSH private-key permissions](reference.md#windows-ssh-private-key-permissions)
if the downloaded `.pem` file cannot be read or OpenSSH rejects its permissions.
The guide includes PowerShell commands and **Properties → Security** steps.

Azure first downloads and verifies the pinned native [goblinctl release](../../docs/goblinctl.md).
Provisioning finishes once the status page responds and the background
installer has launched. Kubernetes and the application are installed afterward;
this can take several minutes. The page shows each step as waiting, running,
complete, or failed. Closing the browser does not interrupt installation.

If opened through another address during setup, the page redirects to the
configured Goblin hostname so its progress checks and automatic handoff keep
working when Kubernetes takes over.

The installer builds the application from this repository, imports it into K3s,
and checks its internal NGINX route. NGINX then serves progress and HTTP-01
certificate validation publicly; Goblin opens only after trusted HTTPS is verified.
After a successful handoff, the setup UI and installer are disabled. A brief
reconnection during the switch is normal. The permanent native CLI, status, and
logs remain on the VM for repair.

After cert-manager is ready, the application installer provisions PostgreSQL with
certificate authentication and a loopback host endpoint on port 5432, runs the
versioned schema migrations, and deploys
Goblin plus the isolated execution namespace. A database or migration failure
prevents application handoff. [Database setup](../../docs/database.md#installation-and-certificates) and
[execution hosting](../../docs/execution-hosting.md) cover repair, credentials,
repository execution, and GitHub CLI sign-in through Settings. For development
access through an SSH tunnel running inside WSL and explicit Windows certificate
exports, follow [the PostgreSQL workflow](../../docs/database.md#azure-development).
Installers retain no client exports under `/var/lib/goblin/config`.

Headlamp is installed alongside Goblin at **`<goblinUrl>/headlamp/`**. Open
**Settings → Cluster → Open cluster** to inspect workloads, logs, events, and
storage using the same Goblin password. It uses the assigned VM hostname and
the existing route, with read-only cluster access and no extra public port.
See [the cluster view](../../docs/cluster-view.md) for permissions and checks.

Saved application logs are available at **`<goblinUrl>/logs/`** through
**Settings → Logs**, using the same Goblin login. Fluent Bit collects JSON console
output into VictoriaLogs on a separate PVC. See [logging](../../docs/logging.md)
for scope, retention, resource budgets, and verification.

For example, open `https://goblin-prod.southeastasia.cloudapp.azure.com`.
Enter your Goblin password over HTTPS. The installer persists the certificate and
ACME account in Kubernetes Secrets and cert-manager renews it automatically. Keep
public ports 80 and 443 reachable; port 80 serves validation and redirects to HTTPS
after completion. Initial public setup never requests your password.

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

Its overall status becomes `ready` after trusted HTTPS, Goblin readiness, and the
embedded OAuth/MCP discovery endpoints have been verified. The URL is saved in
`/var/lib/goblin/public-url`. Bootstrap failures appear in Azure and in
`/var/log/goblin-bootstrap.log`; background failures appear on the status page and
in `/var/log/goblin-installer.log`. To retry after correcting a failure:

```bash
sudo goblinctl install retry
```

See the
[reference](reference.md) for troubleshooting, naming, SSH access, and maintenance.

Goblin supports current installation formats. Superseded development installations
require deliberate recreation; template redeployment is not an in-place upgrade
workflow. Manage Azure resources manually and preserve data according to the
chosen recreation scope.

## Test locally

The [local WSL/Ubuntu installer](../local/README.md) installs the same bundle from your current
checkout and keeps the live setup page and resulting Goblin installation available
for manual testing, including from a Windows host through WSL.

For fast ChatGPT plugin feedback, keep a dedicated Azure test VM as the HTTPS
gateway and route it to your WSL build with the explicit
[development relay](../../docs/chatgpt-local-testing.md). Production routes directly
to the Goblin instance installed on Azure.

## Cost and removal

The default VM is `Standard_D4s_v6` (4 vCPUs, 16 GiB RAM) with a 128 GiB Standard
SSD OS disk. Azure charges apply, including for resources left after a failed deployment.
To remove the deployment, delete its dedicated resource group. **This deletes
all resources and data in that group.**
