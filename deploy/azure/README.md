# Deploy Goblin infrastructure to Azure

Deploy a customer-owned Ubuntu VM, a static public IP with an Azure-managed
hostname, single-node K3s, and the Kubernetes Agent Sandbox core controller.
Resource names include your company or software name and have no instance number.

**This is an infrastructure foundation, not a working Goblin application.**
It does not yet install a Goblin web interface, administrator onboarding,
connectors, domain-setup agent, or Let's Encrypt certificates. The public hostname
will resolve, but it is not a ready-to-use Goblin setup page.

## Deploy through the Azure portal

[![Deploy to Azure](https://aka.ms/deploytoazurebutton)](https://portal.azure.com/#view/Microsoft_Azure_CreateUIDef/CustomDeploymentBlade/uri/https%3A%2F%2Fraw.githubusercontent.com%2Fjgador%2Fgoblin%2Fmaster%2Fdeploy%2Fazure%2Fazuredeploy.json/uiFormDefinitionUri/https%3A%2F%2Fraw.githubusercontent.com%2Fjgador%2Fgoblin%2Fmaster%2Fdeploy%2Fazure%2FuiFormDefinition.json)

The button loads both the ARM template and `uiFormDefinition.json`. Both files
must be published and publicly accessible at the linked GitHub URLs. The form
uses Azure's built-in `Microsoft.Compute.CredentialsCombo` control to generate
and download a key during creation; the ARM template alone does not provide
that dialog.

1. Sign in and select your subscription, resource group, and region on **Basics**.
   Use a dedicated resource group; `rg-goblin-prod` is the suggested default name.
   Keep **Company or software name** as `Goblin` or replace it with your own name.
2. On **Save your VM access key**, choose **Generate new key pair**. Technical
   users can instead choose a public key already in Azure or supply their own.
3. Leave **Enable direct SSH access now** unchecked unless you need terminal
   access immediately. Enabling it also requires your source IP/CIDR.
4. Review **Advanced settings**; resource-name overrides are optional.
5. On **Review + create**, select **Create**, then Azure's **Download private key
   and create resource** action. Save the downloaded file for later VM access.
6. Follow installation in Azure's deployment page. After success, open
   **Outputs** for resource names, public IP, hostname, the readiness command,
   and an SSH command with instructions for using your saved key later.

The form explains the download in plain language:

> Save this file somewhere safe: you will need it if you want to connect directly
> to your VM later. It is not needed for everyday Goblin use. Azure will not let
> you download the same private key again.

Azure keeps the public key and supplies it to the template. The private key is
downloaded to the customer's computer and is not passed to the template, put in
deployment outputs, or stored in Key Vault. If an existing public key is used,
the customer must already have its matching private key; no new download occurs.
Key download does not enable public SSH by itself.

The portal form explicitly selects a resource group so Azure's credentials
control has a group for its SSH-key resource. That selection is passed as the
template's `resourceGroupName` override. The raw ARM/CLI entry point still
generates the resource group name when the override is omitted.

For a local preview, open the [Form view sandbox](https://aka.ms/form/sandbox),
select package type **CustomTemplate**, load `azuredeploy.json`, replace the
generated form with `uiFormDefinition.json`, and select **Preview**. Selecting
**Create** starts a real deployment. Before distributing the installer, verify
key generation, the download dialog, and VM access using the downloaded key in
a live Azure portal deployment; schema validation alone does not exercise them.

Users can also upload just `azuredeploy.json` through Azure Portal **Deploy a
custom template → Build your own template in the editor → Load file**, or use
the CLI instructions below. These raw-template paths accept an existing public
key or no SSH key; they do not run the guided key-download workflow. The ARM JSON
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

This is a **subscription-scoped deployment** so it can create the resource group
with a generated or overridden name. The deploying identity needs permission to
deploy at subscription scope and create the resource group and its resources;
subscription-level Contributor is one way to grant those permissions. Resource
group-only access is insufficient for this entry point.

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
disabled in both modes. The guided portal flow installs the public key that
matches the downloaded private key. A raw-template deployment that omits the
optional key has no SSH login credential.
K3s includes Traefik, but no Goblin ingress or trusted HTTPS certificate is
configured. Do not interpret a Traefik response as an installed application.

Kubernetes data uses the VM's managed OS disk. This is a single-node installation
without automatic backups or high availability. Deleting the VM detaches its disk
and NIC; deleting the resource group also deletes the resources and data inside it.

## Use the saved key or add SSH access later

If you used the guided download flow, the matching public key is already installed
on the VM. When direct access is needed, allow inbound TCP port 22 from your IP
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

Edit `main.bicep`, `modules/vm.bicep`, or `bootstrap.sh`, then regenerate the
self-contained portal artifact with Bicep:

```bash
bicep build deploy/azure/main.bicep --outfile deploy/azure/azuredeploy.json
bash -n deploy/azure/bootstrap.sh
```

Commit the regenerated JSON together with its sources. Then run Azure's validation
command above with real parameters in the intended subscription. Compilation and
validation do not replace a live deployment check of image pulls, runtime behavior,
and regional capacity. Update pinned versions and their digests together.

Validate `uiFormDefinition.json` against its published Azure Form view schema and
check that every `view.outputs.parameters` key matches an ARM parameter. The
credentials control's `sshPublicKey` is empty during review when generating a
new key; Azure supplies it after the creation/download step. The template keeps
an empty default to permit that pre-generation review and raw deployments without
SSH. Validate the completed portal flow before treating the key as installed.
