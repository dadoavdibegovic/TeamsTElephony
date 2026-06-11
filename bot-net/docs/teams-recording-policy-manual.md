# Teams Compliance Recording Policy — setup manual

This is what makes inbound Teams/PSTN calls actually reach the bot. The bot is
authorized to join a user's calls via a **Teams compliance recording policy** that
points at the bot's application instance. Without this, no call audio ever arrives.

Run as a **Teams Administrator** in Teams PowerShell. Allow **up to ~1 hour** for the
application↔policy association to become `active`.

## Identifiers
| | |
|---|---|
| Tenant ID | `d5663c64-53b6-427d-bd45-ad3d3b91764e` |
| Bot App (client) ID | `7607addb-4830-4a98-be37-97ac0ebe3f8c` |
| Bot calling webhook (already set on `bot-calltranskript-prod`) | `https://call.sgb-energie.de/api/calling` |

## 0. Connect
```powershell
Install-Module MicrosoftTeams -Scope CurrentUser    # first time only
Connect-MicrosoftTeams                              # sign in as Teams admin
```

## 1. Create the application instance (bind it to the bot's AppId)
```powershell
$botAppId = "7607addb-4830-4a98-be37-97ac0ebe3f8c"
$instUpn  = "recordingbot@sgb-energie.de"   # any UPN in a verified domain; no license/number needed

$inst = New-CsOnlineApplicationInstance -UserPrincipalName $instUpn `
          -DisplayName "CallTranskript Recording Bot" -ApplicationId $botAppId
Sync-CsOnlineApplicationInstance -ObjectId $inst.ObjectId   # push it to Azure AD
$inst.ObjectId    # note this — used in step 3
```

## 2. Create the compliance recording policy
```powershell
$policy = "CallTranskriptRecording"
New-CsTeamsComplianceRecordingPolicy -Identity $policy -Enabled $true `
  -Description "CallTranskript automatic compliance recording"
```

## 3. Attach the bot's application instance to the policy
```powershell
Set-CsTeamsComplianceRecordingPolicy -Identity $policy `
  -ComplianceRecordingApplications @(
    New-CsTeamsComplianceRecordingApplication -Parent $policy -Id $inst.ObjectId `
      -RequiredBeforeCallEstablishment $false
  )
```
> `-RequiredBeforeCallEstablishment $false` = if the bot is ever unreachable, the
> user's call still connects normally (just isn't recorded). Set `$true` only if a
> call must be **blocked** when recording can't start.

## 4. Assign the policy to a user (start with ONE test user)
```powershell
Grant-CsTeamsComplianceRecordingPolicy -Identity "testuser@sgb-energie.de" -PolicyName $policy
```
Roll out to the other agents only after the test call works.

## 5. Verify
```powershell
# The attached app must reach State = active (not "provisioning") before it works:
Get-CsTeamsComplianceRecordingPolicy -Identity $policy |
  Select-Object Identity -ExpandProperty ComplianceRecordingApplications

# Confirm the user has the policy:
Get-CsOnlineUser testuser@sgb-energie.de |
  Select-Object UserPrincipalName, TeamsComplianceRecordingPolicy
```

## How it fits together
A user with the policy makes/receives a call → Teams invites the bot's application
instance → Microsoft POSTs the incoming-call notification to the bot's calling webhook
(`https://call.sgb-energie.de/api/calling`) → the bot answers, taps the audio, and
streams the transcript to the CRM.

## Troubleshooting
| Symptom | Check |
|---|---|
| Call connects but bot never joins / no recording banner | App `State` is `active` (step 5); the policy is actually assigned; wait for propagation (~1h after assignment/sync). |
| App stuck at `State = provisioning` | Give it time; re-run `Sync-CsOnlineApplicationInstance -ObjectId $inst.ObjectId`; confirm the AppId matches the bot. |
| Nothing reaches the bot at all | VM running + `https://call.sgb-energie.de/health` returns 200; the bot app's Graph permissions (`Calls.AccessMedia.All`, `Calls.JoinGroupCall.All`) have admin consent (they do). |
| Need to remove/rotate | `Grant-CsTeamsComplianceRecordingPolicy -Identity <user> -PolicyName $null` to unassign; `Remove-CsTeamsComplianceRecordingPolicy -Identity $policy` to delete. |

## Prerequisites already in place (no action)
- Azure Bot `bot-calltranskript-prod`: calling enabled, webhook → `https://call.sgb-energie.de/api/calling`.
- Bot app Graph permissions consented: `Calls.AccessMedia.All`, `Calls.JoinGroupCall.All`, `Calls.InitiateGroupCall.All`.
- Bot running on the VM (HTTPS 443 + media 8445).
