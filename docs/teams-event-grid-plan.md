# Plan: Teams routing + Event Grid wiring

Bringing real PSTN calls through the existing Teams Direct Routing into the
CallTranskript pipeline. This document covers the architectural decision,
the work split (what Azure CLI can do vs what needs Teams admin center),
and the recommended sequence.

Status as of 2026-05-18: pipeline code is complete and verified end-to-end
*except* the inbound call source. Today, ACS has no phone numbers and no
Event Grid subscription, so no PSTN call can reach the system regardless
of where it originates.

---

## Current topology

```
[PSTN caller] → [SIP trunk → SBC] → [Teams Direct Routing] → [Teams user/queue]
                                                                   │
                                                                   ✗ no path to ACS today
                                                                   ✗ no Event Grid sub
                                                                   ✗ no ACS phone number
[funcapp-callassist-webhook] ← [Event Grid] ← [acs-calltranskript-prod]
                                                  │
                                                  ↓ (when calls arrive)
                                            answers, streams audio,
                                            transfers to Teams user
                                            (transferhandler.ts)
```

The Azure side (`app-backend`, `func-webhook`, KV, ACS, SignalR, Speech,
OpenAI) is all wired correctly. **Only the inbound delivery is missing.**

---

## Pattern decision

There are two realistic patterns for "PSTN → Teams → ACS":

### Pattern A — ACS-direct PSTN number (simplest, but bypasses Teams)
- Buy a new phone number on the ACS resource (`az communication phonenumber purchase`).
- Publish it as the call-in number. Teams Direct Routing is bypassed for this number.
- `IncomingCall` fires directly on ACS.

Pros: minimum moving parts; no Teams admin work; fastest to get a working demo.
Cons: customers see a *new* number; doesn't reuse existing Teams DID;
double-billed for PSTN minutes (Teams trunk + ACS).
Best for: pilot/test only, not the SGB production path.

### Pattern B — Teams Resource Account → ACS app (Recommended)
- Register the ACS resource as an "application instance" with a calling-enabled
  Teams Resource Account.
- Configure a Call Queue or Auto Attendant in Teams admin center that forwards
  matching PSTN calls to the resource account.
- The resource account routes to the ACS app, which fires `IncomingCall`.
- `transferhandler.ts` can hand the call back to a Teams user (the original
  intended agent) after enrichment/screening.

Pros: keeps existing Teams DIDs; no caller-visible change; ACS becomes a
middleware screening/enrichment layer in front of the existing agents.
Cons: requires Teams admin center work that's outside Azure CLI; takes a
few hours of click-ops + license check.
Best for: SGB's production model.

### Recommendation: **Pattern B**

The whole point of the system is to enrich + transcribe + suggest *for the
existing Teams agent workflow*. Pattern A would force a separate inbound
number, which defeats the purpose. Go Pattern B.

---

## Work split

| Step | Who | Where | Why |
|---|---|---|---|
| 1. Event Grid system topic on ACS | Me | `az eventgrid system-topic create` | Pure Azure, scriptable |
| 2. Event Grid subscription → funcapp | Me | `az eventgrid system-topic event-subscription create` | Pure Azure, scriptable |
| 3. Dead-letter storage container | Me | `az storage container create` | Pure Azure, scriptable |
| 4. Register ACS app in Entra | You | Teams admin / Entra portal | Requires Teams admin role |
| 5. Calling-enabled Resource Account in Teams | You | Teams admin center | Outside Azure |
| 6. Assign Microsoft Teams Phone Resource Account license | You | M365 admin center | License operation |
| 7. Call Queue or Auto Attendant → ACS app | You | Teams admin center | Outside Azure |
| 8. Direct Routing rule for the target DID(s) | You | Teams admin center / SBC | May involve trunk changes |
| 9. End-to-end test call | Both | Real phone + Log Analytics | Validation |

Steps 1-3 I can execute immediately. Steps 4-8 are entirely on the M365/Teams
side and need an admin with the appropriate roles.

---

## Phase 1 — Event Grid wiring (Azure-side, executable today)

### 1a. Create system topic on ACS resource

```sh
az eventgrid system-topic create \
  --name acs-calltranskript-topic \
  --resource-group Call_Transkript_Infra \
  --location global \
  --topic-type Microsoft.Communication.CommunicationServices \
  --source /subscriptions/0c3d1568-2bf4-4eb6-b037-1b94eb8b5061/resourceGroups/Call_Transkript_Infra/providers/Microsoft.Communication/CommunicationServices/acs-calltranskript-prod
```

### 1b. Create dead-letter container

```sh
az storage container create \
  --name eg-deadletter \
  --account-name stcallassist \
  --auth-mode login
```

### 1c. Subscribe `IncomingCall` to funcapp

```sh
az eventgrid system-topic event-subscription create \
  --resource-group Call_Transkript_Infra \
  --system-topic-name acs-calltranskript-topic \
  --name incoming-call-to-funcapp \
  --endpoint https://funcapp-callassist-webhook.azurewebsites.net/api/incoming-call \
  --endpoint-type webhook \
  --included-event-types Microsoft.Communication.IncomingCall \
  --max-delivery-attempts 5 \
  --event-ttl 60 \
  --deadletter-endpoint /subscriptions/0c3d1568-2bf4-4eb6-b037-1b94eb8b5061/resourceGroups/Call_Transkript_Infra/providers/Microsoft.Storage/storageAccounts/stcallassist/blobServices/default/containers/eg-deadletter
```

Behaviour of the existing `func-webhook` already handles:
- The Event Grid `SubscriptionValidation` handshake (returns `validationCode`)
- The `Microsoft.Communication.IncomingCall` event (forwards to backend)

So this subscription will work the moment it's created. No code change needed.

### 1d. Verify

```sh
az eventgrid system-topic event-subscription list \
  --resource-group Call_Transkript_Infra \
  --system-topic-name acs-calltranskript-topic \
  -o table

az eventgrid system-topic event-subscription show \
  --resource-group Call_Transkript_Infra \
  --system-topic-name acs-calltranskript-topic \
  --name incoming-call-to-funcapp \
  --query provisioningState -o tsv
```

Expected: `Succeeded`. Funcapp logs should also show a `SubscriptionValidationEvent`
arriving during creation — confirming the webhook is reachable.

---

## Phase 2 — Teams admin work (manual, you)

### 2a. Register ACS app for Teams calling

ACS has a documented "register Application Instance" flow that creates an
Entra app representing the ACS endpoint to Teams. The ACS app needs the
following Graph permissions (Application):
- `Calls.AccessMedia.All`
- `Calls.JoinGroupCall.All`
- `Calls.InitiateGroupCall.All`

Admin consent required. Reference:
https://learn.microsoft.com/en-us/azure/communication-services/concepts/interop/teams-call-automation

### 2b. Create Resource Account in Teams admin center

In Teams Admin Center → Voice → Resource accounts:
- Create a new Resource Account
- Assign a **Microsoft Teams Phone Resource Account** license (free in most M365 E5)
- Assign a phone number (can be one of the existing Direct Routing DIDs)
- Link to the ACS application instance from step 2a

### 2c. Create Call Queue or Auto Attendant

In Teams Admin Center → Voice → Call queues (or Auto attendants):
- Create a Call Queue
- Add the Resource Account as the recipient
- Configure routing: forward to the ACS app
- Set music-on-hold, agent timeout, overflow rules

Or for a simpler pass-through, an Auto Attendant with a single menu item
"forward to external app" pointing at the ACS Resource Account.

### 2d. Direct Routing target

Ensure the SBC routes the target inbound DID to the Resource Account number
(or to the Call Queue). May require changes on your SBC side or in
"Voice routing policies" in Teams admin center.

---

## Phase 3 — Validation

### 3a. Confirm Event Grid delivery

After Phase 1 + 2 are done, place a test call. Then in Log Analytics:

```kusto
// Funcapp received an IncomingCall event
AppRequests
| where TimeGenerated > ago(15m)
| where AppRoleName == "funcapp-callassist-webhook"
| where Url contains "incoming-call"
| project TimeGenerated, ResultCode, DurationMs, Url
| order by TimeGenerated desc
```

### 3b. Confirm forwarding to backend

```kusto
// app-backend received /calls/incoming
AppRequests
| where TimeGenerated > ago(15m)
| where AppRoleName == "app-calltranskript-backend"
| where Url contains "/calls/incoming"
| project TimeGenerated, ResultCode, DurationMs
```

### 3c. Confirm custom events fired

Use `ops/dashboard.kql` query #2 (success rate) and #4 (enrichment hit rate).
Expect: `call_answered` count > 0 (proves real ACS context was answered),
`hasEntra` or `hasCrm` true if the caller is in your directories.

### 3d. Confirm dead-letter is empty

```sh
az storage blob list \
  --account-name stcallassist \
  --container-name eg-deadletter \
  --auth-mode login \
  --query "[].{name:name,size:properties.contentLength,created:properties.creationTime}" \
  -o table
```

Empty = healthy. If blobs appear, inspect them — they contain the events
that failed all retry attempts.

---

## Risks and gotchas

1. **Event Grid subscription validation handshake** — The funcapp must
   respond to `SubscriptionValidationEvent` with the `validationCode` in
   200 ms. Our code does this; just confirming the funcapp is actually
   running and reachable when we run Phase 1.

2. **ACS app permission consent** — Graph admin consent is required for the
   ACS app's calling permissions. Without it, ACS can't accept calls
   routed from Teams.

3. **Resource Account licensing** — Without the Phone Resource Account
   license assigned, the resource account can't accept calls. Free in
   most M365 E5 plans but must be explicitly assigned.

4. **Direct Routing voice route order** — If the DID matches multiple
   voice routes in Teams admin, the highest-priority one wins. Adding
   a new route for the ACS Resource Account may need to outrank the
   existing user routing.

5. **Media path** — When the call lands on ACS and we call
   `answerCall(... mediaStreamingOptions)`, ACS opens a WebSocket back
   to `wss://<CALLBACK_BASE_URL>/media/<correlationId>`. The current
   `CALLBACK_BASE_URL` is `https://app-calltranskript-backend.azurewebsites.net`
   — confirm App Service WebSocket support is enabled
   (`az webapp config set --web-sockets-enabled true`).

6. **Cost** — ACS PSTN forwarding is billed per minute (separate from
   the Teams trunk). For a pilot, this is minor; for production volume,
   plan capacity.

---

## Suggested sequence

1. **Now** — I execute Phase 1 (Event Grid wiring). Confirms funcapp can
   receive validation events even without real calls. Low risk.

2. **You schedule** — Coordinate Phase 2 with whoever holds Teams admin
   role. Resource Account + ACS app registration is the gate.

3. **Together, off-hours** — Place a test call from a non-production
   number to validate the full chain. Watch the KQL queries in real time.

4. **Iterate** — Tune voice routing, agent transfer targets, suggestion
   prompt for the call types you actually see.

---

## Open questions for you

1. Do you have Teams admin role yourself, or does that go through someone else?
2. Which existing DID should be the first to route through ACS — a low-volume
   test number, or production day 1?
3. ~~WebSocket support — is it already enabled?~~ **Confirmed enabled**
   (`webSocketsEnabled: true`, HTTP/2 on, TLS 1.2+, Always On). No action.
4. Dead-letter storage — `stcallassist` is the existing storage account.
   OK to add an `eg-deadletter` container there, or prefer a separate
   account for audit isolation?
