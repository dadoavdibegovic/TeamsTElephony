# ─────────────────────────────────────────────────────────────────────────────
# Business-hours auto-start for the bot VM (companion to the 18:30 auto-shutdown
# set in provision-bot-vm.ps1). Azure Automation runbook on a weekday 08:30 schedule.
#
# Cost: the Automation account free tier covers a daily seconds-long start job.
# Together with auto-shutdown this keeps the VM running ~08:30–18:30 Mon–Fri only.
#
# Run after: az login; VM provisioned. Idempotent-ish (re-creating is safe).
# ─────────────────────────────────────────────────────────────────────────────
$ErrorActionPreference = 'Stop'
$SUB='0c3d1568-2bf4-4eb6-b037-1b94eb8b5061'; $RG='Call_Transkript_Infra'
$AA='aa-bot-calltranskript'; $VM='vm-bot-calltranskript'; $LOC='westeurope'
$API='2023-11-01'

az provider register -n Microsoft.Automation --wait
az extension add --name automation --only-show-errors 2>$null | Out-Null

Write-Host "=== Automation account + system identity ===" -ForegroundColor Cyan
az automation account create -g $RG -n $AA -l $LOC --output none
$aaId="/subscriptions/$SUB/resourceGroups/$RG/providers/Microsoft.Automation/automationAccounts/$AA"
az resource update --ids $aaId --set identity.type=SystemAssigned --output none
$mi = az resource show --ids $aaId --query "identity.principalId" -o tsv

Write-Host "=== grant identity VM start rights ===" -ForegroundColor Cyan
$vmId="/subscriptions/$SUB/resourceGroups/$RG/providers/Microsoft.Compute/virtualMachines/$VM"
az role assignment create --assignee-object-id $mi --assignee-principal-type ServicePrincipal --role "Virtual Machine Contributor" --scope $vmId --output none

Write-Host "=== runbook (Start-AzVM via managed identity) ===" -ForegroundColor Cyan
$rb = Join-Path $env:TEMP 'StartBotVM.ps1'
@"
Disable-AzContextAutosave -Scope Process | Out-Null
Connect-AzAccount -Identity | Out-Null
Start-AzVM -ResourceGroupName '$RG' -Name '$VM'
Write-Output "Start-AzVM issued for $VM"
"@ | Set-Content -Path $rb -Encoding UTF8
az automation runbook create -g $RG --automation-account-name $AA -n StartBotVM --type PowerShell --location $LOC --output none
az automation runbook replace-content -g $RG --automation-account-name $AA -n StartBotVM --content "@$rb" --output none
az automation runbook publish -g $RG --automation-account-name $AA -n StartBotVM --output none
Remove-Item $rb -Force

Write-Host "=== weekday 08:30 schedule (REST: advancedSchedule weekDays) ===" -ForegroundColor Cyan
$base="https://management.azure.com$aaId"
$sb = Join-Path $env:TEMP 'sched.json'
@'
{"name":"weekdays-0830","properties":{"description":"Start bot VM 08:30 Europe/Berlin Mon-Fri.","startTime":"2026-06-11T08:30:00+02:00","frequency":"Week","interval":1,"timeZone":"W. Europe Standard Time","advancedSchedule":{"weekDays":["Monday","Tuesday","Wednesday","Thursday","Friday"]}}}
'@ | Set-Content -Path $sb -Encoding ascii
az rest --method put --uri "$base/schedules/weekdays-0830?api-version=$API" --body "@$sb" --headers "Content-Type=application/json" --output none
Remove-Item $sb -Force

Write-Host "=== link schedule -> runbook ===" -ForegroundColor Cyan
$guid=[guid]::NewGuid().ToString()
$jb = Join-Path $env:TEMP 'js.json'
'{"properties":{"schedule":{"name":"weekdays-0830"},"runbook":{"name":"StartBotVM"}}}' | Set-Content -Path $jb -Encoding ascii
$jsUri="$base/jobSchedules/$guid" + "?api-version=$API"
az rest --method put --uri $jsUri --body "@$jb" --headers "Content-Type=application/json" --output none
Remove-Item $jb -Force

Write-Host "Done. Auto-start: weekdays 08:30 Europe/Berlin. Pairs with 18:30 auto-shutdown." -ForegroundColor Green
Write-Host "Test now: az automation runbook start -g $RG --automation-account-name $AA -n StartBotVM" -ForegroundColor Yellow
