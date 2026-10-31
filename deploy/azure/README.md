# Deploy Goblin to Azure

Deploy an Ubuntu VM with a static public IP and an Azure-managed hostname. A
background service on the VM installs single-node K3s, F5 NGINX Ingress Controller,
cert-manager, Agent Sandbox, PostgreSQL, and Goblin. Open **`setupUrl`** in the
deployment outputs to follow read-only progress. The page opens the HTTPS
**`goblinUrl`** after the certificate is issued and Goblin is verified.

## Deploy

Azure installation is manual. Repository workflows publish installation assets
without signing in to Azure or managing cloud resources.

1. Open the [website installation page](https://goblinboard.com/install/) (the
   **Deploy to Azure** button in the [root README](../../README.md) links to it),
   choose **Goblin version**, then select **Deploy to Azure**. The recommended
   release is selected by default. In Azure, select your subscription, a dedicated
   resource group (for example `rg-goblin-prod`), and a region. The form shows the
   selected Goblin version.
2. Enter and confirm a **Goblin password** ([rules](reference.md#goblin-password)).
   Save it in your password manager; you use it to open Goblin.
3. Enter the **HTTPS certificate contact email** for Let’s Encrypt. Azure’s
   generated hostname needs no DNS record.
4. For the VM access key, choose **Generate new key pair** or use an existing
   public key.
5. Select **Review + create → Create**. For a new key, select **Download private
   key and create resource** and save the file; it can only be downloaded once.

If Windows OpenSSH cannot read the downloaded `.pem` file or rejects its
permissions, see [Windows SSH private-key permissions](reference.md#windows-ssh-private-key-permissions).

Azure first downloads and verifies the pinned native [goblinctl release](../../docs/goblinctl.md).
Provisioning finishes once the status page responds and the background installer
has started. Kubernetes and Goblin are installed afterward; this can take several
minutes. The page shows each step as waiting, running, complete, or failed.
Closing the browser does not stop installation. Public setup is read-only and
never asks for your password.

The installer builds Goblin from the selected release source and imports it into
K3s. It provisions PostgreSQL with certificate authentication and a loopback host
endpoint on port 5432, applies the schema migrations, and deploys Goblin with its
isolated execution namespace. A database or migration failure stops installation
before Goblin opens. After the internal NGINX route works, NGINX serves progress
and the HTTP-01 certificate challenge publicly. Goblin opens only after trusted
HTTPS is verified; a short reconnect during this switch is normal. After success,
the setup page and installer are disabled. The native CLI, status, and logs stay
on the VM for repair.

Keep public ports 80 and 443 open. Port 80 serves certificate validation and
redirects to HTTPS after setup. cert-manager keeps the certificate and ACME account
in Kubernetes Secrets and renews the certificate automatically.

After setup:

- Open `goblinUrl`, for example `https://goblin-prod.southeastasia.cloudapp.azure.com`,
  and enter your Goblin password.
- **Settings → Cluster → Open cluster** opens read-only Headlamp at
  `<goblinUrl>/headlamp/` with the same password. See [the cluster view](../../docs/cluster-view.md).
- **Settings → Logs → Open logs** opens saved application logs at
  `<goblinUrl>/logs/`. See [logging](../../docs/logging.md).
- [Database setup](../../docs/database.md#installation-and-certificates) covers
  PostgreSQL repair and certificates. [Execution hosting](../../docs/execution-hosting.md)
  covers repository execution and GitHub sign-in. For developer database access
  through an SSH tunnel from WSL, follow [the PostgreSQL Azure workflow](../../docs/database.md#azure-development).

## Verify

After deployment succeeds, open **Outputs** for resource names, the public IP,
hostname, and administration commands. On the VM, open **Run command →
RunShellScript** and run:

```bash
cat /var/lib/goblin/bootstrap-status
```

`setup-ready` means the status page started and the installer launched, not that
Goblin is installed. Check background progress with:

```bash
cat /var/lib/goblin/install/status.json
```

Its overall `status` becomes `ready` after trusted HTTPS, Goblin readiness, and the
OAuth/MCP discovery endpoints are verified. Bootstrap failures appear in Azure and
in `/var/log/goblin-bootstrap.log`. Installer failures appear on the status page and
in `/var/log/goblin-installer.log`. After fixing the cause, retry:

```bash
sudo goblinctl install retry
```

See the [reference](reference.md) for troubleshooting, naming, SSH access, and
maintenance.

Goblin supports only the current installation format. Template redeployment is
not an in-place upgrade. Recreate superseded installations deliberately, and keep
the data you need.

## Test locally

The [local WSL/Ubuntu installer](../local/README.md) runs the same installer with
your current checkout. To test the ChatGPT plugin against a WSL build, route a
dedicated Azure test VM to it with the [development relay](../../docs/chatgpt-local-testing.md).

## Cost and removal

The default VM is `Standard_D4s_v6` (4 vCPUs, 16 GiB RAM) with a 128 GiB Standard
SSD OS disk. Azure charges apply, including for resources left after a failed
deployment. To remove the deployment, delete its dedicated resource group. **This
deletes all resources and data in that group.**
