# Live test runbook — Adnan calls +49 8142 4102506

Concrete, copy-paste runbook for the first end-to-end PSTN test of the
CallTranskript pipeline. Uses specific test values so it can be replayed.

## The trip your call will take

```
Adnan's mobile (+49 151 424 515 74)
   │  dials
   ▼
+49 8142 4102506
   │  ← Teams Direct Routing (SBC routes inbound DID)
   ▼
Teams Resource Account "CallTranskript Routing"
   │  ← backed by ACS Application Instance (you create in step 1)
   ▼
Teams Auto Attendant rule "Redirect → External application"
   │
   ▼
ACS resource acs-calltranskript-prod (immutable id 3c686b4b-…)
   │  ← fires Microsoft.Communication.IncomingCall event
   ▼
Event Grid → funcapp-callassist-webhook  ✅ already wired
   │
   ▼
app-backend POST /calls/incoming  ✅ already wired
   │
   ├─→ ACS answer + open WebSocket back for audio
   ├─→ Entra Graph lookup by phone number
   └─→ CRM lookup by phone number  ⚠ currently times out
   │
   ▼
Azure Speech (de-DE) transcribes the audio stream live
   │
   ▼
GPT-4o suggestionEngine generates German agent suggestions
   │
   ▼
Azure SignalR pushes events to agent-ui
   │
   ▼
agent-ui (running on your laptop) shows caller card + live transcript + AI suggestions
```

The right side of the diagram (everything after ACS) is already
working and tested. The left side (everything before ACS) is what needs
wiring up now.

---

## What you do, in order

### Step 1 — Teams PowerShell: register ACS as an app instance

Open PowerShell **with Teams admin rights** and paste:

```powershell
# One-time module install (skip if already installed)
Install-Module MicrosoftTeams -Force -AllowClobber

# Sign in as Teams admin
Connect-MicrosoftTeams

# Create the Application Instance pointing at your ACS resource
$inst = New-CsOnlineApplicationInstance `
  -UserPrincipalName "acs-calltranskript@sgb-energie.de" `
  -ApplicationId "3c686b4b-6aa0-4255-9799-8533e59605f3" `
  -DisplayName "CallTranskript ACS"

# Sync to make it visible to Teams.
# NOTE: -ApplicationId is required as of MicrosoftTeams module ~6.x.
# Older docs show only -ObjectId, which now errors with
# "At least one of ApplicationId or AcsResourceId must be provided."
Sync-CsOnlineApplicationInstance `
  -ObjectId $inst.ObjectId `
  -ApplicationId "3c686b4b-6aa0-4255-9799-8533e59605f3"

# Note this ObjectId — you'll need it later
$inst.ObjectId
```

Wait ~15 minutes after this for Microsoft 365 replication.

### Step 2 — admin.teams.microsoft.com → Voice → Resource accounts

- Click **+ Add**
- **Display name**: `CallTranskript Routing`
- **Username**: `acs-calltranskript@sgb-energie.de`  *(must match the UPN from step 1)*
- **Resource account type**: `Auto attendant`
- Save

### Step 3 — admin.microsoft.com → Users → Active users

- Find `acs-calltranskript@sgb-energie.de` (it now exists as a user)
- Open → **Licenses and apps**
- Check **Microsoft Teams Phone Resource Account**
- Save. Wait ~10 minutes for the license to activate.

### Step 4 — admin.teams.microsoft.com → Voice → Resource accounts → CallTranskript Routing

- Click **Assign / unassign**
- **Phone number type**: `Direct Routing`
- **Assigned phone number**: `+49 8142 4102506`
- Save

If Teams says "this number is already assigned elsewhere," the DID
currently routes to a Teams user or queue. Unassign it from there
first, or pick a different test DID.

### Step 5 — admin.teams.microsoft.com → Voice → Auto attendants → + Add

- **Name**: `CallTranskript Routing`
- **Default language**: German (de-DE)
- **Operator**: skip
- **Resource accounts**: add `CallTranskript Routing`
- **Call flow**:
  - **Greeting**: leave empty or "Connecting your call…" TTS
  - **First action**: Redirect call
  - **Redirect target**: **External application**
  - **Search**: select **CallTranskript ACS** (the application instance from step 1)
- Save

### Step 6 — verify the SBC voice route

The DID `+49 8142 4102506` must reach the Teams Resource Account from
your SBC. Two checks in Teams admin center:

- **Voice → Direct Routing → SBCs**: confirm the SBC is *Active*
- **Voice → Phone numbers**: search `+49 8142 4102506`, confirm
  "Assigned to: CallTranskript Routing"

If your SBC was previously delivering this DID to a different Teams
user, the reassignment in step 4 should automatically steal it — no SBC
change needed.

### Step 7 — Wait

**30–60 minutes** of doing nothing. Microsoft 365 propagation is slow
and not instantaneous. Calls placed before propagation completes will
often just ring and drop with no trace anywhere — frustrating but normal.

### Step 8 — CRM side: confirm Adnan exists

At https://crm.sgb-energie.de search for Adnan or `+49 151 424 515 74`.
Confirm the partner record has that phone in the TELEFON or MOBIL
field. The SGB CRM normalizes phone format internally (per the comment
in `crmEnrichment.ts`), so any form works.

> ⚠ **Known issue:** CRM lookup from the deployed App Service is
> currently timing out at 3s on every call. Until that's resolved,
> the CallerCard will show "No CRM data" even though Adnan's record
> exists. Entra won't have him either (he's not in SGB's Entra users).
> So you'll likely see only the phone number and the caller display
> name in the test — the partner number + account fields will be empty.
> The call itself will work end-to-end; only the CRM enrichment panel
> will miss.

### Step 9 — Run agent-ui locally so you can see the live call

This is where you watch the test happen. Open a regular terminal:

```powershell
cd C:\GIT\TeamsAudioAi\callassist\agent-ui

# Create the env file pointing at the deployed backend
@"
VITE_BACKEND_URL=https://app-calltranskript-backend.azurewebsites.net
VITE_SIGNALR_HUB=calltranskript
"@ | Set-Content .env.local -Encoding utf8

npm install   # if not already done
npm run dev
```

Open the printed URL (usually http://localhost:5173) in a browser.

You should see:

- **CallStatusBar** at top: "Idle" badge, no phone number,
  "Disconnected" → "Connecting" → **"Connected"** (green) within 5-10s
- **CallerCard** left panel: "No active call"
- **SuggestionPanel** and **TranscriptPanel** right: "No suggestions
  yet" and "Waiting for speech…"

If the connection state never reaches Connected, the agent-ui can't
reach the SignalR negotiate endpoint — usually a corporate firewall
or CORS issue. The browser console will tell you which.

### Step 10 — Place the test call

Adnan dials `+49 8142 4102506` from his mobile. He should hear silence
or the brief greeting, then nothing — the call is connected but his
audio is being captured for transcription, not played to a human.

Within a couple of seconds, in the agent-ui:

- **CallStatusBar** flips to **"Active"** (blue pill), shows
  `+49 151 424 515 74`, live timer starts.
- **CallerCard** shows the phone number and `displayName` from Teams
  (likely "Adnan" if his outbound caller ID is set).
  - If/when CRM enrichment works again: partner_number, account name,
    address populate here.
- **TranscriptPanel** shows a green **LIVE** dot, starts streaming
  interim transcript chunks (italic gray) that finalize into solid
  white lines as Speech detects sentence breaks.
- **SuggestionPanel** starts pulsing with GPT-4o-generated German
  suggestions, one every 3-5 seconds of meaningful speech.

Have Adnan talk for a while — say his name, describe a fake issue,
ask a question. Speech transcription needs continuous audio.

### Step 11 — End the call

Adnan hangs up. agent-ui flips to **"Ended"**. Audio streaming stops,
suggestion engine stops, callStore retains the entry 5 minutes for
post-call inspection then auto-deletes.

---

## Where you'll see the transcript, and when

| Surface | Latency | Content |
|---|---|---|
| agent-ui TranscriptPanel | interim ~500ms; final ~1-2s after Adnan pauses | Italic gray interim lines that solidify into final lines as Speech detects sentence breaks |
| agent-ui SuggestionPanel | ~3-5s after meaningful new speech accumulates | Pulse animation on new suggestion; copy button to grab text |
| App Insights → AppEvents | 60-90s after the call | `call_incoming`, `enrichment_completed`, `call_answered`, `suggestion_generated`, `call_ended` events with full Properties |
| App Insights → AppServiceConsoleLogs | 30-60s after the call | Actual stdout from the backend, including `=== handleIncomingCall START ===`, `CRM lookup …`, transcription events |

The **agent-ui is the live view**. Log Analytics is the **post-mortem**
— useful if something didn't appear in the UI and you need to see
whether the event fired but didn't push, vs never fired at all.

---

## What if the call doesn't reach the pipeline?

If you place the call and nothing appears in the agent-ui after 15s,
the call isn't getting past Teams. Diagnostic order:

1. **App Insights** — paste this immediately after hanging up:
   ```kusto
   AppEvents
   | where TimeGenerated > ago(5m)
   | where Name == "call_incoming"
   ```
   - Empty → Teams never routed it to ACS. Problem is in steps 1-6.
   - Present → ACS got the call. Look at later events to see where it died.

2. **Event Grid dead-letter** — check if EG couldn't deliver to funcapp:
   ```sh
   az storage blob list \
     --account-name stcallassist \
     --container-name eg-deadletter \
     --auth-mode login \
     --query "length(@)" -o tsv
   ```
   Should be 0. If >0, fetch the blob to see the failed event payload.

3. **Teams call history** — admin.teams.microsoft.com → Users → find
   Adnan's user or the resource account → call history. Shows how
   Teams routed the call, with timestamps and outcome codes.

---

## Quick-reference values

| What | Value |
|---|---|
| ACS immutable resource ID | `3c686b4b-6aa0-4255-9799-8533e59605f3` |
| Tenant ID | `d5663c64-53b6-427d-bd45-ad3d3b91764e` |
| Subscription ID | `0c3d1568-2bf4-4eb6-b037-1b94eb8b5061` |
| Resource Account UPN to create | `acs-calltranskript@sgb-energie.de` |
| Test DID to assign | `+49 8142 4102506` |
| Expected caller (Adnan) | `+49 151 424 515 74` |
| Backend URL (for agent-ui .env.local) | `https://app-calltranskript-backend.azurewebsites.net` |
| SignalR hub name | `calltranskript` |
| Event Grid subscription | `acs-calltranskript-topic / incoming-call-to-funcapp` ✅ already created |
| Log Analytics workspace | `LogAnaliticsWorkspace` (Call_Transkript_Infra) |
| Ops dashboard queries | `ops/dashboard.kql` |
