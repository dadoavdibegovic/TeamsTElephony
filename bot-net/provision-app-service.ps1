# Bot App Service provisioning script — Session 3
# Run this after: az login --tenant d5663c64-53b6-427d-bd45-ad3d3b91764e
# Requires: az CLI, subscription Call_Transkript active

$RG        = "Call_Transkript_Infra"
$PLAN      = "asp-bot-calltranskript-core"
$APP       = "app-bot-calltranskript"
$LOCATION  = "westeurope"
$KV        = "kv-calltranskript-prod"
$SUB       = "0c3d1568-2bf4-4eb6-b037-1b94eb8b5061"

Write-Host "=== Phase 1: Create App Service Plan ===" -ForegroundColor Cyan
az appservice plan create `
  --name $PLAN `
  --resource-group $RG `
  --location $LOCATION `
  --sku P1V3 `
  --is-linux

Write-Host "=== Phase 2: Create Web App ===" -ForegroundColor Cyan
az webapp create `
  --name $APP `
  --resource-group $RG `
  --plan $PLAN `
  --runtime "DOTNETCORE:8.0"

Write-Host "=== Phase 3: Web App configuration ===" -ForegroundColor Cyan
az webapp config set `
  --name $APP `
  --resource-group $RG `
  --always-on true `
  --web-sockets-enabled true `
  --http20-enabled true `
  --min-tls-version "1.2" `
  --ftps-state Disabled

Write-Host "=== Phase 4: Enable system-assigned managed identity ===" -ForegroundColor Cyan
$IDENTITY = az webapp identity assign `
  --name $APP `
  --resource-group $RG `
  --query principalId `
  --output tsv

Write-Host "MI principal ID: $IDENTITY" -ForegroundColor Yellow

Write-Host "=== Phase 5: Grant MI Key Vault Secrets User on KV ===" -ForegroundColor Cyan
$KV_SCOPE = "/subscriptions/$SUB/resourceGroups/$RG/providers/Microsoft.KeyVault/vaults/$KV"
az role assignment create `
  --assignee-object-id $IDENTITY `
  --assignee-principal-type ServicePrincipal `
  --role "Key Vault Secrets User" `
  --scope $KV_SCOPE

Write-Host "=== Phase 6: Set App Service settings ===" -ForegroundColor Cyan
az webapp config appsettings set `
  --name $APP `
  --resource-group $RG `
  --settings `
    "Bot__AppId=7607addb-4830-4a98-be37-97ac0ebe3f8c" `
    "Bot__TenantId=d5663c64-53b6-427d-bd45-ad3d3b91764e" `
    "Bot__ClientSecret=@Microsoft.KeyVault(VaultName=$KV;SecretName=BotClientSecret)" `
    "Bot__ServiceCname=$APP.azurewebsites.net" `
    "Bot__CallingWebHookEndpoint=https://$APP.azurewebsites.net/api/calling" `
    "Backend__IngestWss=wss://app-calltranskript-backend.azurewebsites.net/bot/audio" `
    "Backend__IngestSecret=@Microsoft.KeyVault(VaultName=$KV;SecretName=BackendIngestSecret)" `
    "APPLICATIONINSIGHTS_CONNECTION_STRING=@Microsoft.KeyVault(VaultName=$KV;SecretName=AppInsightsConnectionString)" `
    "ASPNETCORE_ENVIRONMENT=Production" `
    "WEBSITES_PORT=9442"

Write-Host "=== Phase 7: Set health check path ===" -ForegroundColor Cyan
az webapp config set `
  --name $APP `
  --resource-group $RG `
  --generic-configurations '{"healthCheckPath": "/health"}'

Write-Host "=== Phase 8: Publish and deploy ===" -ForegroundColor Cyan
$PUBLISH_DIR = "C:\GIT\TeamsAudioAi\bot-net\ComplianceRecordingBot\publish"
$ZIP_PATH    = "C:\GIT\TeamsAudioAi\bot-net\bot-publish.zip"

& "C:\Program Files\dotnet\dotnet.exe" publish `
  "C:\GIT\TeamsAudioAi\bot-net\ComplianceRecordingBot\ComplianceRecordingBot.csproj" `
  -c Release `
  -r linux-x64 `
  --self-contained false `
  -o $PUBLISH_DIR

if (Test-Path $ZIP_PATH) { Remove-Item $ZIP_PATH }
Compress-Archive -Path "$PUBLISH_DIR\*" -DestinationPath $ZIP_PATH

az webapp deploy `
  --name $APP `
  --resource-group $RG `
  --src-path $ZIP_PATH `
  --type zip

Write-Host "=== Phase 9: Verify health endpoint ===" -ForegroundColor Cyan
Start-Sleep -Seconds 30
$response = Invoke-RestMethod -Uri "https://$APP.azurewebsites.net/health" -Method Get
Write-Host "Health response: $($response | ConvertTo-Json)" -ForegroundColor Green

Write-Host "=== Done! ===" -ForegroundColor Green
Write-Host "App URL: https://$APP.azurewebsites.net" -ForegroundColor Green
Write-Host "Health: https://$APP.azurewebsites.net/health" -ForegroundColor Green
Write-Host "Calling webhook: https://$APP.azurewebsites.net/api/calling" -ForegroundColor Green
Write-Host ""
Write-Host "NEXT: Run the following to update Azure Bot Service webhook:" -ForegroundColor Yellow
Write-Host "  az bot msteams create --resource-group $RG --name bot-calltranskript-prod --enable-calling true --calling-web-hook https://$APP.azurewebsites.net/api/calling" -ForegroundColor Yellow
