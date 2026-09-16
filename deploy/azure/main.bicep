targetScope = 'subscription'

@description('Company or software name. Keep Goblin or replace it with your own name. Use letters, numbers, spaces or hyphens, starting with a letter. Spaces become hyphens and letters become lowercase in default resource names.')
@minLength(2)
@maxLength(24)
param companyName string = 'Goblin'

@secure()
@description('Password for opening Goblin. A password is required, with no minimum length or character-mix requirement (up to 128 characters). This is separate from your Azure, SSH, and AI provider credentials. Use the same password when redeploying unless you intend to change it.')
@maxLength(128)
param goblinPassword string

@description('Public part of the VM access key. The guided portal form supplies it after key generation and private-key download. For CLI deployment, supply an existing public key or leave blank for setup without SSH. Never supply a private key. Set sshSourceAddressPrefix only when direct SSH access is needed.')
param adminSshPublicKey string = ''

@description('Azure region for the resource group and VM. Defaults to the subscription deployment location.')
param location string = deployment().location

@description('Environment included in generated names. No instance number is appended.')
@allowed(['dev', 'test', 'staging', 'prod'])
param environment string = 'prod'

@description('Goblin branch, tag, or commit to build from github.com/jgador/goblin. Use a commit SHA for repeatable installations; master follows the current application.')
@minLength(1)
@maxLength(128)
param goblinSourceRef string = 'master'

@description('Linux administrator username.')
param adminUsername string = 'goblinadmin'

@description('VM size. The default provides 4 vCPUs and 16 GiB RAM; verify regional availability and pricing before deploying. Use an x64 size.')
param vmSize string = 'Standard_D4s_v5'

@description('OS disk capacity in GiB. Kubernetes data is stored on this managed disk.')
@minValue(64)
@maxValue(1024)
param osDiskSizeGB int = 128

@description('Optional advanced setting: source IP/CIDR allowed to connect over SSH, for example 203.0.113.10/32. Public SSH is enabled only when this setting and adminSshPublicKey are both supplied. Azure Portal Run Command remains available without SSH.')
param sshSourceAddressPrefix string = ''

@description('Virtual network address range. Keep it separate from the K3s pod range 10.42.0.0/16 and service range 10.43.0.0/16.')
param virtualNetworkAddressPrefix string = '10.20.0.0/16'

@description('VM subnet address range, contained in the virtual network range.')
param subnetAddressPrefix string = '10.20.0.0/24'

@description('Optional resource group name. Blank uses the generated name (default: rg-goblin-prod). Use a dedicated resource group.')
@maxLength(90)
param resourceGroupName string = ''

@description('Optional VM name. Blank uses the generated name (default: vm-goblin-prod). Must also be a valid Linux hostname.')
@maxLength(64)
param virtualMachineName string = ''

@description('Optional public IP resource name. Blank uses the generated name (default: pip-goblin-prod). This is separate from the DNS label.')
@maxLength(80)
param publicIpName string = ''

@description('Optional virtual network name. Blank uses the generated name (default: vnet-goblin-prod).')
@maxLength(64)
param virtualNetworkName string = ''

@description('Optional subnet name. Blank uses the generated name (default: snet-goblin-prod).')
@maxLength(80)
param subnetName string = ''

@description('Optional network interface name. Blank uses the generated name (default: nic-goblin-prod).')
@maxLength(80)
param networkInterfaceName string = ''

@description('Optional network security group name. Blank uses the generated name (default: nsg-goblin-prod).')
@maxLength(80)
param networkSecurityGroupName string = ''

@description('Optional OS disk name. Blank uses the generated name (default: osdisk-goblin-prod).')
@maxLength(80)
param osDiskName string = ''

@description('Optional Azure public DNS label. Blank uses the generated label (default: goblin-prod). Use lowercase letters, numbers and hyphens, starting with a letter and ending with a letter or number. Must be available in the region; choose another label if occupied.')
@maxLength(63)
param dnsLabel string = ''

var companyPrefix = replace(toLower(trim(companyName)), ' ', '-')
var workloadPrefix = companyPrefix == 'goblin' ? 'goblin' : '${companyPrefix}-goblin'
var nameBase = '${workloadPrefix}-${environment}'
var names = {
  resourceGroup: empty(trim(resourceGroupName)) ? 'rg-${nameBase}' : trim(resourceGroupName)
  virtualMachine: empty(trim(virtualMachineName)) ? 'vm-${nameBase}' : trim(virtualMachineName)
  publicIp: empty(trim(publicIpName)) ? 'pip-${nameBase}' : trim(publicIpName)
  virtualNetwork: empty(trim(virtualNetworkName)) ? 'vnet-${nameBase}' : trim(virtualNetworkName)
  subnet: empty(trim(subnetName)) ? 'snet-${nameBase}' : trim(subnetName)
  networkInterface: empty(trim(networkInterfaceName)) ? 'nic-${nameBase}' : trim(networkInterfaceName)
  networkSecurityGroup: empty(trim(networkSecurityGroupName)) ? 'nsg-${nameBase}' : trim(networkSecurityGroupName)
  osDisk: empty(trim(osDiskName)) ? 'osdisk-${nameBase}' : trim(osDiskName)
}
var effectiveDnsLabel = empty(trim(dnsLabel)) ? nameBase : trim(dnsLabel)
var tags = {
  application: 'goblin'
  company: trim(companyName)
  environment: environment
  managedBy: 'goblin-arm'
}

resource goblinResourceGroup 'Microsoft.Resources/resourceGroups@2025-04-01' = {
  name: names.resourceGroup
  location: location
  tags: tags
}

module infrastructure './modules/vm.bicep' = {
  name: 'goblin-infrastructure'
  scope: goblinResourceGroup
  params: {
    location: location
    names: names
    tags: tags
    dnsLabel: effectiveDnsLabel
    adminUsername: adminUsername
    adminSshPublicKey: adminSshPublicKey
    goblinPassword: goblinPassword
    goblinSourceRef: goblinSourceRef
    vmSize: vmSize
    osDiskSizeGB: osDiskSizeGB
    sshSourceAddressPrefix: trim(sshSourceAddressPrefix)
    virtualNetworkAddressPrefix: virtualNetworkAddressPrefix
    subnetAddressPrefix: subnetAddressPrefix
  }
}

@description('Effective customer resource names, including any overrides.')
output resourceNames object = names

@description('Public IPv4 address of the VM.')
output publicIpAddress string = infrastructure.outputs.publicIpAddress

@description('Azure-managed public hostname serving the Goblin UI over HTTP.')
output publicHostname string = infrastructure.outputs.publicHostname

@description('Open this URL to follow installation progress. It opens Goblin when ready. HTTPS is configured separately.')
output goblinUrl string = 'http://${infrastructure.outputs.publicHostname}'

@description('VM resource ID for administration through the Azure portal or CLI.')
output virtualMachineResourceId string = infrastructure.outputs.virtualMachineResourceId

@description('SSH command using the saved private key. Replace the example key path with the downloaded file. Empty when no public key was supplied.')
output sshCommand string = empty(trim(adminSshPublicKey)) ? '' : 'ssh -i /path/to/saved-key.pem ${adminUsername}@${infrastructure.outputs.publicHostname}'

@description('Instructions for later VM access. The private key is never included in deployment outputs.')
output sshAccessNote string = empty(trim(adminSshPublicKey))
  ? 'No SSH key was supplied. An authorized Azure administrator can add a new public key later.'
  : 'Keep your matching private key for later VM access. ${empty(trim(sshSourceAddressPrefix)) ? 'Public SSH is closed; an administrator must allow port 22 from your IP before connecting.' : 'Public SSH is restricted to the configured source IP or CIDR.'}'

@description('Command for checking installation progress through Azure Run Command, including before Kubernetes exists.')
output readinessCommand string = 'az vm run-command invoke --ids ${infrastructure.outputs.virtualMachineResourceId} --command-id RunShellScript --scripts "cat /var/lib/goblin/bootstrap-status /var/lib/goblin/install/status.json"'
