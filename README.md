# CallTranskript — Teams Audio AI

Real-time AI call assistance for SGB Energie call agents. Live transcript,
GPT-4o suggestions, and customer enrichment delivered to a React panel while
the call is in progress.

## Status

**Pre-rebuild.** The Azure-based AI pipeline (Speech transcription,
GPT-4o suggestions, SignalR push, Entra + CRM enrichment, agent UI) is
built and verified working in isolation. The call-source integration is
being rebuilt as a Microsoft Teams Compliance Recording bot after the
original ACS-based approach hit a Microsoft architectural dead end.

See git history (May 2026) for the deleted ACS-specific modules; they're
not needed for the bot path.

## Repository layout

```
callassist/
  app-backend/    Express server: enrichment (Entra + SGB CRM),
                  Speech / OpenAI orchestration, SignalR REST publish.
                  Bot/Graph Communications integration: TODO.
  agent-ui/       Vite + React: live transcript, AI suggestions,
                  caller info card.
  shared/         TypeScript types + phone normalization shared
                  across apps.
ops/
  dashboard.kql   10 KQL queries for Log Analytics ops dashboard.
docs/
  (planning docs for the bot integration go here as work resumes)
```

## Target call flow (bot path, in progress)

```
PSTN caller dials existing SGB DID
   ↓
SIP trunk → Teams Phone (unchanged)
   ↓
Agent's Teams client rings; agent answers
   ↓
Compliance Recording policy triggers our bot to join the call silently
   ↓
Bot receives caller + agent audio streams via Microsoft Graph Communications API
   ↓
Audio fans out to: Azure Speech (de-DE) → live transcript
                  Azure OpenAI (GPT-4o, throttled) → agent suggestions
                  Entra + CRM enrichment (on call start)
   ↓
SignalR push → agent-ui panel (transcript, suggestions, caller info)
```

## Local development

```sh
# install per-app
cd callassist/shared       && npm ci
cd ../app-backend          && npm ci
cd ../agent-ui             && npm ci

# configure env (see .env.example in app-backend)
cp callassist/app-backend/.env.example callassist/app-backend/.env
# fill in real values (or load via Key Vault references in production)

# run
cd callassist/app-backend  && npm run dev
cd callassist/agent-ui     && npm run dev
```

## Testing

```sh
cd callassist/app-backend && npm test         # vitest unit tests
cd callassist/app-backend && npm run typecheck
```

CI (`.github/workflows/ci.yml`) runs typecheck + tests + build for
app-backend and agent-ui.

## Deployment

`app-calltranskript-backend` (Azure App Service) hosts the backend.
Manual zip deploy currently; CI/CD pipeline is open work.

## Infrastructure (Azure)

Subscription `Call_Transkript` (`0c3d1568-…`), tenant SGB Energie GmbH:

- `Call_Transkript_Infra` (West Europe): `app-calltranskript-backend`,
  `kv-calltranskript-prod` (Key Vault, RBAC mode), `sigr-calltranskript-prod`
  (SignalR Serverless mode), `appi-calltranskript` (Application Insights),
  `LogAnaliticsWorkspace`, `stcallassist` (storage).
- `Call_Transkript_AI`: `speech-calltranskript-prod` (Speech S0, West Europe),
  `oai-calltranskript-prod` (OpenAI, Sweden Central).

## Status of components

| Component | Status |
|---|---|
| Azure Speech transcription pipeline | ✅ working |
| Azure OpenAI suggestion engine (German prompt, throttled) | ✅ working |
| Entra ID + SGB CRM enrichment | ✅ working (CRM has 3s timeout intermittently) |
| SignalR push → agent-ui | ✅ working (Serverless mode) |
| Agent UI (caller card, transcript, suggestions, status bar) | ✅ working |
| Application Insights custom telemetry + dashboard queries | ✅ deployed |
| Retry / backoff on transient failures | ✅ implemented |
| Key Vault references for all secrets | ✅ deployed |
| **Call source integration (Compliance Recording bot)** | ⏳ rebuild |
| CI/CD deploy pipeline | ⏳ TODO |
| Load test (20 concurrent calls) | ⏳ TODO |
| Staged production rollout plan | ⏳ TODO |
