# Azure deployment reference

For deployment steps, see the [Azure deployment guide](README.md).

## Deployment requirements

Your Azure account needs permission to deploy resources in the selected group
and, if creating a group, permission to create it in the subscription.
Check pricing, quota, and availability in your region. Alternative VM sizes must
support x64 Ubuntu and Trusted Launch.

## Goblin password

On **Basics**, enter **Goblin password** and **Confirm Goblin password**. Azure
checks that they match. Even a one-character password is accepted. A non-blank
password is required, with no character-mix requirement and a maximum of 128
characters. Tabs, line breaks, and control characters are rejected. Spaces and
special characters are preserved exactly. This password opens Goblin; it is
separate from VM SSH and AI provider credentials.

Both ARM entry points require `goblinPassword` as a secure string without a
default. The confirmation stays in the portal form; only the password is sent
to the deployment. For CLI deployments, omit it from the parameter file and
let Azure CLI prompt for the missing secure parameter:

```bash
az deployment sub create \
  --name goblin \
  --location southeastasia \
  --template-file deploy/azure/main.bicep \
  --parameters @deploy/azure/azuredeploy.parameters.example.json
```

Avoid putting the password in command arguments or checked-in parameter files.
The VM extension receives it through `protectedSettings`. The bootstrap passes
it to the hashing helper through stdin. The background worker later creates the
`goblin-owner-password` Kubernetes Secret in `goblin`. Only a salted PBKDF2-SHA256 verifier
(600,000 iterations, 16-byte random salt) is stored in that Secret. Neither the
password nor the verifier appears in deployment outputs, public status, or logs.
The root-only verifier stays in `/var/lib/goblin/install/private/owner-password`
so retries can reuse it without asking for the password again.

Azure's Custom Script extension can retain its generated `script.sh` under
`/var/lib/waagent/custom-script/download/<run>/`. That root-readable script
contains the original password encoded as Base64, which is reversible. Bootstrap
cleanup removes its temporary hash file and working directory, but does not
remove this extension-managed copy.

The preview manifest mounts the Secret read-only and configures
`GOBLIN_PASSWORD_HASH_FILE`. If the Secret is missing, the pod cannot start; if
its verifier is invalid, Goblin refuses to start. Workspace login uses only the
configured password.

Existing installations in `goblin-preview` require a
[namespace migration](../../docs/authentication-preview.md#migrate-from-earlier-deployment-names).
Updating the template creates the password Secret in `goblin`; it does not move
the old application's persistent data.

For an existing Azure installation, redeploy the updated template with the same
resource names and Goblin password. The background worker builds/imports the application,
configures its public route, and replaces the pod to load the image, origin, and
password verifier. For a password change, redeploy with the new password.
To restart the application separately:

```bash
sudo k3s kubectl delete pod -n goblin -l app=goblin-auth
```

The Sandbox controller recreates the pod with the same persistent data. The
saved provider connection is preserved. Reuse your current Goblin password on
ordinary redeployments. Provisioning configures public HTTP; HTTPS is a separate
setup step.

## Naming

Names use Microsoft's [resource abbreviations](https://learn.microsoft.com/en-us/azure/cloud-adoption-framework/ready/azure-best-practices/resource-abbreviations)
with your company name and environment (`prod` by default):

```text
Default:        <resource-abbreviation>-goblin-<environment>
Custom company: <resource-abbreviation>-<company>-goblin-<environment>
```

Use 2–24 letters, numbers, spaces, or hyphens for the company name, starting with
a letter and ending with a letter or number. Generated names use lowercase and
replace spaces with hyphens; for example, `Acme` produces `vm-acme-goblin-prod`.

For the default company name `Goblin` and environment `prod`:

| Resource | Default name |
| --- | --- |
| Resource group | `rg-goblin-prod` (suggested) |
| Virtual machine | `vm-goblin-prod` |
| Public IP | `pip-goblin-prod` |
| Virtual network | `vnet-goblin-prod` |
| Subnet | `snet-goblin-prod` |
| Network interface | `nic-goblin-prod` |
| Network security group | `nsg-goblin-prod` |
| OS disk | `osdisk-goblin-prod` |
| Public DNS label | `goblin-prod` |

The resource group is selected on **Basics**. Customize other names under
**Advanced settings → Optional resource-name overrides**; blank fields use the
defaults. Keep names stable on updates: changing a name creates a new resource.

There is no instance number or generated suffix. The DNS label must be available
in the region; change **Public hostname prefix** if it is taken. For example,
the default hostname in Southeast Asia is
`goblin-prod.southeastasia.cloudapp.azure.com`.

## Check readiness or diagnose installation

Open the VM in Azure Portal, select **Run command → RunShellScript**, and run:

```bash
cat /var/lib/goblin/bootstrap-status
cat /var/lib/goblin/install/status.json
systemctl status goblin-installer.service --no-pager
tail -n 100 /var/log/goblin-installer.log
```

`bootstrap-status` reports `running`, `setup-ready`, or `failed` for the Azure
extension only. `setup-ready` means the status UI responded and the background
worker was launched. The status JSON reports `waiting`, `running`, `failed`, or
`ready` for the full installation, with per-step timestamps and attempt counts.
Detailed logs stay on the VM; only safe progress messages are publicly readable.
The UI has no install/retry controls and does not expose configuration or logs.

If the Azure extension fails, inspect `/var/log/goblin-bootstrap.log`. If a
background step fails, the status page identifies it and the Azure deployment
can still show success. After fixing an external issue such as blocked downloads,
retry through SSH or Azure Run Command:

```bash
sudo systemctl start goblin-installer.service
```

The worker resumes after reboot while installation is incomplete. It rechecks
cluster readiness and reapplies manifests; it does not trust old completed-step
markers, recreate PVCs, or regenerate passwords. A completed source download is
reused across retries, including when a branch has moved. An explicit template
redeployment starts a fresh source download and can change the owner password.
Azure does not rerun an unchanged successful extension automatically.

Once Kubernetes exists, additional diagnostics are available:

```bash
k3s kubectl get nodes -o wide
k3s kubectl get deployments -n cert-manager
k3s kubectl get deployment -n agent-sandbox-system agent-sandbox-controller
k3s kubectl get sandbox,pods,svc,ingress -n goblin
journalctl -u k3s --no-pager -n 100
```

## What deployment success means

The VM extension embeds a deterministic Python zipapp and its checksum. It waits
only for base OS preparation, password hashing, the status service, and the
background worker launch. Ubuntu's Python standard library runs the bundle; no
SDK, cluster, Docker, application build, or external bundle release is needed
before Azure returns. Per-VM settings and the password verifier are written
locally and are never included in the reusable bundle.

The independent systemd worker installs and checks:

1. K3s `v1.36.4+k3s1`: ready API, registered node, and ready node.
2. Cert-manager `v1.21.2`: all CRDs, controllers, and admission webhook.
3. The Goblin owner password Secret and Agent Sandbox `v1.0.2`.
4. The Goblin image, built with Docker and imported into K3s.
5. The Goblin workload and its internal Traefik route.
6. Public ingress and application readiness at the assigned hostname.

Cert-manager 1.21 supports Kubernetes 1.33–1.36. Its readiness probe submits a
Certificate with `--dry-run=server`, so no Certificate, Issuer, CA, or Secret is
created. Installing cert-manager does not modify PostgreSQL configuration,
connection strings, database credentials, or authentication. PostgreSQL remains
an explicit separate setup step. Run `bash deploy/postgres/setup.sh` from the
checkout to issue database certificates and configure password-free Npgsql
connections; see the [database guide](../../docs/database.md). HTTPS remains
independent.

Upstream installer/manifests use pinned versions and verified SHA-256 digests.
Outbound internet access is required for downloads and image pulls. The Ubuntu
24.04 LTS image uses Azure's latest revision. Agent Sandbox extensions are not
installed. Existing K3s services are reused rather than automatically upgraded.

## Status UI and public URL handoff

`goblin-setup.service` runs as an unprivileged dynamic user. It reads only the
public status file and bundled assets. `goblin-installer.service` runs as root;
only this worker can change cluster resources. A file lock serializes attempts,
and status writes use atomic replacement with fsync.

The setup page initially owns port 80. A HelmChartConfig keeps Traefik's Service
as `ClusterIP`; the worker checks `/readyz` and the application page through
that internal route. It then records the handoff, stops the setup listener,
switches Traefik to `LoadBalancer`, and verifies both the node ingress path and
the public hostname. The browser reconnects and opens Goblin at the same URL.
Provisioning checks setup over a local Unix socket so existing ServiceLB rules
cannot send its health check to an older Goblin deployment.

If activation fails, the worker restores `ClusterIP`, waits for ServiceLB pods
to release the host ports, and restarts the status UI. A persistent marker also
allows recovery after a killed worker or reboot during activation. If Kubernetes
itself cannot respond, automatic network rollback may also fail; use SSH or Azure
Run Command to inspect Traefik/ServiceLB. The next attempt retries recovery.

After success both setup units are disabled. The UI stays stopped across reboot,
and the worker exits. Temporary downloads are removed; the small bundle, status,
verifier, and local logs remain. To repeat readiness checks and repair a completed
installation, use these root commands (this briefly takes Goblin offline):

```bash
sudo systemctl stop goblin-installer.service
sudo flock /var/lib/goblin/install/installer.lock python3 /opt/goblin/setup/goblin-setup.pyz state init
sudo systemctl enable --now goblin-installer.service
```

The installer manages the `traefik` HelmChartConfig and HTTP application overlay.
Preserve any later custom ingress/TLS configuration in the installation source
before re-running it. Reprovisioning an older deployment switches public routing
to the setup page once the background worker has made Traefik internal.

## Application source and public access

The background installer downloads the `goblinSourceRef` branch, tag, or commit from
`github.com/jgador/goblin` (default `master`) over HTTPS, then builds the repository's
Dockerfile on the VM using Docker Engine and Buildx. There is no separate image
registry to configure. The worker installs Ubuntu's `docker.io` and `docker-buildx`
packages when needed, builds with host networking, and imports the `docker save`
archive into K3s. Before a new Docker installation starts, it enables
`ip-forward-no-drop` in Docker's daemon configuration, preserving existing settings
and avoiding a default forwarding drop policy that could interrupt K3s networking.
K3s continues to use its existing containerd runtime to run the application.
The archive's SHA-256 is used as the local image tag and saved in
`/var/lib/goblin/application-source-sha256`; it identifies the downloaded content,
not an independent verification of its publisher. Use a commit SHA for
`goblinSourceRef` in CLI deployments when the installation must be repeatable.
The selected source must include the Dockerfile and deployment overlays. The
setup worker itself comes from the bundle embedded in the Azure template.

The public IP resource's `dnsSettings.fqdn` is passed into bootstrap; the VM's
Linux hostname is not used to construct the URL. The worker renders
`deploy/azure/app`, which shares the PVC, password Secret mount, and application
configuration with `deploy/auth/sandbox.yaml`. It configures:

- `GOBLIN_PUBLIC_ORIGIN=http://<assigned-hostname>` and the explicit
  `GOBLIN_ALLOW_INSECURE_HTTP=true` setting.
- A Traefik Ingress for the same hostname, routing `/` to `goblin-auth:8787`.
- A network policy allowing only Traefik pods in `kube-system` to reach port 8787.

The rendered files remain under `/var/lib/goblin/deploy/azure/app`. The deployment
returns `goblinUrl`, and `/var/lib/goblin/public-url` stores the same address.
The UI uses the password selected during provisioning. HTTP traffic is
unencrypted until HTTPS is configured. To add HTTPS later, configure Traefik's
TLS entry point and certificate, update the app's public origin to `https://...`,
remove the HTTP opt-in, apply the overlay, and recreate the pod. Redeploying this
HTTP template reapplies its HTTP configuration, so preserve later TLS changes
in the deployment source before redeploying.

## Runtime and networking

The cluster uses K3s's default container runtime. **Installing the Agent Sandbox
controller does not itself provide a hardened runtime for untrusted code.**
Configuring gVisor or another suitable runtime, workload permissions, network
policies, and task lifecycles remains necessary before running untrusted agent
workloads. The upstream project explains [the runtime boundary](https://github.com/kubernetes-sigs/agent-sandbox#readme).

The network permits public HTTP and HTTPS. Goblin uses HTTP ingress by default. It
does not expose the Kubernetes API to the internet. Public SSH is disabled unless
you supply both a public key and a source address; password authentication is
disabled. The portal flow installs the supplied public key; retain its matching
private key for SSH access.
K3s includes Traefik, and the background worker configures the Goblin ingress. A trusted HTTPS
certificate is not configured. If the hostname returns 404, inspect the Goblin
Ingress host rule; if it returns 503, inspect the Sandbox pod and service endpoints.

Kubernetes data uses the VM's managed OS disk. This is a single-node installation
without automatic backups or high availability. Deleting the VM detaches its disk
and NIC; deleting the resource group also deletes the resources and data inside it.

## Use the saved key or add SSH access later

If you supplied a public key during deployment, it is already installed on the
VM. When direct access is needed, allow inbound TCP port 22 from your IP
in the VM subnet's network security group and connect using the saved private
key, administrator username, and public hostname. You do not need to create
another key pair.

An administrator with VM and network management permissions can replace a lost
key:

1. In Azure Portal, open **SSH keys → Create**, generate a new key pair, and
   download the private key to the administrator's computer. Copy the public key
   from the resulting Azure resource. An existing local key pair also works.
2. Open the VM's **Help → Reset password** page, select **Reset SSH public key**,
   enter the administrator username (`goblinadmin` by default), paste the public
   key, and select **Update**. Creating the Azure SSH-key resource alone does not
   install the key on the VM.
3. In the VM subnet's network security group, add an inbound TCP port 22 rule
   restricted to the administrator's source IP/CIDR.
4. Connect using the matching private key, VM username, and public hostname.

The [Azure VMAccess procedure](https://learn.microsoft.com/en-us/troubleshoot/azure/virtual-machines/linux/troubleshoot-ssh-connection#use-the-azure-portal)
uses the VM agent and requires it to be working. VMAccess can change SSH server
configuration; keep password authentication disabled when enabling key-based
access. Azure's **Run command** is also available for diagnostics without opening
SSH, provided the VM agent is responsive.

Azure retains the public key; keep the downloaded private key on the
administrator's computer. If it is lost, repeat the process with a new key pair.
Remove public keys that should no longer grant access. Close the port 22 rule
when temporary access is no longer needed. These are manual administration
changes: later template deployments can reset network rules, so review the
planned networking changes before redeploying. Use VMAccess to manage keys on an
existing VM; changing deployment parameters is not a substitute for that step.

### Windows SSH private-key permissions

Use these steps for Windows OpenSSH in PowerShell after downloading the Azure
private key. Apply permission changes to the `.pem` file itself. These are local
Windows file permissions, separate from the Goblin password and the VM's SSH
public-key configuration.

Start with the error immediately before `Permission denied (publickey)`:

| Message | Meaning and next step |
| --- | --- |
| `Load key "goblin-pem.pem": Permission denied` | Windows prevented SSH from reading the local file. Check ownership and grant your account read access. |
| `UNPROTECTED PRIVATE KEY FILE`, `bad permissions`, or `Permissions ... are too open` | OpenSSH rejected the key's permissions. Keep your own read access and remove access for unrelated accounts. |
| Only `Permission denied (publickey)` remains after the key loads | Check the VM address, administrator username, and whether the VM has the public key matching this private key. |

For example, this sequence indicates a local key-loading failure before SSH
could authenticate with that key:

```text
Load key "goblin-pem.pem": Permission denied
goblinadmin@YOUR_VM_HOSTNAME: Permission denied (publickey).
```

Owning the file does not by itself grant permission to read its contents. If you
disabled inheritance and removed all permission entries, Windows denies read
access even to the owner. Restore an explicit **Allow → Read** entry for the
account running SSH.

The downloaded `.pem` file contains your private SSH key. Someone who can read
it could use it to authenticate as you wherever its matching public key is
authorized. OpenSSH checks the file's permissions and rejects keys readable by
unrelated accounts. An `UNKNOWN\UNKNOWN` entry is an account identifier (SID)
that Windows cannot resolve to a name. Removing its permission entry removes
its access to the file; it does not delete an account.

#### Inspect the key and restore read access

Open PowerShell as the Windows account you normally use for SSH. Adjust the
filename to match your downloaded key; the account name is detected automatically:

```powershell
$keyPath = "$env:USERPROFILE\.ssh\goblin-pem.pem"
$windowsAccount = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
icacls $keyPath
$acl = Get-Acl -LiteralPath $keyPath
$acl | Select-Object Owner
$acl.Access | Format-Table IdentityReference, FileSystemRights, AccessControlType, IsInherited -AutoSize
```

These commands display permissions without revealing the private key's contents.
Common markers are `(F)` for full control, `(R)` for read, `(M)` for modify, and
`(I)` for a permission inherited from the parent folder. An empty access list
with inheritance disabled explains the unreadable-file case above.

If you own the file but lack read permission, grant your Windows account an
explicit read entry:

```powershell
icacls $keyPath /grant:r "${windowsAccount}:(R)"
icacls $keyPath
```

`/grant:r` replaces explicit allow permissions for the named account. It does
not remove other accounts' access or override an explicit deny. If inheritance
is already disabled and only your account has access, proceed to reconnect.
If `icacls` fails, or an explicit deny still blocks your account, use the
File Explorer steps below to check ownership and permissions before continuing.

#### Remove overly broad permissions

First confirm the read grant above succeeded. Then disable inheritance and
remove inherited permission entries:

```powershell
icacls $keyPath /inheritancelevel:r
icacls $keyPath
```

The explicit read grant survives this step. Disabling inheritance first without
preserving or adding your own entry can leave the file unreadable. Explicit
permissions for other accounts also survive, so inspect the remaining entries
and remove unrelated access through File Explorer.

You can also perform the entire repair through File Explorer:

1. Right-click the `.pem` file and open **Properties → Security → Advanced**.
2. Check **Owner**. If needed, select **Change**, enter the Windows account you
   use for SSH, select **Check Names**, and confirm. Changing ownership may
   require administrator approval; the owner should still be your SSH account.
3. Add an explicit entry for your account: **Add → Select a principal**, enter
   that account, then select **Check Names → OK**. Set **Type** to **Allow** and
   enable **Read**, then save the entry. Resolve any unintended **Deny** entry
   applying to your account, because a deny can override the read grant.
4. If inheritance is enabled, select **Disable inheritance → Convert inherited
   permissions into explicit permissions on this object**.
   This preserves the current entries so you can review them individually.
   If inheritance is already disabled, leave it disabled.
5. Remove entries for unrelated users or groups, such as **Everyone**, **Users**,
   **Authenticated Users**, or the unknown SID identified in the SSH error.
   Keep your own account's **Allow → Read** entry. Removing every entry causes
   the `Load key ...: Permission denied` error.
6. Select **Apply**, then **OK**. Run `icacls $keyPath` again to confirm your
   account has explicit read access and unrelated accounts cannot read the key.

#### Reconnect and handle the host authenticity prompt

Replace `YOUR_VM_HOSTNAME` with the deployment's public hostname or IP address.
Use the administrator username chosen during provisioning (`goblinadmin` by default):

```powershell
ssh -i $keyPath goblinadmin@YOUR_VM_HOSTNAME
```

The first-connection prompt, `The authenticity of host ... can't be established`,
is separate from private-key file permissions. It means SSH has no saved host
key for that address. Verify the displayed fingerprint through Azure Portal:
open the VM's **Run command → RunShellScript** and, for an ED25519 host key, run:

```bash
ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub
```

Compare the `SHA256:...` value with the SSH prompt. If they match, enter the full
word `yes`; `y` is not accepted. SSH saves the host key in `known_hosts`, so that
prompt normally disappears on later connections to the same address.

If the key-loading error disappears but `Permission denied (publickey)` remains,
confirm that you are connecting to the intended VM with the correct administrator
username and matching private key. The VM needs the corresponding public key for
that user; downloading or creating an Azure SSH-key resource alone does not add
it to an existing VM. Follow [Use the saved key or add SSH access later](#use-the-saved-key-or-add-ssh-access-later)
if the installed public key needs to be replaced.

References: Microsoft's [`icacls` command documentation](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/icacls)
and [Windows OpenSSH file-permission guidance](https://github.com/PowerShell/Win32-OpenSSH/wiki/Security-protection-of-various-files-in-win32-openssh).

## Maintain and validate the template

Edit the Bicep or bootstrap sources, then regenerate both self-contained ARM
artifacts. Keep the shared parameter defaults and naming rules in `main.bicep`
and `portal.bicep` aligned:

```bash
python3 deploy/azure/build-setup-bundle.py
bicep build deploy/azure/main.bicep --outfile deploy/azure/azuredeploy.json
bicep build deploy/azure/portal.bicep --outfile deploy/azure/azuredeploy.portal.json
bash -n deploy/azure/bootstrap.sh deploy/azure/install-app.sh deploy/azure/setup/installer.sh
sh -n deploy/azure/missing-ssh-key.sh
npm test
```

Setup changes are embedded in the regenerated templates; they need no separate
bundle download or release. Application builds still download `master` by default,
so application changes must reach the selected source ref before deployment. A
CLI deployment can select a published commit using `goblinSourceRef`.

Commit the generated `setup-bundle.b64`, `setup-bundle.sha256`, and ARM JSON together
with their sources. `python3 deploy/azure/build-setup-bundle.py --check` verifies
bundle freshness. The bundle is about 13 KiB, well within the Custom Script 64 KiB
script limit after embedding; tests enforce this limit. Verify changes with a live
portal deployment; compilation does not check image pulls, runtime behavior, or
regional capacity. Update pinned versions and their digests together.

Validate `createUiDefinition.json` against its published CreateUiDefinition schema
and check that `parameters.outputs` maps exactly to `azuredeploy.portal.json`'s
parameters. Do not use the old `uiFormDefinitionUri` route with this file.

The credentials control returns an empty `sshPublicKey` while reviewing a newly
generated key. The template must accept that review state; the extension guard
rejects a missing key at installation time for the portal entry point. Verify
both review validation and the final key handoff in a live portal deployment.
Also verify password masking, matching confirmation, length validation, and
unlocking the installed preview with the deployment password. Automated tests
exercise early Azure completion, the real worker with mocked infrastructure commands,
cert-manager isolation, handoff failure/retry, verifier compatibility, and password
persistence. Browser tests cover live progress, failure, refresh and handoff reconnect.
Use a disposable Ubuntu VM for systemd/reboot and real ServiceLB handoff checks;
mocks cannot establish those behaviors.
