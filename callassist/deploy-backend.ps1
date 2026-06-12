# ─────────────────────────────────────────────────────────────────────────────
# Deploy app-calltranskript-backend (Linux App Service, Node 20).
#
# CRITICAL: node_modules must be built on LINUX. Some deps (microsoft-cognitive-
# services-speech-sdk, etc.) ship platform-specific native libs — Windows-built
# node_modules crash the Linux container (node exits 1 / 503). So we DON'T ship
# node_modules: we ship the prebuilt (platform-independent) dist + a package.json
# with the build script removed + the lockfile, and let Oryx run `npm install`
# on the Linux server (SCM_DO_BUILD_DURING_DEPLOYMENT=true).
#
# Why dist is prebuilt and the build script is stripped: the repo's build is
# `rimraf ../dist && tsc` which outputs ABOVE wwwroot, so Oryx can't run it in
# place. We compile locally (output is just JS, portable) and let Oryx only install.
#
# NOTE: `az webapp deploy` may report "failed: site failed to start within 10 min"
# when Oryx's npm install runs long — the site usually comes up right after.
# ALWAYS verify with /health rather than trusting the deploy exit code.
#
# Run after: az login. From repo root or anywhere (uses absolute paths).
# ─────────────────────────────────────────────────────────────────────────────
$ErrorActionPreference = 'Continue'
$ca  = 'C:\GIT\TeamsAudioAi\callassist'
$rg  = 'Call_Transkript_Infra'
$app = 'app-calltranskript-backend'

Write-Host "=== build (local tsc -> dist; portable JS) ===" -ForegroundColor Cyan
Push-Location "$ca\app-backend"; npm run build; Pop-Location

Write-Host "=== stage: dist + package.json(no build script) + lockfile (NO node_modules) ===" -ForegroundColor Cyan
$stage = "$ca\deploy-oryx"
if (Test-Path $stage) { [IO.Directory]::Delete($stage, $true) }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
robocopy "$ca\dist" "$stage\dist" /E /NFL /NDL /NJH /NJS /NP | Out-Null
node -e "const fs=require('fs');const p=require('$($ca -replace '\\','/')/app-backend/package.json');delete p.scripts.build;delete p.scripts.dev;delete p.scripts['test:watch'];fs.writeFileSync('$($stage -replace '\\','/')/package.json',JSON.stringify(p,null,2))"
Copy-Item "$ca\app-backend\package-lock.json" "$stage\package-lock.json"

Write-Host "=== zip ===" -ForegroundColor Cyan
$zip = "$ca\backend-oryx.zip"
if (Test-Path $zip) { [IO.File]::Delete($zip) }
Push-Location $stage; tar -a -c -f $zip dist package.json package-lock.json; Pop-Location

Write-Host "=== ensure Oryx server build is ON ===" -ForegroundColor Cyan
az webapp config appsettings set -g $rg -n $app --settings 'SCM_DO_BUILD_DURING_DEPLOYMENT=true' --output none

Write-Host "=== deploy (Oryx installs node_modules on Linux; may 'time out' but still come up) ===" -ForegroundColor Cyan
az webapp deploy --resource-group $rg --name $app --src-path $zip --type zip

Write-Host "=== verify (the source of truth, not the deploy exit code) ===" -ForegroundColor Cyan
Start-Sleep -Seconds 40
try { (Invoke-WebRequest "https://$app.azurewebsites.net/health" -TimeoutSec 30 -UseBasicParsing).Content }
catch { Write-Host "not healthy yet — wait + recheck /health; Oryx build can take >10 min" -ForegroundColor Yellow }
