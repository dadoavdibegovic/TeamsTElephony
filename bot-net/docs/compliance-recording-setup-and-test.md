# Compliance Recording Bot — Setup & Test Manual

How to wire the deployed bot into Microsoft Teams and verify it records a call
end-to-end. Written 2026-06-10.

## 0. What's already done (baseline — no action needed)

- **Bot host:** `vm-bot-calltranskript` (Windows Server 2022, westeurope), running the
  `ComplianceRecordingBot` Windows Service.
- **Endpoint:** `https://call.sgb-energie.de/api/calling` (DNS `call.sgb-energie.de` →
  `20.8.112.131`), TLS via the Sectigo `*.sgb-energie.de` cert. `/health` returns 200.
- **Media:** listening on TCP 8445; `MediaPlatform.Initialize()` succeeds.
- **Azure Bot** `bot-calltranskript-prod`: messaging endpoint + Teams calling webhook
  point to the URL above (`incomingCallRoute = graphPma`).
- **Graph app** `CallTranskript-ComplianceBot` (AppId `7607addb-4830-4a98-be37-97ac0ebe3f8c`):
  application permissions `Calls.AccessMedia.All`, `Calls.JoinGroupCall.All`,
  `Calls.InitiateGroupCall.All` granted with admin consent.
- **Schedule:** VM auto-starts 08:30 and auto-shuts 18:30 Europe/Berlin, Mon–Fri.

## 1. Identifiers you'll reuse

| Name | Value |
|---|---|
| Tenant ID | `d5663c64-53b6-427d-bd45-ad3d3b91764e` |
| Bot App (client) ID | `7607addb-4830-4a98-be37-97ac0ebe3f8c` |
| Bot FQDN / webhook | `https://call.sgb-energie.de/api/calling` |
| Resource group | `Call_Transkript_Infra` |
| VM | `vm-bot-calltranskript` |

## 2. Teams admin setup (one-time) — Teams PowerShell

Run as a Teams Administrator. The application-instance ↔ policy association can take
**up to ~1 hour** to propagate, so do this before you plan to test.

```powershell
# Install once, then connect
Install-Module MicrosoftTeams -Scope CurrentUser   # if not already installed
Connect-MicrosoftTeams

$botAppId = "7607addb-4830-4a98-be37-97ac0ebe3f8c"
$instUpn  = "recordingbot@sgb-energie.de"           # pick a UPN in a verified domain
$policy   = "CallTranskriptRecording"

# 2.1 Create the application instance bound to the bot's AppId
$inst = New-CsOnlineApplicationInstance -UserPrincipalName $instUpn `
          -DisplayName "CallTranskript Recording Bot" -ApplicationId $botAppId
Sync-CsOnlineApplicationInstance -ObjectId $inst.ObjectId   # syncs to Azure AD

# 2.2 Create the compliance recording policy
New-CsTeamsComplianceRecordingPolicy -Identity $policy -Enabled $true `
  -Description "CallTranskript automatic compliance recording"

# 2.3 Attach the application instance to the policy
Set-CsTeamsComplianceRecordingPolicy -Identity $policy `
  -ComplianceRecordingApplications @(
    New-CsTeamsComplianceRecordingApplication -Parent $policy -Id $inst.ObjectId
  )

# 2.4 Assign the policy to a TEST user (start with one)
Grant-CsTeamsComplianceRecordingPolicy -Identity "testuser@sgb-energie.de" -PolicyName $policy
```

Verify it stuck:

```powershell
Get-CsTeamsComplianceRecordingPolicy -Identity $policy            # shows the attached app, State should reach "active"
Get-CsOnlineUser testuser@sgb-energie.de | Select-Object UserPrincipalName, TeamsComplianceRecordingPolicy
```

> Notes
> - The application instance does **not** need a phone number or a Teams license for
>   compliance recording.
> - If `Get-CsTeamsComplianceRecordingPolicy` shows the application `State` as
>   `provisioning`, wait — it must become `active` before recording works.

## 3. Before each test — make sure the VM is up

Outside 08:30–18:30 the VM is deallocated. Start it manually if testing off-hours:

```powershell
az vm start -g Call_Transkript_Infra -n vm-bot-calltranskript
# wait ~60s, then confirm the service is serving:
curl https://call.sgb-energie.de/health        # expect {"status":"healthy",...}
```

## 4. Run the test call

Simplest first test (no PSTN needed):

1. Sign in to Teams as **testuser@sgb-energie.de** (the user with the policy).
2. Place a **Teams call to another user** (or have someone call them).
3. Both participants should see a **recording/compliance banner** in the call.
4. End the call after ~30s of talking.

Follow-up test (production path): place a **PSTN call** that routes through Teams
Direct Routing to that user, to exercise the real trunk path.

## 5. How to observe the bot during/after the test

### 5a. Application Insights (primary — the service logs here)
Portal → `appi-calltranskript` → **Logs**, run:

```kusto
traces
| where timestamp > ago(30m)
| where message has_any ("Incoming call","MediaPlatform","ICommunicationsClient","CallHandler","audio")
| project timestamp, message, severityLevel
| order by timestamp desc
```

Expected sequence on a good call:
- `Incoming call. CallId=...`
- a `CallHandler` answering the call
- media/audio stream messages, then a WebSocket connection to the backend.

Also useful: **Live Metrics** (watch in real time during the call) and
`exceptions | where timestamp > ago(30m)`.

### 5b. The VM (service + listeners + Windows event log)
```powershell
az vm run-command invoke -g Call_Transkript_Infra -n vm-bot-calltranskript `
  --command-id RunPowerShellScript --scripts `
  "Get-Service ComplianceRecordingBot | Select Status; `
   Get-NetTCPConnection -State Listen | ? {$_.LocalPort -in 443,8445} | Select LocalPort,OwningProcess; `
   Get-WinEvent -LogName Application -MaxEvents 20 | ? {$_.TimeCreated -gt (Get-Date).AddMinutes(-15) -and $_.LevelDisplayName -in 'Error','Warning'} | Select TimeCreated,ProviderName,Message | Format-List"
```

### 5c. Backend (did audio arrive?)
The bot forwards audio to `wss://app-calltranskript-backend.azurewebsites.net/bot/audio`.
Check the backend (`app-calltranskript-backend`) logs / App Insights for an inbound
WebSocket connection and audio frames around the call time (see
`callassist/app-backend/src/bot/audioIngestServer.ts`).

## 6. Troubleshooting

| Symptom | Check |
|---|---|
| Call connects but bot never joins / no banner | Policy `State` is `active` (§2 verify); policy actually assigned to the caller; wait for propagation (~1h after assignment). |
| Bot not invited at all | Webhook reachable from internet: `Invoke-WebRequest https://call.sgb-energie.de/api/calling` should complete the TLS handshake (a GET returns 401/405 — that's fine, it means it's live). VM running. |
| Bot joins but no audio recorded | NSG + Windows firewall allow inbound **8445** (both are configured); public IP `20.8.112.131` reachable on 8445; cert valid. Check `exceptions` in App Insights for media errors. |
| `MediaPlatform` / `NativeMedia` errors after a redeploy | VC++ redistributable present on the VM (installed; `deploy-bot-vm.ps1` reinstalls if missing). |
| TLS / cert errors | Cert `*.sgb-energie.de` expires **2026-10-06** — renew before then (see §7). |
| Permission/consent errors in logs | Admin consent on the bot app's Graph permissions (granted; re-consent in Entra portal if changed). |

## 7. Operations

- **Start/stop:** `az vm start|deallocate -g Call_Transkript_Infra -n vm-bot-calltranskript`.
  Auto: start 08:30 / shutdown 18:30 Europe/Berlin Mon–Fri.
- **Redeploy after a code change:** run `bot-net/deploy-bot-vm.ps1` (publishes, ships to
  the VM, restarts the service).
- **Rotate the media/TLS cert (before 2026-10-06):** put the new PFX in KV secret
  `BotMediaCertPfx` (base64) + `BotMediaCertPfxPassword`, then restart the service:
  `az vm run-command invoke -g Call_Transkript_Infra -n vm-bot-calltranskript --command-id RunPowerShellScript --scripts "Restart-Service ComplianceRecordingBot"`.
- **RDP (if needed):** only allowed from the admin IP in the NSG; admin password is in
  KV secret `BotVmAdminPassword` (user `botadmin`).
