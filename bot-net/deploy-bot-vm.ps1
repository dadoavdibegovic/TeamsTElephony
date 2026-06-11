# ─────────────────────────────────────────────────────────────────────────────
# Deploy the bot to the Windows VM (vm-bot-calltranskript).
#
# Runs the bot as a Windows Service with self-hosted Kestrel (no IIS). Secrets are
# pulled from Key Vault at runtime via the VM's managed identity, so nothing secret
# is shipped in the artifact (appsettings.Production.json holds non-secret config only).
#
# Flow: publish -> zip -> upload to a short-lived blob SAS -> az vm run-command:
#       download, install ASP.NET Core 8 runtime + VC++ redist, register+start service.
#
# Prereqs: az login (tenant d5663c64-...); VM + networking already provisioned
#          (provision-bot-vm.ps1); media cert in KV (BotMediaCertPfx[+Password]).
# Re-runnable: each run redeploys the latest build and restarts the service.
# ─────────────────────────────────────────────────────────────────────────────
$ErrorActionPreference = 'Stop'

$RG        = 'Call_Transkript_Infra'
$VM        = 'vm-bot-calltranskript'
$ACCT      = 'stcallassist'
$CONTAINER = 'botdeploy'
$PROJ      = Join-Path $PSScriptRoot 'ComplianceRecordingBot\ComplianceRecordingBot.csproj'
$PUB       = Join-Path $PSScriptRoot 'ComplianceRecordingBot\publish'
$ZIP       = Join-Path $PSScriptRoot 'bot-publish.zip'

Write-Host "=== Publish (win-x64, framework-dependent) ===" -ForegroundColor Cyan
& "$env:ProgramFiles\dotnet\dotnet.exe" publish $PROJ -c Release -r win-x64 --self-contained false -o $PUB
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

Write-Host "=== Package + upload to blob (2h SAS) ===" -ForegroundColor Cyan
Compress-Archive -Path (Join-Path $PUB '*') -DestinationPath $ZIP -Force
$key = az storage account keys list -g $RG -n $ACCT --query "[0].value" -o tsv
az storage container create --account-name $ACCT --account-key $key --name $CONTAINER --output none
az storage blob upload --account-name $ACCT --account-key $key --container-name $CONTAINER --name bot-publish.zip --file $ZIP --overwrite --output none
$exp = (Get-Date).ToUniversalTime().AddHours(2).ToString('yyyy-MM-ddTHH:mmZ')
$sas = az storage blob generate-sas --account-name $ACCT --account-key $key --container-name $CONTAINER --name bot-publish.zip --permissions r --expiry $exp --https-only -o tsv
$url = "https://$ACCT.blob.core.windows.net/$CONTAINER/bot-publish.zip?$sas"

Write-Host "=== Run VM bootstrap (download, runtime, vc_redist, service) ===" -ForegroundColor Cyan
# Build the VM-side script with the SAS URL injected, write to a temp file, invoke.
$vmScript = @"
`$ErrorActionPreference='Stop'
`$url='$url'; `$botDir='C:\bot'; `$zip='C:\deploy\bot-publish.zip'; `$svc='ComplianceRecordingBot'
if (Get-Service `$svc -EA SilentlyContinue) { Stop-Service `$svc -Force -EA SilentlyContinue; Start-Sleep 3 }
New-Item -ItemType Directory -Force -Path C:\deploy | Out-Null
[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
Invoke-WebRequest -Uri `$url -OutFile `$zip -UseBasicParsing
New-Item -ItemType Directory -Force -Path `$botDir | Out-Null
Expand-Archive -Path `$zip -DestinationPath `$botDir -Force
# ASP.NET Core 8 runtime (machine-wide) if missing
`$dn = Join-Path `$env:ProgramFiles 'dotnet'; `$dnx = Join-Path `$dn 'dotnet.exe'
`$have = (Test-Path `$dnx) -and [bool](& `$dnx --list-runtimes 2>`$null | Select-String 'Microsoft.AspNetCore.App 8\.')
if (-not `$have) { Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile C:\deploy\di.ps1 -UseBasicParsing; & C:\deploy\di.ps1 -Channel 8.0 -Runtime aspnetcore -InstallDir `$dn -NoPath }
# VC++ redist (NativeMedia.dll dependency) if missing
if (-not (Test-Path 'C:\Windows\System32\vcruntime140.dll')) { Invoke-WebRequest 'https://aka.ms/vs/17/release/vc_redist.x64.exe' -OutFile C:\deploy\vc.exe -UseBasicParsing; Start-Process C:\deploy\vc.exe -ArgumentList '/install','/quiet','/norestart' -Wait }
[Environment]::SetEnvironmentVariable('ASPNETCORE_ENVIRONMENT','Production','Machine')
[Environment]::SetEnvironmentVariable('DOTNET_ROOT',`$dn,'Machine')
foreach (`$p in 443,8445) { if (-not (Get-NetFirewallRule -DisplayName "Bot-Inbound-`$p" -EA SilentlyContinue)) { New-NetFirewallRule -DisplayName "Bot-Inbound-`$p" -Direction Inbound -Action Allow -Protocol TCP -LocalPort `$p | Out-Null } }
if (Get-Service `$svc -EA SilentlyContinue) { sc.exe delete `$svc | Out-Null; Start-Sleep 3 }
New-Service -Name `$svc -BinaryPathName (Join-Path `$botDir 'ComplianceRecordingBot.exe') -DisplayName 'Compliance Recording Bot' -StartupType Automatic | Out-Null
Start-Service `$svc; Start-Sleep 15
Write-Output ("SERVICE: " + (Get-Service `$svc).Status)
Get-NetTCPConnection -State Listen -EA SilentlyContinue | Where-Object { `$_.LocalPort -in 443,8445 } | ForEach-Object { Write-Output ("LISTEN " + `$_.LocalPort) }
"@
$tmp = Join-Path $env:TEMP 'vm-deploy-gen.ps1'
Set-Content -Path $tmp -Value $vmScript -Encoding UTF8
az vm run-command invoke -g $RG -n $VM --command-id RunPowerShellScript --scripts "@$tmp" --query "value[].message" -o tsv
Remove-Item $tmp -Force

Write-Host "=== Cleanup deploy blob ===" -ForegroundColor Cyan
az storage blob delete --account-name $ACCT --account-key $key --container-name $CONTAINER --name bot-publish.zip --output none

Write-Host "Done. Verify: curl https://call.sgb-energie.de/health" -ForegroundColor Green
