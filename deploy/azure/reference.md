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
it to the hashing helper through stdin and creates the `goblin-owner-password`
Kubernetes Secret in `goblin`. Only a salted PBKDF2-SHA256 verifier
(600,000 iterations, 16-byte random salt) is stored in that Secret. Neither the
password nor the verifier appears in deployment outputs or bootstrap logs.

Azure's Custom Script extension can retain its generated `script.sh` under
`/var/lib/waagent/custom-script/download/<run>/`. That root-readable script
contains the original password encoded as Base64, which is reversible. Bootstrap
cleanup removes its temporary hash file and working directory, but does not
remove this extension-managed copy.

The preview manifest mounts the Secret read-only and configures
`GOBLIN_PASSWORD_HASH_FILE`. If the Secret is missing, the pod cannot start; if
its verifier is invalid, Goblin refuses to start. An old `owner-token` on the
preview data volume no longer grants access when the password is configured.

Existing installations in `goblin-preview` require a
[namespace migration](../../docs/authentication-preview.md#migrate-from-earlier-deployment-names).
Updating the template creates the password Secret in `goblin`; it does not move
the old application's persistent data.

For an existing Azure installation, redeploy the updated template with the same
resource names and Goblin password. Bootstrap builds/imports the application,
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

## Check readiness or diagnose a failed bootstrap

Open the VM in Azure Portal, select **Run command → RunShellScript**, and execute
the following without opening SSH. You can also use the deployment's
`readinessCommand` output from Azure CLI.

```bash
cat /var/lib/goblin/bootstrap-status
k3s kubectl get nodes
k3s kubectl get deployment -n agent-sandbox-system agent-sandbox-controller
k3s kubectl get sandbox,pods,svc,ingress -n goblin
cat /var/lib/goblin/public-url
tail -n 100 /var/log/goblin-bootstrap.log
```

`bootstrap-status` reports `running`, `ready`, or `failed`. A healthy installation
shows `ready`, a `Ready` node, an available Agent Sandbox controller, and a ready
Goblin Sandbox. The readiness check also retrieves the UI through Traefik using
the assigned hostname and the node's private IP, without waiting on public DNS.

If deployment fails, open the failed operation on Azure's deployment page. The
`goblin-bootstrap` error includes the failing stage, exit code, and diagnostics.
Resources can remain provisioned and incur charges after a failure.

If an older bootstrap fails during **Checking Kubernetes readiness** with
`error: no matching resources found`, the API became ready before the kubelet
registered its node. The updated bootstrap waits for a node to appear before
waiting for its `Ready` condition. Retry with the updated template in the same
resource group, keeping the same resource names and Goblin password.

If **Waiting for Kubernetes node registration** fails, or the node appears but
**Checking Kubernetes node readiness** times out, inspect K3s on the VM:

```bash
k3s kubectl get nodes -o wide
journalctl -u k3s --no-pager -n 100
```

After correcting an external problem such as blocked downloads, redeploy the
current template from the portal and enter the same Goblin password again.
Azure does not return the extension's protected script in `az vm extension show`;
copying its public `settings` is no longer sufficient for a retry. Redeploying
an unchanged successful extension does not rerun bootstrap or upgrade the
installation.

## What deployment success means

The VM extension embeds `bootstrap.sh` and waits for:

- K3s `v1.36.4+k3s1`, a ready Kubernetes API, node registration, and a ready node.
- The Goblin password verifier stored in the `goblin` namespace.
- Agent Sandbox `v1.0.2`, its core CRD, and its controller rollout.
- The Goblin image built with Docker and imported into K3s's `k8s.io` image namespace.
- The Goblin Sandbox ready and the UI responding through Traefik on port 80.

The K3s installer and Agent Sandbox manifest use pinned release URLs and checked
SHA-256 digests. Downloads and container pulls require outbound internet access.
The Ubuntu 24.04 LTS image uses Azure's latest image revision at deployment time.
Agent Sandbox extensions are not installed. The Goblin authentication application
is installed as a Sandbox workload with a persistent data volume.

## Application source and public access

The template downloads the `goblinSourceRef` branch, tag, or commit from
`github.com/jgador/goblin` (default `master`) over HTTPS, then builds the repository's
Dockerfile on the VM using Docker Engine and Buildx. There is no separate image
registry to configure. Bootstrap installs Ubuntu's `docker.io` and `docker-buildx`
packages when needed, builds with host networking, and imports the `docker save`
archive into K3s. Before a new Docker installation starts, it enables
`ip-forward-no-drop` in Docker's daemon configuration, preserving existing settings
and avoiding a default forwarding drop policy that could interrupt K3s networking.
K3s continues to use its existing containerd runtime to run the application.
The archive's SHA-256 is used as the local image tag and saved in
`/var/lib/goblin/application-source-sha256`; it identifies the downloaded content,
not an independent verification of its publisher. Use a commit SHA for
`goblinSourceRef` in CLI deployments when the installation must be repeatable.
The source ref must include this application installer and the Azure overlay.

The public IP resource's `dnsSettings.fqdn` is passed into bootstrap; the VM's
Linux hostname is not used to construct the URL. Bootstrap renders
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
K3s includes Traefik, and bootstrap configures the Goblin ingress. A trusted HTTPS
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

If Windows OpenSSH reports `UNPROTECTED PRIVATE KEY FILE`, `bad permissions`,
or `Permissions ... are too open`, it is refusing to use the local private key.
The resulting `Permission denied (publickey)` does not by itself mean that the
VM's public key is incorrect.

The downloaded `.pem` file contains your private SSH key. Someone who can read
it could use it to authenticate as you wherever its matching public key is
authorized. OpenSSH checks the file's permissions and rejects keys readable by
unrelated accounts. An `UNKNOWN\UNKNOWN` entry is an account identifier (SID)
that Windows cannot resolve to a name. Removing its permission entry removes
its access to the file; it does not delete an account.

To inspect the key's current permissions in PowerShell, adjust the filename to
match your downloaded key:

```powershell
$keyPath = "$env:USERPROFILE\.ssh\ssh-goblin.pem"
icacls $keyPath
```

Common permission markers are `(F)` for full control, `(R)` for read, `(M)` for
modify, and `(I)` for a permission inherited from the parent folder.

For a more readable view, including the file owner:

```powershell
$acl = Get-Acl -LiteralPath $keyPath

$acl | Select-Object Owner

$acl.Access | Format-Table IdentityReference, FileSystemRights, AccessControlType, IsInherited -AutoSize
```

These commands only display permissions. They do not change the file or reveal
the private key's contents.

To correct the permissions through File Explorer:

1. Right-click the `.pem` file and open **Properties → Security → Advanced**.
2. Check that your Windows account owns the file and has read access. Use
   **Change** beside the owner or **Add** to correct these if needed.
3. If inheritance is enabled, select **Disable inheritance → Convert inherited
   permissions into explicit permissions on this object**.
4. Remove access entries for unrelated accounts, including the SID identified
   in the SSH error. Keep your own account's access.
5. Select **Apply**, then **OK**. Inspect the permissions again and retry SSH.

## Maintain and validate the template

Edit the Bicep or bootstrap sources, then regenerate both self-contained ARM
artifacts. Keep the shared parameter defaults and naming rules in `main.bicep`
and `portal.bicep` aligned:

```bash
bicep build deploy/azure/main.bicep --outfile deploy/azure/azuredeploy.json
bicep build deploy/azure/portal.bicep --outfile deploy/azure/azuredeploy.portal.json
bash -n deploy/azure/bootstrap.sh deploy/azure/install-app.sh
sh -n deploy/azure/missing-ssh-key.sh
npm test
```

Publish the application sources and regenerated templates together: the default
installer downloads `master`, so local changes must reach that branch before they
can be installed by the published Azure button. A CLI deployment can select a
published commit using `goblinSourceRef`.

Commit the regenerated JSON together with its sources. Verify changes with a live
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
exercise the rendered bootstrap with mocked infrastructure commands, verifier
compatibility with the C# backend, token rejection, and password persistence.
