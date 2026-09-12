# Deploy Goblin infrastructure to Azure

Deploy a customer-owned Ubuntu VM, a static public IP with an Azure-managed
hostname, single-node K3s, and the Kubernetes Agent Sandbox core controller.
Resource names include your company or software name and have no instance number.

**This is an infrastructure foundation, not a working Goblin application.**
It does not yet install a Goblin web interface, administrator onboarding,
connectors, domain-setup agent, or Let's Encrypt certificates. The public hostname
will resolve, but it is not a ready-to-use Goblin setup page.

## Deploy through the Azure portal

[![Deploy to Azure](https://aka.ms/deploytoazurebutton)](https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2Fjgador%2Fgoblin%2Fmaster%2Fdeploy%2Fazure%2Fazuredeploy.portal.json/createUIDefinitionUri/https%3A%2F%2Fraw.githubusercontent.com%2Fjgador%2Fgoblin%2Fmaster%2Fdeploy%2Fazure%2FcreateUiDefinition.json)

The button loads `azuredeploy.portal.json` with `createUiDefinition.json` using
Azure's native custom-template creation flow. Both files must be publicly
accessible at the linked URLs. The installer is intended to keep configuration,
key generation, private-key download, and deployment inside Azure Portal.

**Portal verification pending:** this replaces the Form view route that displayed
only a public-key input. It follows Azure's [Linux VM example](https://github.com/Azure/azure-quickstart-templates/tree/master/quickstarts/microsoft.compute/vm-trustedlaunch-linux)
and [credentials control documentation](https://learn.microsoft.com/en-us/azure/azure-resource-manager/managed-applications/microsoft-compute-credentialscombo).
The new route's selector, download dialog, and final key installation still need
a live portal test. This is a candidate fix, not a verified installation flow.

The intended customer steps are:

1. On **Basics**, select your subscription, resource group, and region. Use a
   dedicated resource group; `rg-goblin-prod` is the suggested default name.
   Keep **Company or software name** as `Goblin` or enter your own name.
2. In the VM credentials section on the same page, choose **Generate new key
   pair** and give the key a name. Existing-key options remain available for
   administrators who already have a key pair.
3. Leave **Advanced settings** at their defaults unless needed. Direct SSH network
   access is closed by default; enabling it requires an allowed source IP/CIDR.
4. On **Review + create**, select **Create**, then **Download private key and
   create resource**. Save the file for direct VM access later. It is not needed
   for everyday Goblin use. Azure does not offer another download of the same
   private key. Using an existing key pair does not produce a new download.
5. Follow installation on Azure's deployment page. After success, open **Outputs**
   for resource names, public IP, hostname, and administration commands.

The public-key text field is labeled **SSH public key**. The private-key download
is a separate action handled by Azure during creation. Only the public key is
passed to the template and installed on the VM. The private key stays on the
customer's computer; it is not in deployment outputs or Key Vault.

The native creation flow selects or creates the resource group before deploying
the resource-group-scoped `portal.bicep` template. It uses the same VM module and
naming defaults as the subscription-scoped `main.bicep` CLI entry point. The portal
uses the resource-group name chosen on **Basics**; the CLI entry point can still
create a group with an automatically generated name. The portal entry point tags
the VM and its resources; it does not change the selected resource group's tags.

For a local UI preview, open the [Create UI Definition Sandbox](https://portal.azure.com/#blade/Microsoft_Azure_CreateUIDef/SandboxBlade),
paste `createUiDefinition.json`, and select **Preview**. The UI preview does not
prove that the key download and deployment work together. For the full test,
publish both linked JSON files and use the button. Verify that **Generate new key
pair** is visible, a private key can be downloaded, and the matching public key is
installed on the VM. Then test access with the downloaded key using restricted SSH.
Do not direct customers to generate keys elsewhere if this check fails; the
in-portal workflow still needs a fix.

The portal template allows an empty public key during review because Azure
creates a new key after the final **Create** action. If the public key is still
missing when the VM extension runs, installation fails in Azure with a
**VM access-key setup** error. That check prevents an apparently successful
installation without the expected SSH key; resources may already exist at that
point. Raw CLI installations can intentionally omit a key.

Users can also upload just `azuredeploy.json` through Azure Portal **Deploy a
custom template → Build your own template in the editor → Load file**, or use
the CLI instructions below. These raw-template paths accept an existing public
key or no SSH key; they do not generate or download a key. The ARM JSON
is self-contained and embeds its modules and bootstrap script.

Administrators with appropriate Azure permissions can use the VM's **Run
command** for troubleshooting without opening SSH or using the downloaded key.

Installation feedback stays in the installation flow; email notifications are
not part of this experience. Azure's deployment page reports a failure if
provisioning or the bootstrap readiness checks fail. Open the failed operation
on that page to inspect its error. Bootstrap failures include the installation
stage and exit code in the `goblin-bootstrap` extension's error output, alongside
the underlying command's diagnostic output. No SSH connection is needed to view
the deployment error.

The current interface is Azure's native deployment page. It does not stream every
bootstrap stage as a live progress display. A future Goblin installation screen
should present progress and failures in place, with a clear explanation and
expandable technical details. It must be able to report provisioning failures
even if the VM or Goblin application never starts.

The portal entry point deploys at **resource-group scope**. The deploying identity
needs permission to deploy the VM, networking, extensions, and SSH-key resource
in the selected group. Creating a new resource group also requires subscription
permission to create it. The CLI entry point uses **subscription scope**, needs
permission to deploy there and create its resource group, and is not suitable
for an identity with access only to an existing resource group.

Azure charges for the VM, managed disk, public IP, and applicable network usage.
The default VM is `Standard_D4s_v5` (4 vCPUs, 16 GiB RAM), with a 128 GiB Standard
SSD OS disk. Check current pricing, quota, and availability in your selected
region. An alternative VM size must support x64 Ubuntu and Trusted Launch.

## Naming

Defaults use Microsoft's [resource abbreviations](https://learn.microsoft.com/en-us/azure/cloud-adoption-framework/ready/azure-best-practices/resource-abbreviations):

```text
Default:        <resource-abbreviation>-goblin-<environment>
Custom company: <resource-abbreviation>-<company>-goblin-<environment>
```

`companyName` defaults to `Goblin`; clients can replace it with their own company
or software name. It accepts a short name of 2–24 characters: letters, numbers,
spaces, and hyphens, beginning with a letter. Default names lowercase it, trim
outer whitespace, and replace spaces with hyphens. The default `Goblin` name
appears only once in generated names. Use a short name without other punctuation. Azure validates the
resulting resource names; the guided form also validates the company-name format.
The original name is retained in the `company` resource tag.

For the default company name `Goblin` and environment `prod`:

| Resource | Default | Optional override parameter |
| --- | --- | --- |
| Resource group | `rg-goblin-prod` | `resourceGroupName` |
| Virtual machine | `vm-goblin-prod` | `virtualMachineName` |
| Public IP | `pip-goblin-prod` | `publicIpName` |
| Virtual network | `vnet-goblin-prod` | `virtualNetworkName` |
| Subnet | `snet-goblin-prod` | `subnetName` |
| Network interface | `nic-goblin-prod` | `networkInterfaceName` |
| Network security group | `nsg-goblin-prod` | `networkSecurityGroupName` |
| OS disk | `osdisk-goblin-prod` | `osDiskName` |
| Public DNS label | `goblin-prod` | `dnsLabel` |

The guided form uses the resource group selected on **Basics**; automatic
resource-group naming applies to raw-template deployments that omit its override.

Each blank override uses its generated default. Nonblank overrides are used as
supplied, apart from trimming surrounding whitespace, and must satisfy Azure's
resource-specific naming rules. The VM name must also be a valid Linux hostname.
The environment can be `dev`, `test`, `staging`, or `prod`.

DNS labels and public IP resource names are independent. In Southeast Asia,
the default public hostname is:

```text
goblin-prod.southeastasia.cloudapp.azure.com
```

There is no instance number or generated suffix. The DNS label must be available
within the Azure region. If it is occupied, supply a different `dnsLabel` and
retry. Reusing the same inputs targets the same resources; for another installation
in the same region, choose a different environment or resource group and DNS label.
Keep names stable on updates: changing a name creates a different resource rather
than renaming an existing one. This template uses a dedicated resource group and
does not adopt an existing network or cluster.

## Deploy from a local checkout

Install Azure CLI and sign in with `az login`. Explicitly select your intended
subscription with `az account set --subscription <subscription-id>`.

From the repository root, copy `deploy/azure/azuredeploy.parameters.example.json`
to a local parameter file outside the repository and adjust the company, region,
and optional overrides. The example needs no SSH key. For direct SSH access, add
`adminSshPublicKey` with your public key and `sshSourceAddressPrefix` with your
allowed source IP/CIDR. An SSH private key or Azure credential must never go into
the parameter file.

Validate first; this command does not provision resources:

```bash
az deployment sub validate \
  --name goblin \
  --location southeastasia \
  --template-file deploy/azure/azuredeploy.json \
  --parameters @/path/to/goblin.parameters.json
```

Deploy:

```bash
az deployment sub create \
  --name goblin \
  --location southeastasia \
  --template-file deploy/azure/azuredeploy.json \
  --parameters @/path/to/goblin.parameters.json
```

The command's `--location` stores deployment metadata. The template's `location`
parameter controls where the VM and resource group are created and defaults to
that deployment location if omitted. A deployment name has a fixed metadata
location; use a different deployment name if you change that location.

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
disabled in both modes. The guided portal flow installs the supplied public key;
the customer must retain its matching private key. A raw-template deployment that omits the
optional key has no SSH login credential.
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

An administrator with VM and network management permissions can enable SSH after
a raw-template installation without a key, or replace a lost key:

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

## Check readiness or diagnose a failed bootstrap

Run the deployment's `readinessCommand` output, or open the VM's Azure Portal
**Run command → RunShellScript** and execute:

```bash
cat /var/lib/goblin/bootstrap-status
k3s kubectl get nodes
k3s kubectl get deployment -n agent-sandbox-system agent-sandbox-controller
tail -n 100 /var/log/goblin-bootstrap.log
```

`bootstrap-status` reports `running`, `ready`, or `failed`. The VM extension fails
the deployment if installation or its readiness checks fail. Earlier resources
can remain provisioned and incur charges after a failed deployment.

After correcting an external problem such as blocked downloads, explicitly rerun
the extension (replace the resource names as needed):

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

This reuses the script already installed on the VM extension. A failed ARM
deployment can also be redeployed from the
portal; an unchanged successful extension is not a general-purpose upgrade job.

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

Commit the regenerated JSON together with its sources. Then run Azure's validation
command above with real parameters in the intended subscription. Compilation and
validation do not replace a live deployment check of image pulls, runtime behavior,
and regional capacity. Update pinned versions and their digests together.

Validate `createUiDefinition.json` against its published CreateUiDefinition schema
and check that `parameters.outputs` maps exactly to `azuredeploy.portal.json`'s
parameters. Do not use the old `uiFormDefinitionUri` route with this file.

The credentials control returns an empty `sshPublicKey` while reviewing a newly
generated key. The template must accept that review state; the extension guard
rejects a missing key at installation time for the portal entry point. Verify
both review validation and the final key handoff in a live portal deployment.
