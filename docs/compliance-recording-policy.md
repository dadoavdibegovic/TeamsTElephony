# Compliance Recording Policy — PowerShell runbook

Commands to register the CallTranskript bot as a compliance recorder in
Microsoft Teams and assign the recording policy to specific users.

Run these **after** the contractor's .NET bot is deployed and reachable
at a public HTTPS URL. The policy creation will validate the bot
endpoint, so don't run it before deployment.

## Prerequisites

- Teams admin role (you have it)
- `MicrosoftTeams` PowerShell module installed (you've used it before)
- The contractor's bot is deployed and the Azure Bot Service
  `bot-calltranskript-prod` has its `callingWebhook` updated from the
  placeholder to the real endpoint (we do this via `az` once the
  contractor confirms their deploy URL)

## 1. Connect

```powershell
Connect-MicrosoftTeams
```

## 2. Create the policy

```powershell
# First create the "Application instance" the policy references.
# This declares OUR bot (by its Entra app ID) as a valid recording app.
$app = New-CsTeamsComplianceRecordingApplication `
  -Identity "CallTranskriptRecording/7607addb-4830-4a98-be37-97ac0ebe3f8c" `
  -Parent "CallTranskriptRecording" `
  -RequiredBeforeMeetingJoin $false `
  -RequiredBeforeCallEstablishment $false `
  -ConcurrentInvitationCount 1

# Then create the policy that bundles those apps.
New-CsTeamsComplianceRecordingPolicy `
  -Identity "CallTranskriptRecording" `
  -Description "Records inbound/outbound calls for CallTranskript AI assistant" `
  -Enabled $true `
  -ComplianceRecordingApplications @($app)
```

### Key parameters explained

| Param | Value | Effect |
|---|---|---|
| `RequiredBeforeMeetingJoin` | `$false` | Bot doesn't have to be in meetings before the user joins |
| `RequiredBeforeCallEstablishment` | `$false` | If the bot fails to join, the call still proceeds (no AI but call isn't blocked). **Strongly recommended `$false` for pilot.** Switch to `$true` later for strict compliance. |
| `ConcurrentInvitationCount` | `1` | How many bot instances Teams will try in parallel if the first one fails. Keep at 1 unless you have multiple recording bots. |
| `Enabled` | `$true` | Policy is active. Set `$false` to disable without deleting. |

## 3. Assign to a test user

Start with a single user (yourself) before rolling out broader:

```powershell
Grant-CsTeamsComplianceRecordingPolicy `
  -PolicyName "CallTranskriptRecording" `
  -Identity "adnan.avdibegovic@sgb-energie.de"
```

Wait ~30-60 minutes for the policy to propagate to that user's Teams
client. Then place a call (from any phone or another Teams user) to
your DID. The bot should automatically be invoked to join the call.

## 4. Verify

Confirm the policy is assigned:

```powershell
Get-CsOnlineUser -Identity "adnan.avdibegovic@sgb-energie.de" `
  | Format-List UserPrincipalName, TeamsComplianceRecordingPolicy
```

You should see `TeamsComplianceRecordingPolicy : CallTranskriptRecording`.

Then call the user. Watch:

- Contractor's bot service logs — should show `IncomingCall` notification
- `app-calltranskript-backend` logs — should show `=== BOT WS CONNECTED ===`
- agent-ui — should flip from Idle to Active, transcript should appear
- App Insights `customEvents` — `bot_call_started`, `audio_orchestrator_started`

## 5. Roll out to more users

Once the test user works, add more agents one at a time:

```powershell
$agents = @(
  "agent1@sgb-energie.de",
  "agent2@sgb-energie.de",
  "agent3@sgb-energie.de"
)

foreach ($agent in $agents) {
  Grant-CsTeamsComplianceRecordingPolicy `
    -PolicyName "CallTranskriptRecording" `
    -Identity $agent
  Write-Host "Assigned to $agent"
}
```

Don't bulk-assign on day one — propagation can take an hour and
debugging is much harder if 20 users are affected simultaneously.

## 6. Rollback

Remove the policy from a user (they go back to normal Teams calling,
no bot):

```powershell
Grant-CsTeamsComplianceRecordingPolicy -PolicyName $null `
  -Identity "adnan.avdibegovic@sgb-energie.de"
```

Disable the policy globally (kept but inactive):

```powershell
Set-CsTeamsComplianceRecordingPolicy -Identity "CallTranskriptRecording" -Enabled $false
```

Delete the policy entirely (after unassigning all users):

```powershell
Remove-CsTeamsComplianceRecordingPolicy -Identity "CallTranskriptRecording"
Remove-CsTeamsComplianceRecordingApplication -Identity "CallTranskriptRecording/7607addb-4830-4a98-be37-97ac0ebe3f8c"
```

## Common gotchas

1. **Propagation lag** — 30-60 minutes after assignment is normal. If
   you assign and immediately test, the bot won't be invoked.

2. **`RequiredBeforeCallEstablishment $true` is dangerous early on** —
   if your bot has any issue (auth failure, crash, network hiccup),
   ALL calls to the assigned user get blocked. Keep it `$false` during
   development and pilot. Only switch when you have observability +
   alerting in place.

3. **Bot endpoint validation** — `New-CsTeamsComplianceRecordingApplication`
   may validate the bot is reachable. Make sure the Azure Bot Service
   `bot-calltranskript-prod` has its real (not placeholder) calling
   webhook configured first.

4. **Bot identity vs policy identity** — the `7607addb-…` in the policy
   is the Entra App ID of the bot. NOT the Azure Bot Service resource
   name, NOT the Application Instance ObjectId. Easy to confuse.

5. **Only inbound calls trigger recording by default** — if you want
   outbound calls recorded too, the bot has to be configured to handle
   that direction. Discuss with the contractor.

## Verification queries (KQL)

After a test call, paste into Log Analytics → `LogAnaliticsWorkspace`:

```kusto
// Bot calls received in the last hour
AppEvents
| where TimeGenerated > ago(1h)
| where Name in ("bot_ws_connected", "bot_call_started", "audio_orchestrator_started")
| project TimeGenerated, Name, Properties=tostring(Properties)
| order by TimeGenerated asc
```

```kusto
// Did anything fail?
AppExceptions
| where TimeGenerated > ago(1h)
| where Properties contains "bot"
| project TimeGenerated, ExceptionType, OuterMessage, Properties=tostring(Properties)
```
