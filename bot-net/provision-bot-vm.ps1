# ─────────────────────────────────────────────────────────────────────────────
# Bot VM provisioning — replaces the App Service approach.
#
# WHY A VM: ComplianceRecordingBot uses Microsoft.Skype.Bots.Media (application-
# hosted media). Per Microsoft docs this CANNOT run on Azure App Service — it needs
# a Windows Server VM with a public IPv4 on the instance and open media ports.
# (App Service only exposes 80/443 via a shared front end and gives no routable IP.)
#
# COST OPTIMIZATION (pay-as-you-go, business hours only):
#   - Standard_B4as_v2 (4 vCPU / 16 GB, AMD, burstable) — cheapest in-spec size
#     (Microsoft documents a 4-vCPU minimum). D-series 4-vCPU are quota-restricted
#     in this subscription; B-series v1 (B4ms) is not offered — Bv2 is.
#   - StandardSSD OS disk (not Premium).
#   - Auto-shutdown 18:30 W. Europe time (DST-correct). Auto-START at 08:30 is added
#     separately once the bot runs on the VM (no point auto-starting a dead VM).
#   - No Bastion. RDP is restricted to the admin IP; day-to-day management uses
#     `az vm run-command` over the control plane (no broad inbound exposure).
#
# Run after: az login --tenant d5663c64-53b6-427d-bd45-ad3d3b91764e
# Idempotent-ish: re-running skips resources that already exist.
# ─────────────────────────────────────────────────────────────────────────────
$ErrorActionPreference = 'Stop'

$SUB      = '0c3d1568-2bf4-4eb6-b037-1b94eb8b5061'
$RG       = 'Call_Transkript_Infra'
$LOCATION = 'westeurope'
$KV       = 'kv-calltranskript-prod'

$VM       = 'vm-bot-calltranskript'
$SIZE     = 'Standard_B4as_v2'
$IMAGE    = 'MicrosoftWindowsServer:WindowsServer:2022-datacenter-azure-edition:latest'
$ADMIN    = 'botadmin'
$OSDISK   = 'osdisk-bot-calltranskript'

$PIP      = 'pip-bot-calltranskript'
$VNET     = 'vnet-bot-calltranskript'
$SUBNET   = 'snet-bot'
$NSG      = 'nsg-bot-calltranskript'
$NIC      = 'nic-bot-calltranskript'

$ADMIN_IP = '87.129.168.51'   # source allowed for RDP (management workstation)
$PW_SECRET = 'BotVmAdminPassword'

function Have($q) { $r = az resource list -g $RG --query $q -o tsv 2>$null; return [bool]$r }

Write-Host "=== Phase 0: Generate + store admin password in Key Vault ===" -ForegroundColor Cyan
# Strong random password (base64 of 18 bytes ~ 24 chars) + guaranteed complexity suffix.
$bytes = New-Object 'System.Byte[]' 18
[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$PW = ([Convert]::ToBase64String($bytes) -replace '[+/=]', 'x') + 'Aa9!'
try {
    az keyvault secret set --vault-name $KV --name $PW_SECRET --value $PW --output none
    Write-Host "  Admin password stored in KV secret '$PW_SECRET'." -ForegroundColor Green
} catch {
    Write-Host "  WARN: could not write to KV (data-plane RBAC?). Granting current user Secrets Officer and retrying..." -ForegroundColor Yellow
    $me = az ad signed-in-user show --query id -o tsv
    $kvId = "/subscriptions/$SUB/resourceGroups/$RG/providers/Microsoft.KeyVault/vaults/$KV"
    az role assignment create --assignee-object-id $me --assignee-principal-type User --role "Key Vault Secrets Officer" --scope $kvId --output none
    Start-Sleep -Seconds 20
    az keyvault secret set --vault-name $KV --name $PW_SECRET --value $PW --output none
    Write-Host "  Admin password stored in KV secret '$PW_SECRET' (after granting role)." -ForegroundColor Green
}

Write-Host "=== Phase 1: Networking (VNet + subnet) ===" -ForegroundColor Cyan
az network vnet create --resource-group $RG --name $VNET --location $LOCATION `
  --address-prefixes 10.42.0.0/24 --subnet-name $SUBNET --subnet-prefixes 10.42.0.0/27 --output none
Write-Host "  VNet/subnet ready." -ForegroundColor Green

Write-Host "=== Phase 2: NSG + rules ===" -ForegroundColor Cyan
az network nsg create --resource-group $RG --name $NSG --location $LOCATION --output none
# RDP: management only, from the admin workstation IP.
az network nsg rule create --resource-group $RG --nsg-name $NSG --name Allow-RDP-Admin `
  --priority 1000 --direction Inbound --access Allow --protocol Tcp `
  --source-address-prefixes "$ADMIN_IP/32" --destination-port-ranges 3389 --output none
# HTTPS: Graph calling notifications / bot signaling endpoint.
az network nsg rule create --resource-group $RG --nsg-name $NSG --name Allow-HTTPS `
  --priority 1010 --direction Inbound --access Allow --protocol Tcp `
  --source-address-prefixes Internet --destination-port-ranges 443 --output none
# Media: Skype.Bots.Media application-hosted media TCP port (InstancePublicPort).
az network nsg rule create --resource-group $RG --nsg-name $NSG --name Allow-Media-TCP `
  --priority 1020 --direction Inbound --access Allow --protocol Tcp `
  --source-address-prefixes Internet --destination-port-ranges 8445 --output none
Write-Host "  NSG rules: RDP(admin), 443, 8445." -ForegroundColor Green

Write-Host "=== Phase 3: Public IP (Standard, static) ===" -ForegroundColor Cyan
az network public-ip create --resource-group $RG --name $PIP --location $LOCATION `
  --sku Standard --allocation-method Static --output none
$PUBIP = az network public-ip show -g $RG -n $PIP --query ipAddress -o tsv
Write-Host "  Public IP: $PUBIP" -ForegroundColor Green

Write-Host "=== Phase 4: NIC ===" -ForegroundColor Cyan
az network nic create --resource-group $RG --name $NIC --location $LOCATION `
  --vnet-name $VNET --subnet $SUBNET --network-security-group $NSG --public-ip-address $PIP --output none
Write-Host "  NIC ready." -ForegroundColor Green

Write-Host "=== Phase 5: Create VM ($SIZE, Windows Server 2022) ===" -ForegroundColor Cyan
# --computer-name must be <=15 chars (Windows NetBIOS limit); the resource name is longer.
az vm create --resource-group $RG --name $VM --location $LOCATION `
  --computer-name BOTVM01 `
  --nics $NIC --image $IMAGE --size $SIZE `
  --admin-username $ADMIN --admin-password $PW `
  --os-disk-name $OSDISK --storage-sku StandardSSD_LRS `
  --nic-delete-option Detach --output none
if ($LASTEXITCODE -ne 0) { throw "VM create failed (exit $LASTEXITCODE) - see error above." }
Write-Host "  VM created." -ForegroundColor Green

Write-Host "=== Phase 6: System-assigned identity + KV Secrets User ===" -ForegroundColor Cyan
$MI = az vm identity assign -g $RG -n $VM --query systemAssignedIdentity -o tsv
if (-not $MI) { $MI = az vm show -g $RG -n $VM --query identity.principalId -o tsv }
$kvId = "/subscriptions/$SUB/resourceGroups/$RG/providers/Microsoft.KeyVault/vaults/$KV"
az role assignment create --assignee-object-id $MI --assignee-principal-type ServicePrincipal `
  --role "Key Vault Secrets User" --scope $kvId --output none
Write-Host "  VM MI ($MI) granted Key Vault Secrets User." -ForegroundColor Green

Write-Host "=== Phase 7: Auto-shutdown 18:30 W. Europe time ===" -ForegroundColor Cyan
az vm auto-shutdown -g $RG -n $VM --time 1830 --output none
# az sets UTC; patch the schedule to W. Europe Standard Time (handles DST).
az resource update -g $RG --resource-type "Microsoft.DevTestLab/schedules" `
  --name "shutdown-computevm-$VM" --set properties.timeZoneId="W. Europe Standard Time" --output none
Write-Host "  Auto-shutdown set: 18:30 Europe/Berlin." -ForegroundColor Green

Write-Host ""
Write-Host "=== DONE ===" -ForegroundColor Green
Write-Host "VM:        $VM ($SIZE)"
Write-Host "Public IP: $PUBIP"
Write-Host "Admin:     $ADMIN  (password in KV secret '$PW_SECRET')"
Write-Host "Mgmt:      az vm run-command invoke -g $RG -n $VM --command-id RunPowerShellScript --scripts '...'"
Write-Host ""
Write-Host "NEXT:" -ForegroundColor Yellow
Write-Host "  - Pick an FQDN that resolves to $PUBIP and issue a matching media cert."
Write-Host "  - Install ASP.NET Core 8 Hosting Bundle on the VM, deploy the bot."
Write-Host "  - Revise bot media ports (signaling 443 vs media 8445; PublicIp=$PUBIP)."
Write-Host "  - Repoint Azure Bot 'bot-calltranskript-prod' calling webhook to the new FQDN."
Write-Host "  - Add auto-START 08:30 weekdays once the bot is verified running."
