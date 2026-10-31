# Azure deployment reference

For deployment steps, see the [Azure deployment guide](README.md).

## Deployment requirements

Your Azure account needs permission to deploy resources in the selected group
and, if creating a group, permission to create it in the subscription.
Check pricing, quota, and availability in your region. Alternative VM sizes must
support x64 Ubuntu and Trusted Launch.

## Goblin password

On **Basics**, enter **Goblin password** and **Confirm Goblin password**; the
portal checks that they match. The password needs at least one non-space
character and at most 128 characters, with no other length or character-mix rule.
Tabs, line breaks, and other control characters are rejected. Spaces and special
characters are kept exactly. This password opens Goblin; it is separate from VM
SSH and AI provider credentials.

Both ARM entry points require `goblinPassword` as a secure string without a
default. Only the password, not the confirmation, is sent to the deployment. For a
CLI deployment, download `azuredeploy.json` from the chosen `goblin-v<version>`
GitHub Release; it already fixes the source SHA and installer for that release.
Leave the password out of the parameter file and let Azure CLI prompt for it:

```bash
az deployment sub create \
  --name goblin \
  --location southeastasia \
  --template-file azuredeploy.json \
  --parameters @deploy/azure/azuredeploy.parameters.example.json
```

Do not put the password in command arguments or checked-in parameter files.
The VM extension receives it through `protectedSettings`, and bootstrap hashes it
through stdin. Only a salted PBKDF2-SHA256 verifier (600,000 iterations, 16-byte
random salt) is stored: in the `goblin-owner-password` Secret in namespace
`goblin`, and in the root-only `/var/lib/goblin/install/private/owner-password`
so retries reuse it. Neither the password nor the verifier appears in deployment
outputs, public status, or logs. If the Secret is missing, the pod cannot start;
if its verifier is invalid, Goblin refuses to start.

**Azure's Custom Script extension can keep its generated `script.sh` under
`/var/lib/waagent/custom-script/download/<run>/`.** That root-readable script
contains the original password in Base64, which is reversible. Bootstrap does not
remove this extension-managed copy.

To change the password of a completed installation, run
`sudo goblinctl password set --replace` in an SSH session on the VM (see
[goblinctl](../../docs/goblinctl.md#commands)), or redeploy the template with the
same resource names and the new password. A redeployment with changed inputs
starts a new installation attempt: the worker builds and imports the application,
configures its public route, and replaces the pod to load the image, origin, and
password verifier. Azure does not rerun an unchanged successful extension.
Redeployment is not an upgrade path for superseded installation formats.

To restart the application without other changes:

```bash
sudo k3s kubectl delete pod -n goblin -l app=platform
```

The Sandbox controller recreates the pod with the same persistent data and saved
provider connection.

## Naming

Names use Microsoft's [resource abbreviations](https://learn.microsoft.com/en-us/azure/cloud-adoption-framework/ready/azure-best-practices/resource-abbreviations)
with your company name and environment (`prod` by default; also `dev`, `test`, or
`staging`):

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

The resource group is selected on **Basics**. Change other names under
**Advanced settings → Optional resource-name overrides**; blank fields use the
defaults. Keep names stable on updates: changing a name creates a new resource.

There is no instance number or generated suffix. The DNS label must be available
in the region; change **Public hostname prefix** if it is taken. For example,
the default hostname in Southeast Asia is
`goblin-prod.southeastasia.cloudapp.azure.com`.

## Check readiness or diagnose installation

Open the VM in Azure Portal, select **Run command → RunShellScript**, and run the
commands below. Run Command uses root; over SSH, run them with `sudo`.

```bash
cat /var/lib/goblin/bootstrap-status
cat /var/lib/goblin/install/status.json
systemctl status goblin-installer.service --no-pager
tail -n 100 /var/log/goblin-installer.log
```

`bootstrap-status` reports `running`, `setup-ready`, or `failed` for the Azure
extension only. `setup-ready` means the status page responded and the background
installer started. The status JSON reports `waiting`, `running`, `failed`, or
`ready` for the full installation, with per-step timestamps and attempt counts.
The public page shows safe progress and a filtered installation log; private
diagnostics stay on the VM.

If the Azure extension fails, inspect `/var/log/goblin-bootstrap.log`. If a
background step fails, the status page shows it, and the Azure deployment can
still show success. After fixing an external issue, such as blocked downloads,
retry through SSH or Run Command. The Azure status page has no retry button.

```bash
sudo goblinctl install retry
```

An interrupted installation resumes after reboot. A failed attempt disables its
installer and needs an explicit retry; reboot alone does not retry it. A retry
checks cluster readiness again and reapplies manifests. It does not trust old
completed-step markers, recreate PVCs, or regenerate passwords. A completed source
download is reused across retries.

Once Kubernetes exists, more diagnostics are available:

```bash
k3s kubectl get nodes -o wide
k3s kubectl get deployments -n cert-manager
k3s kubectl get deployment -n sandbox sandbox
k3s kubectl get sandbox,pods,svc,ingress -n goblin
journalctl -u k3s --no-pager -n 100
```

If readiness keeps returning HTTP 404, inspect the Ingress events and NGINX
controller logs. A ready controller can still reject an individual Ingress:

```bash
sudo k3s kubectl -n goblin describe ingress goblin
sudo k3s kubectl -n kube-system logs deployment/goblin-nginx-controller --tail=100
```

F5 rejects an Ingress when a service in its `nginx.org/websocket-services`
annotation is not one of its backends. The installer and the development relay
change that annotation together with the `/` backend (`platform`,
`goblin-installation` during setup, or `goblin-dev-relay`). If the hostname
returns 503, inspect the Sandbox pod and the `platform` Service endpoints.

## What deployment success means

The VM extension waits for cloud-init, downloads the versioned, checksum-pinned
native goblinctl archive, starts the status page, and starts the separate
installer. Azure reports success only after the download is verified and the
status page has bound its port. The binary contains the setup UI and installer
assets; serving setup needs no cluster, Docker, or application build.

The `goblin-installer.service` worker installs and checks:

1. K3s `v1.36.5+k3s1`: ready API, registered node, and ready node.
2. cert-manager `v1.21.3`: all CRDs, controllers, and admission webhook.
3. The Goblin owner password Secret and Agent Sandbox `v1.0.4` (core only, no
   extensions).
4. The Goblin image, built with Docker and imported into K3s.
5. Certificate-authenticated PostgreSQL and schema migrations.
6. F5 NGINX Ingress Controller 5.7.0 (Helm chart 2.8.0), the Goblin workload,
   execution namespace, and internal NGINX route.
7. Public progress and challenge routing, Let’s Encrypt certificate issuance, and
   browser-trusted HTTPS at Azure’s assigned hostname.
8. Public application readiness and OAuth/MCP discovery.

The K3s 1.36 and cert-manager 1.21 pins stay within the
[published cert-manager 1.21 support range](https://cert-manager.io/docs/releases/)
of Kubernetes 1.33–1.36. Independent steps run in parallel; see
[parallel installation](../../docs/goblinctl.md#parallel-installation-and-live-progress)
for the order, download resume rules, and image-pull limits. The cert-manager
check uses a server-side dry run and creates no resources. For PostgreSQL repair,
see the [database guide](../../docs/database.md#installation-and-certificates).

Upstream installers, manifests, the K3s binary and airgap image archive, and the
NGINX chart use pinned versions and verified SHA-256 digests. Verified downloads
stay in `/var/cache/goblin/downloads`. Bundled Traefik is disabled. Outbound
internet access is required for downloads and image pulls. The VM uses the latest
Ubuntu 24.04 LTS image. An existing K3s is reused only when its version and
Traefik-disabled configuration match the pins; otherwise recreate the installation.

## Status UI and public URL handoff

`goblin-setup.service` runs as an unprivileged dynamic user and reads only the
public status file and bundled assets. `goblin-installer.service` runs as root;
only it changes cluster resources. A file lock in
`/var/lib/goblin/install/installer.lock` allows one attempt at a time.

The setup page first listens on port 80, and NGINX starts with a `ClusterIP`
Service while the worker checks internal routing. For public HTTPS, the worker
moves the progress listener to the VM’s private address on port 18080, routes the
`goblin` Ingress to it, and switches NGINX to `LoadBalancer` on ports 80 and 443.
cert-manager solves HTTP-01 by editing that Ingress in place. After the
certificate is Ready and public TLS verifies, the worker routes `/` to Goblin and
turns on HTTP-to-HTTPS redirection. The desired Service type is also saved in the
`goblin-nginx` HelmChart, so VM restarts keep HTTPS.

If activation fails, the worker restores `ClusterIP`, waits for ServiceLB to
release the host ports, and restarts the status page. A persistent marker allows
this recovery after a killed worker or reboot. If Kubernetes cannot respond,
automatic rollback can also fail; inspect NGINX and ServiceLB through SSH or Run
Command. The next attempt retries the recovery.

After success, both setup units are disabled, temporary downloads are removed, and
the native CLI, status, verifier, and logs remain. To repeat the installation
checks and repair a completed installation, run these root commands. This briefly
takes Goblin offline and downloads the source again:

```bash
sudo systemctl stop goblin-installer.service
sudo flock /var/lib/goblin/install/installer.lock goblinctl internal state init
sudo systemctl enable --now goblin-installer.service
```

Certificate Secrets and Goblin PVCs remain across ordinary application restarts.

## Application source and public access

The installer downloads the selected `goblinSourceRef` commit from
`github.com/jgador/goblin` over HTTPS and builds the repository Dockerfile on the
VM with Docker Engine and Buildx (Ubuntu `docker.io` and `docker-buildx`,
installed when missing). There is no image registry to configure. Before Docker
is first installed, the worker sets `ip-forward-no-drop` in
`/etc/docker/daemon.json`, keeping other settings, so Docker does not drop K3s
traffic. K3s keeps its own containerd runtime.

The build uses a private Docker client configuration at
`/var/lib/goblin/install/private/work/docker-config` and
`unix:///var/run/docker.sock`. Personal Docker credentials, helpers, contexts, TLS
settings, and builders are not used or changed. Public base images are pulled
anonymously, so registry rate limits apply. Docker images and build cache stay on
the VM after setup.

The SHA-256 of the source archive is the local image tag and is saved in
`/var/lib/goblin/application-source-sha256`. It identifies the downloaded content;
it does not verify the publisher. Installer behavior comes from the published
goblinctl binary; see [releases](../../docs/releases.md).

The worker renders `deploy/azure/platform` to `/var/lib/goblin/deploy/azure/platform`
with the public IP's `dnsSettings.fqdn`. The overlay sets
`GOBLIN_PUBLIC_ORIGIN=https://<assigned-hostname>` and `GOBLIN_CHATGPT_ENABLED=true`,
adds an `nginx` Ingress `goblin` routing `/` to `platform:8787` with the
`goblin-public-tls` Secret, and lets the NGINX pods in `kube-system` reach port
8787. `goblinUrl` and `/var/lib/goblin/public-url` contain the HTTPS address;
`setupUrl` is the initial HTTP progress page. For workload, image, and PVC names,
see [Kubernetes names](../../docs/kubernetes-names.md); inspect existing PVC
bindings before changing storage manifests.

## Runtime and networking

The cluster uses K3s's default container runtime. **Installing the Agent Sandbox
controller does not provide a hardened runtime for untrusted code.**
Configure gVisor or another suitable runtime, workload permissions, network
policies, and task lifecycles before running untrusted agent workloads. See the
upstream [runtime boundary](https://github.com/kubernetes-sigs/agent-sandbox#readme).

The network security group allows public TCP 80 and 443. The Kubernetes API is
not exposed to the internet. Public SSH (port 22) is open only when you supply
both a public key and a source address: in the portal, select **Advanced settings
→ Enable direct SSH access now (advanced)** and enter your IP or CIDR; for the CLI,
set `adminSshPublicKey` and `sshSourceAddressPrefix`. Password authentication is
disabled.

Kubernetes data uses the VM's managed OS disk. This is a single-node installation
without automatic backups or high availability. Deleting the VM detaches its disk
and NIC; deleting the resource group also deletes the resources and data inside it.

## Use the saved key or add SSH access later

If you supplied a public key during deployment, it is already installed on the
VM. When direct access is needed, allow inbound TCP port 22 from your IP
in the VM subnet's network security group and connect with the saved private
key, administrator username, and public hostname. You do not need another key pair.

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
   restricted to the administrator's source IP or CIDR.
4. Connect with the matching private key, VM username, and public hostname.

The [Azure VMAccess procedure](https://learn.microsoft.com/en-us/troubleshoot/azure/virtual-machines/linux/troubleshoot-ssh-connection#use-the-azure-portal)
needs a working VM agent. VMAccess can change SSH server configuration; keep
password authentication disabled. **Run command** also works for diagnostics
without SSH while the VM agent responds.

Keep the private key on the administrator's computer; if it is lost, repeat the
steps with a new key pair. Remove public keys that should no longer grant access,
and close the port 22 rule when temporary access ends. These are manual changes:
later template deployments can reset network rules, so review planned networking
changes before redeploying. Use VMAccess, not deployment parameters, to manage
keys on an existing VM.

### Windows SSH private-key permissions

Use these steps for Windows OpenSSH in PowerShell after downloading the Azure
private key. Apply permission changes to the `.pem` file itself.

Start with the error shown just before `Permission denied (publickey)`:

| Message | Meaning and next step |
| --- | --- |
| `Load key "goblin-pem.pem": Permission denied` | Windows prevented SSH from reading the file. Check ownership and grant your account read access. |
| `UNPROTECTED PRIVATE KEY FILE`, `bad permissions`, or `Permissions ... are too open` | OpenSSH rejected the key's permissions. Keep your own read access and remove access for unrelated accounts. |
| Only `Permission denied (publickey)` remains after the key loads | Check the VM address, administrator username, and whether the VM has the matching public key. |

Ownership alone does not grant read access. Unrelated accounts must not be able
to read the private key. An `UNKNOWN\UNKNOWN` entry is an unresolved SID; removing
its permission does not delete an account.

#### Inspect the key and restore read access

Open PowerShell as the Windows account you use for SSH. Adjust the filename to
match your downloaded key; the account name is detected automatically:

```powershell
$keyPath = "$env:USERPROFILE\.ssh\goblin-pem.pem"
$windowsAccount = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
icacls $keyPath
$acl = Get-Acl -LiteralPath $keyPath
$acl | Select-Object Owner
$acl.Access | Format-Table IdentityReference, FileSystemRights, AccessControlType, IsInherited -AutoSize
```

These commands show permissions without revealing the key. Common markers are
`(F)` full control, `(R)` read, `(M)` modify, and `(I)` inherited from the parent
folder. An empty access list with inheritance disabled explains the unreadable
file.

If you own the file but cannot read it, grant your account explicit read access:

```powershell
icacls $keyPath /grant:r "${windowsAccount}:(R)"
icacls $keyPath
```

`/grant:r` replaces explicit allow entries for the named account. It does not
remove other accounts' access or override an explicit deny. If inheritance is
already disabled and only your account has access, reconnect. If `icacls` fails,
or a deny entry still blocks your account, use the File Explorer steps below.

#### Remove overly broad permissions

First confirm that the read grant above succeeded. Then disable inheritance and
remove inherited entries:

```powershell
icacls $keyPath /inheritancelevel:r
icacls $keyPath
```

The explicit read grant survives this step; disabling inheritance without it can
leave the file unreadable. Explicit entries for other accounts also survive, so
remove unrelated access through File Explorer.

To do the whole repair in File Explorer:

1. Right-click the `.pem` file and open **Properties → Security → Advanced**.
2. Check **Owner**. If needed, select **Change**, enter the Windows account you
   use for SSH, select **Check Names**, and confirm. This may need administrator
   approval.
3. Add an entry for your account: **Add → Select a principal**, enter the account,
   then **Check Names → OK**. Set **Type** to **Allow**, enable **Read**, and save.
   Remove any **Deny** entry that applies to your account.
4. If inheritance is enabled, select **Disable inheritance → Convert inherited
   permissions into explicit permissions on this object**. If it is already
   disabled, leave it disabled.
5. Remove entries for unrelated users or groups, such as **Everyone**, **Users**,
   **Authenticated Users**, or the unknown SID from the SSH error. Keep your own
   **Allow → Read** entry; removing every entry causes the `Load key ...:
   Permission denied` error.
6. Select **Apply**, then **OK**. Run `icacls $keyPath` again to confirm that your
   account has explicit read access and unrelated accounts do not.

#### Reconnect and handle the host authenticity prompt

Replace `YOUR_VM_HOSTNAME` with the deployment's public hostname or IP address.
Use the administrator username chosen during deployment (`goblinadmin` by default):

```powershell
ssh -i $keyPath goblinadmin@YOUR_VM_HOSTNAME
```

The first-connection prompt, `The authenticity of host ... can't be established`,
means SSH has no saved host key for that address. Check the fingerprint through
the VM's **Run command → RunShellScript**; for an ED25519 host key, run:

```bash
ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub
```

If the `SHA256:...` value matches the prompt, type the full word `yes`; `y` is not
accepted. SSH saves the host key in `known_hosts`.

If the key loads but `Permission denied (publickey)` remains, check that you are
connecting to the intended VM with the correct username and matching private key.
The VM needs the matching public key for that user; follow
[Use the saved key or add SSH access later](#use-the-saved-key-or-add-ssh-access-later)
to replace it.

References: Microsoft's [`icacls` command documentation](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/icacls)
and [Windows OpenSSH file-permission guidance](https://github.com/PowerShell/Win32-OpenSSH/wiki/Security-protection-of-various-files-in-win32-openssh).

## Maintain and validate the template

Edit the Bicep or bootstrap sources, then regenerate the ARM assets and run the
offline checks. Keep the shared parameter defaults and naming rules in `main.bicep`
(subscription scope, CLI) and `portal.bicep` (resource group scope, portal) aligned.

```bash
make deploy-generate   # cargo xtask azure; writes .artifacts/azure/
sh -n deploy/azure/missing-ssh-key.sh
make test
```

Generated ARM JSON is not committed; release preparation generates and publishes
its own assets. Changes to embedded setup assets need a new goblinctl release and
a dependency pin update; see [releases](../../docs/releases.md). `make test` also
checks the Custom Script size limit, shell syntax, and that
`createUiDefinition.json` outputs match `azuredeploy.portal.json` parameters.

These checks stay manual:

- Validate `createUiDefinition.json` against its published CreateUiDefinition
  schema. The portal opens it through `createUIDefinitionUri`, not
  `uiFormDefinitionUri`.
- In a live portal deployment, check review validation and the final key handoff.
  While a new key is being generated, the form returns an empty `sshPublicKey`, so
  the template accepts it; if the key is still missing at installation, the portal
  entry point's bootstrap fails and asks you to redeploy and download the key.
- Check password masking, matching confirmation, length validation, and opening
  the installed Goblin with the deployment password.
- On a disposable Ubuntu VM, check systemd and reboot behavior and the real
  ServiceLB handoff; mocks cannot cover them.
