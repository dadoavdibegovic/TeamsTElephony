# Architecture: live caller-ID + transcript delivery to CRM

Target scenario (agreed scope, 2026-06-10):

> Agent has Teams + CRM open in the browser. A call comes in from a number. On
> answer, the **number** is delivered to the CRM, which opens a window for the
> matching customer (subject = caller ID). That window has a pane showing the
> **real-time transcript** of the current call (Copilot-style). The CRM later
> processes the transcript and produces suggestions — **out of scope here**.
> Our scope: deliver **(1) the caller number** and **(2) the live transcript stream**
> to the CRM. Scale: ~10–20 simultaneous calls, ~10 agents.

## Data flow (target)

```
Inbound call ─▶ Teams (Direct Routing) ─▶ agent answers
                      │  (compliance recording policy invites the bot)
                      ▼
   Bot on VM: extracts {callerNumber, agentUPN}, taps per-speaker audio
                      │  WSS  call.started / PCM / call.ended
                      ▼
   Backend: Azure Speech streaming STT (per speaker) ─▶ live transcript segments
                      │
                      ▼  CRM delivery (NEW)
   CRM backend: call.started{callerNumber, agentUPN, callId}  ─▶ open window on THAT
                agent's session, find customer by number; transcript{...} ─▶ fill pane
                      │
                      ▼  (next stage, CRM-side, OUT OF SCOPE)
                   CRM processes transcript ─▶ suggestions
```

## What we already have (✅)

| Capability | Where | Status |
|---|---|---|
| Bot joins Teams/PSTN calls (recording policy) | `bot-net` on VM `vm-bot-calltranskript` | live, verified |
| Extracts **caller number** + **agent UPN** | `CallHandler.cs:349-401` (`callerPhone` from phone identity, `agentUpn`) | ✅ |
| Streams audio + `call.started`/`call.ended` to backend | `BackendWebSocketClient.cs`, `audioIngestServer.ts` | live |
| **Live** per-speaker transcription (interim+final) | `speechTranscriber.ts` (Azure Speech `startContinuousRecognition`) | live |
| Transcript events with speaker + isFinal + timestamp | `audioOrchestrator.ts:107` | live |

So the two things the CRM needs — **the number** and **the live transcript** — are
already produced. They currently go to the local `agent-ui` via SignalR.

## What's missing (the remaining work)

1. **CRM delivery module** (`crmTranscriptPublisher`): emit a 3-event contract to the
   CRM — `call.started{callId, callerId, agentUpn, startedAt}`, `transcript{callId,
   speaker, text, isFinal, timestamp}`, `call.ended{callId, endedAt}`. Small backend module.
2. **The transport + window-open contract with the CRM (Konrad).** This is the only
   cross-team dependency. The CRM must open the window on the **right agent's** browser:
   we supply `agentUpn`; the CRM maps it to that agent's logged-in session, opens the
   window, finds the customer by `callerId`, and binds the transcript by `callId`.
3. *(Secondary)* **Audio→blob recorder** for later analysis (write the PCM stream to
   `stcallassist` as WAV per call). Low priority.

### Transport options (Konrad chooses)
- **A — CRM subscribes to our stream** (WebSocket): we host a per-call read-only WSS;
  the CRM window connects. Lowest latency.
- **B — We POST events to a CRM webhook** (Bearer key, like the ticket API): HTTP-native
  for the CRM team; fully decoupled. **Recommended** — the CRM must already push
  "open window" to the agent's browser over its own realtime channel, so it can relay
  the transcript through that same channel; and it matches the CRM's existing auth style.
  We throttle interim segments.

## Components to REMOVE (cost teardown)

The CRM becomes the agent UI and owns suggestions, so several pieces are now dead weight:

| Component | Why removable | Action | Cost impact |
|---|---|---|---|
| OpenAI suggestion engine (`suggestionEngine.ts`) + `oai-calltranskript-prod` | suggestions are the CRM's job (out of scope) | delete code + resource | stops token spend |
| CRM lookup (`crmEnrichment.ts` lookup-by-phone) | the CRM finds the customer itself from the number | delete code (keep phone normalization) | — |
| `agent-ui` React app | replaced by the CRM window | retire code | none (local app) |
| Azure SignalR `sigr-calltranskript-prod` | only fed `agent-ui`; CRM uses its own realtime; trivial scale | delete after confirming no other consumer | **standing cost saved** |
| ACS `acs-calltranskript-prod` + Function App `funcapp-callassist-webhook` | vestigial ACS Call Automation path — **no code references**; PSTN is via Teams Direct Routing, not ACS | delete **after verifying no active phone numbers/routing** | number/standing cost saved |

**Keep:** bot VM, backend (App Service), Azure Speech, Key Vault, storage (recordings),
App Insights, Azure Bot registration `bot-calltranskript-prod`, the Teams recording policy.

## Scale check (10–20 concurrent, 10 agents)

- **Bot VM** `B4as_v2` (4 vCPU **burstable**, `MediaInstanceCapacity=20`): 20 concurrent
  audio-only media sessions is near the practical ceiling for one burstable 4-vCPU.
  Fine for first tests; for sustained 20-call production, load-test and if CPU-throttled
  move to a non-burstable **D4as_v5** (needs a quota bump) or run **2 bot instances**.
- **Azure Speech**: 2 streams/call × 20 = **40 concurrent** real-time recognitions —
  verify/raise the Speech resource's concurrent-request quota.
- **Backend** (Node on the existing App Service): trivial at this scale.
- **VM schedule**: auto-start 08:30 / shutdown 18:30 Mon–Fri. If calls can arrive outside
  business hours, widen or drop the schedule for production (the VM must be up to record).

## Plan & timeline

| Phase | Work | Effort (our side) | Gated by |
|---|---|---|---|
| 0 | bot + STT pipeline + caller-id/agent capture | **DONE** | — |
| 1 | CRM delivery module (3-event contract) + test on `crm-test` | ~2–3 dev-days | Konrad picks transport + builds CRM window/relay; az re-auth |
| 1-test | 1 agent, 1 call: Teams call → CRM window opens by number → live transcript in pane | days after contract agreed | **CRM-side window (Konrad) is the long pole** |
| 2 | Teardown (OpenAI, SignalR, agent-ui, crmEnrichment, ACS, funcapp) | ~0.5–1 day | your go-ahead; verify ACS numbers |
| 3 | Scale validation (10–20 concurrent): VM CPU + Speech quota; upsize if needed | ~1–2 days | — |
| 4 | *(secondary)* audio→blob recorder | ~1 day | — |

**First test:** achievable within days of (a) agreeing the transport contract and
(b) Konrad's CRM-side window being ready — the CRM side is the pacing item, our side is small.
**Productive:** after the first test passes **and** scale validation (Phase 3) **and** the
CRM window is production-ready.

## To proceed I need
1. **Konrad:** transport (A/B, recommend B) + the `call.started`/`transcript` contract,
   incl. how the CRM opens the window for a given `agentUpn`.
2. **Your go-ahead** on the teardown list (I'll verify ACS phone numbers before deleting).
3. **az re-auth** (token expired) so I can verify ACS/funcapp/SignalR state and execute.
