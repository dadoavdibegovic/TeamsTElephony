# CallTranskript — Teams Audio AI

Real-time AI call assistance built on Azure Communication Services (ACS).
Inbound PSTN call → ACS answer → audio stream → Azure Speech (de-DE) →
GPT-4o suggestions → SignalR push → React agent UI.

## Repository layout

```
callassist/
  app-backend/    Express server: ACS Call Automation, audio orchestrator,
                  enrichment (Entra + SGB CRM), SignalR REST publish, transfer
  func-webhook/   Azure Functions v4: Event Grid IncomingCall → backend forward
  agent-ui/       Vite + React: live transcript, AI suggestions, caller info
  shared/         TypeScript types and phone normalization shared across apps
```

## Call flow

```
PSTN caller
  → SIP trunk (unchanged)
  → Teams Phone
  → ACS resource
  → Event Grid IncomingCall event
  → func-webhook (validation + forward)
  → app-backend /calls/incoming
  → ACS answer + media streaming start
  → Entra + CRM enrichment (parallel)
  → media WebSocket (audio frames)
  → Azure Speech (interim + final)
  → GPT-4o suggestion (throttled)
  → SignalR push
  → agent-ui (transcript + suggestions + caller card)
```

## Local development

```sh
# 1. install per-app
cd callassist/shared       && npm ci
cd ../app-backend          && npm ci
cd ../func-webhook         && npm ci
cd ../agent-ui             && npm ci

# 2. configure env (see .env.example in each app)
cp callassist/app-backend/.env.example callassist/app-backend/.env
cp callassist/func-webhook/.env.example callassist/func-webhook/.env
# fill in real values

# 3. run
cd callassist/app-backend  && npm run dev
cd callassist/func-webhook && npm run dev      # requires Azure Functions Core Tools
cd callassist/agent-ui     && npm run dev
```

## Testing

```sh
cd callassist/app-backend && npm test         # vitest unit tests
cd callassist/app-backend && npm run typecheck
```

CI (`.github/workflows/ci.yml`) runs typecheck + tests + build for all three apps.

## Deployment

The app-backend runs on Azure App Service (`app-calltranskript-backend`).
func-webhook runs as an Azure Function (Event Grid trigger on the ACS resource).
agent-ui ships as static files (App Service or Static Web Apps).

Current deploy flow is a manual zip upload — automating this is open work (see SPRINTS below).

## What's done / what's next

Phases 1-4 of the original 5-phase plan are essentially complete:

- ✅ Foundation (Azure resources, Entra registration, ACS provisioning) — minus CI/CD now added
- ✅ Call handling core (webhook, answer, transfer, Graph + CRM enrichment)
- ✅ AI pipeline (audio stream, Speech, GPT-4o, SignalR push)
- ✅ Agent UI (caller card, transcript, suggestions, status bar)

Phase 5 (hardening & go-live) work in progress:

- ✅ Retry + backoff on SignalR push and CRM enrichment
- ✅ Custom App Insights telemetry (call_answered, enrichment_completed, suggestion_generated, etc.)
- ✅ Unit tests for pure logic
- ✅ CI workflow
- ⏳ Move secrets to Key Vault references on the deployed App Service
- ⏳ Auth on `/calltranskript/negotiate` (currently anonymous)
- ⏳ Load test with 20 concurrent calls
- ⏳ Real end-to-end call validation
- ⏳ Application Insights dashboards
- ⏳ Staged rollout plan (2 → 10 → 20 agents)
