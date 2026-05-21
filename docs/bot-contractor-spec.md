# Microsoft Teams Compliance Recording Bot — Contractor Specification

**Project:** CallTranskript — extend SGB Energie's call workflow with real-time AI assistance via a Teams Compliance Recording bot.

**Stack:** .NET 8, C#. Bot Framework, Microsoft Graph Communications Calling API, Microsoft.Graph.Communications.Calls.Media.

**Engagement type:** Single deliverable — production-ready .NET microservice that joins Teams calls, captures audio streams, and forwards to an existing Node.js backend.

---

## What you're building

A .NET microservice (the "bot") that:

1. Registers as a Microsoft Teams calling bot
2. Receives Microsoft Graph Communications notifications when a Compliance Recording policy invokes the bot to join a Teams call
3. Joins the call as a silent recording participant (the bot is invisible to caller and agent)
4. Receives raw audio streams (caller + agent, separately) via the `Microsoft.Graph.Communications.Calls.Media` SDK
5. Forwards both audio streams to an existing Node.js backend over a documented WebSocket protocol
6. Cleans up gracefully when the call ends

## Pre-provisioned resources (already done)

You don't need to create these — they exist:

| Resource | Value |
|---|---|
| Azure subscription | `Call_Transkript` (`0c3d1568-2bf4-4eb6-b037-1b94eb8b5061`) |
| Tenant ID | `d5663c64-53b6-427d-bd45-ad3d3b91764e` (SGB Energie GmbH) |
| Bot Entra app ID | `7607addb-4830-4a98-be37-97ac0ebe3f8c` |
| Bot Entra app object ID | `7568c75d-3756-4eca-9755-5261f857d3f3` |
| Bot service principal ID | `daae406e-82a3-455e-bf60-67273ecaf91d` |
| Graph permissions granted | `Calls.AccessMedia.All`, `Calls.JoinGroupCall.All`, `Calls.InitiateGroupCall.All` (Application, admin-consented). `Calls.JoinGroupCallAsGuest.All` is added to the app but not consented — confirm whether you need it; add via portal if so. |
| Azure Bot Service registration | ✅ `bot-calltranskript-prod` (RG `Call_Transkript_Infra`, SKU F0, kind `azurebot`, Teams channel + calling enabled). Calling webhook is a placeholder you replace after deploying your service. |
| Compliance Recording policy in Teams | TBD — applied to test agent before you test (via PowerShell, we'll do this after you confirm endpoint URL) |
| App Service for hosting | You provision (recommend Linux, .NET 8, separate plan from `asp-calltranskript-core`) |

**Client secret** for the bot Entra app: already generated and stored in Key Vault `kv-calltranskript-prod` as secret `BotClientSecret`. Reference it from your App Service config as `@Microsoft.KeyVault(VaultName=kv-calltranskript-prod;SecretName=BotClientSecret)`.

**Backend ingest secret** (the bearer you send when opening the WebSocket to our backend): stored in Key Vault as `BackendIngestSecret`. Same reference syntax.

## Existing Node.js backend (already running)

URL: `https://app-calltranskript-backend.azurewebsites.net`

You forward audio to this. It handles transcription (Azure Speech), AI suggestions (Azure OpenAI GPT-4o), customer enrichment, and pushes results to a React agent UI via Azure SignalR.

You do **not** need to integrate with Speech, OpenAI, SignalR, CRM, or any agent-side concerns. Your only consumer-facing contract is the WebSocket audio forward.

## Protocol you implement: WebSocket audio forward

When the bot joins a call, open a WebSocket to:

```
wss://app-calltranskript-backend.azurewebsites.net/bot/audio/<correlationId>
```

Where `<correlationId>` is a unique identifier per call (suggest: the Teams call's `callId` / `chatInfo.threadId` GUID — must be stable for the call's lifetime).

### First message — call metadata (JSON text frame)

Immediately after the WebSocket opens, send this JSON as a text frame:

```json
{
  "type": "call.started",
  "correlationId": "<callId>",
  "callerPhone": "<E.164 if available, else null>",
  "callerDisplayName": "<from Teams metadata, else null>",
  "agentUpn": "<UPN of the Teams user receiving the call>",
  "startedAt": "<ISO8601>"
}
```

### Audio frames — binary

After the metadata frame, stream audio as binary WebSocket frames. Each frame is:

```
[1 byte speaker tag][N bytes PCM audio]
```

Speaker tag:
- `0x00` = caller (the PSTN-side person)
- `0x01` = agent (the Teams user)

PCM audio (N bytes):
- 16 kHz sample rate
- 16-bit signed little-endian
- Mono
- Frame size: 20-100ms of audio (640 to 3200 bytes). 20ms preferred for lowest latency; larger if your media SDK delivers in larger chunks.

One frame contains audio from one speaker only. If your SDK delivers caller and agent audio in the same callback, split into two frames and send both.

### Closing — call ended

When the bot leaves the call, send a text frame:

```json
{
  "type": "call.ended",
  "correlationId": "<callId>",
  "endedAt": "<ISO8601>",
  "reason": "normal" | "error" | "timeout"
}
```

Then close the WebSocket cleanly.

### Errors

If something goes wrong mid-call (Graph Communications disconnects you, audio decode error, etc.), send:

```json
{
  "type": "call.error",
  "correlationId": "<callId>",
  "message": "<short human-readable>",
  "code": "<your internal code>"
}
```

Close the WebSocket. Our backend will tolerate this and clean up.

## Authentication to our backend

The WebSocket endpoint will validate a **shared-secret bearer token** in the `Authorization` header during the upgrade handshake.

We will set an env var on the .NET bot:

```
BACKEND_INGEST_SECRET=<we will generate and provide>
```

The bot sends:

```
Authorization: Bearer <BACKEND_INGEST_SECRET>
```

Our backend rejects upgrades without a valid bearer.

## Compliance Recording specifics

- The bot must join calls in **passive (silent) mode** — no audio output, no notifications to participants.
- The bot must respect Microsoft's compliance recording requirements: it's invoked by Teams' policy engine when a recorded user joins a call.
- Call recording (saving audio to disk) is **not** your responsibility. Our backend handles persistence if/when needed.
- Your bot only needs to receive and forward live media.

## Performance requirements

- Bot must accept calls within 2 seconds of receiving the Microsoft Graph notification
- Audio forwarding latency (from Graph Communications receive → our backend WebSocket frame) should be ≤ 200ms
- Bot must support at least **20 concurrent calls** per instance
- Bot must scale horizontally via Azure App Service scale-out (no instance-affinity assumptions in your code)

## Deployment target

- Azure App Service Linux, .NET 8 runtime
- We will create the App Service resource; you push the deploy artifact (zip or container) and we manage app settings + KV references for secrets
- Region: West Europe (must match the existing `app-calltranskript-backend` region for low cross-AZ latency)

## Configuration via App Service settings (you read these, we provide values)

| Env var | Value / KV reference |
|---|---|
| `BOT_APP_ID` | `7607addb-4830-4a98-be37-97ac0ebe3f8c` |
| `BOT_TENANT_ID` | `d5663c64-53b6-427d-bd45-ad3d3b91764e` |
| `BOT_CLIENT_SECRET` | `@Microsoft.KeyVault(VaultName=kv-calltranskript-prod;SecretName=BotClientSecret)` |
| `BACKEND_INGEST_WSS` | `wss://app-calltranskript-backend.azurewebsites.net/bot/audio` (the path includes `/{correlationId}` at the end per-connection) |
| `BACKEND_INGEST_SECRET` | `@Microsoft.KeyVault(VaultName=kv-calltranskript-prod;SecretName=BackendIngestSecret)` |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | `@Microsoft.KeyVault(VaultName=kv-calltranskript-prod;SecretName=AppInsightsConnectionString)` |

Once you deploy your bot, give us the public HTTPS URL of your `/api/calling` endpoint and we'll update the Azure Bot Service `bot-calltranskript-prod` with it (replaces the current placeholder).

## Telemetry expectations (Application Insights)

Emit these custom events from the bot, with `correlationId` property on all of them:

| Event | When |
|---|---|
| `bot_call_received` | Graph notification arrives |
| `bot_call_joined` | Bot successfully joined call |
| `bot_call_join_failed` | Join attempt errored — include error code |
| `bot_audio_streaming_started` | First audio frame forwarded to backend |
| `bot_call_left` | Bot left call normally |
| `bot_call_error` | Mid-call error — include error type |

Plus metrics:

| Metric | What it measures |
|---|---|
| `bot_join_latency_ms` | Graph notification arrival → call.started sent to backend |
| `bot_audio_forward_latency_ms` | Per-frame: media SDK receive → ws.send |
| `bot_active_calls` | Gauge of concurrent calls handled |

## Security

- Bot client secret in Key Vault (`kv-calltranskript-prod`), referenced from app settings
- Backend ingest secret in Key Vault, rotatable
- No call audio persisted to disk by the bot
- All outbound traffic over TLS 1.2+
- Bot must validate Microsoft Graph notifications via JWT signature (don't trust raw incoming payloads)

## Deliverables

1. Source code in a private GitHub repo (we'll provide access)
2. Build script (dotnet publish for Linux)
3. Deployment YAML (GitHub Actions for build + zip-deploy)
4. README explaining local dev, configuration, deployment, troubleshooting
5. Test plan demonstrating:
   - Single-call happy path (join, stream, end)
   - Concurrent calls (5 calls overlapping)
   - Graph notification authentication failure handled
   - Backend WebSocket disconnect mid-call handled
   - Call ended unexpectedly (caller hangs up mid-sentence)
6. Operations runbook for common failure modes
7. One round of hand-off + Q&A after deployment

## References

- **Microsoft Graph Calling Bot samples (official):** https://github.com/microsoftgraph/microsoft-graph-comms-samples
- **Compliance Recording bot pattern (look at the "Recording" sample specifically):** https://github.com/microsoftgraph/microsoft-graph-comms-samples/tree/master/Samples/V1.0Samples/LocalMediaSamples/ComplianceRecordingBot
- **Microsoft.Graph.Communications.Calls.Media SDK:** https://learn.microsoft.com/microsoftteams/platform/bots/calls-and-meetings/registering-calling-bot

## Timeline expectation

2-3 weeks of focused work from engagement start to production-ready deliverable. Specifically:

- Week 1: bot framework registration, Graph Communications integration, joining calls, receiving audio
- Week 2: WebSocket forward to our backend, telemetry, error handling, retry logic
- Week 3: testing, documentation, deployment automation, hand-off

## Out of scope

- Speech-to-text (we have it)
- AI suggestions / GPT integration (we have it)
- Agent UI (we have it)
- Customer data enrichment (we have it)
- Call recording persistence (not needed for AI assistance use case)
- Multi-tenant support (single SGB tenant)

## Questions you may have for us

1. **What about recording compliance / storage?** — Not your problem; we don't persist audio. If SGB later wants compliance storage, that's a separate workstream.
2. **Multiple languages?** — Initially German only (de-DE) at the Speech layer. Bot is language-agnostic; just forward bytes.
3. **What about agent transfer / supervision features?** — Out of scope for v1. Bot is observe-only.
4. **Concurrency above 20?** — Plan for horizontal scaling. We can grow App Service instance count.

---

Contact for clarifications during engagement: Adnan Avdibegovic (SGB), via email or shared Slack channel TBD.
