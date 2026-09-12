# Azure deployment reference

For deployment steps, see the [Azure deployment guide](README.md).

## Deployment requirements

Your Azure account needs permission to deploy resources in the selected group
and, if creating a group, permission to create it in the subscription.
Check pricing, quota, and availability in your region. Alternative VM sizes must
support x64 Ubuntu and Trusted Launch.

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
tail -n 100 /var/log/goblin-bootstrap.log
```

`bootstrap-status` reports `running`, `ready`, or `failed`. A healthy installation
shows `ready`, a `Ready` node, and an available Agent Sandbox controller.

If deployment fails, open the failed operation on Azure's deployment page. The
`goblin-bootstrap` error includes the failing stage, exit code, and diagnostics.
Resources can remain provisioned and incur charges after a failure.

After correcting an external problem such as blocked downloads, a failed
deployment can be redeployed from the portal. To explicitly rerun the extension
using Azure CLI, replace the resource names as needed:

```bash
az vm extension show \
  --resource-group rg-goblin-prod \
  --vm-name vm-goblin-prod \
  --name goblin-bootstrap \
  --query settings --output json > bootstrap-settings.json

az vm extension set \
  --resource-group rg-goblin-prod \
  --vm-name vm-goblin-prod \
  --name CustomScript \
  --extension-instance-name goblin-bootstrap \
  --publisher Microsoft.Azure.Extensions \
  --version 2.1 \
  --settings @bootstrap-settings.json \
  --force-update
```

This reuses the extension's existing script. Redeploying an unchanged successful
extension does not rerun bootstrap or upgrade the installation.

## What deployment success means

The VM extension embeds `bootstrap.sh` and waits for:

- K3s `v1.36.4+k3s1`, a ready Kubernetes API, and a ready node.
- Agent Sandbox `v1.0.2`, its core CRD, and its controller rollout.

The K3s installer and Agent Sandbox manifest use pinned release URLs and checked
SHA-256 digests. Downloads and container pulls require outbound internet access.
The Ubuntu 24.04 LTS image uses Azure's latest image revision at deployment time.
Agent Sandbox extensions and workloads are not installed.

The cluster uses K3s's default container runtime. **Installing the Agent Sandbox
controller does not itself provide a hardened runtime for untrusted code.**
Configuring gVisor or another suitable runtime, workload permissions, network
policies, and task lifecycles remains necessary before running untrusted agent
workloads. The upstream project explains [the runtime boundary](https://github.com/kubernetes-sigs/agent-sandbox#readme).

The network permits public HTTP and HTTPS for future application ingress. It
does not expose the Kubernetes API to the internet. Public SSH is disabled unless
you supply both a public key and a source address; password authentication is
disabled. The portal flow installs the supplied public key; retain its matching
private key for SSH access.
K3s includes Traefik, but no Goblin ingress or trusted HTTPS certificate is
configured. Do not interpret a Traefik response as an installed application.

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
bash -n deploy/azure/bootstrap.sh
sh -n deploy/azure/missing-ssh-key.sh
```

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
