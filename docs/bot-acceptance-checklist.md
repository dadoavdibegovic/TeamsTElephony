# Bot agent acceptance checklist

What the backend agent (or Adnan, or both) verify before declaring the
.NET bot ready for production. Pull-through of acceptance criteria from
`bot-contractor-spec.md`, expressed as concrete checks.

Run these in order. Don't proceed to later sections if earlier ones fail.

## 1. Code review (before any deploy)

- [ ] Repo `bot-net/` has a .NET 8 solution that builds cleanly with `dotnet build`
- [ ] The solution uses the Microsoft Compliance Recording sample as a starting point — recognizable patterns (`CallHandler`, `ComplianceRecordingBot`, `BotMediaStream`)
- [ ] `BackendWebSocketClient` (or equivalent) exists and implements our protocol:
  - sends `call.started` text frame first
  - sends `[speaker_tag][PCM]` binary frames (0x00 caller, 0x01 agent)
  - sends `call.ended` on cleanup
  - includes `Authorization: Bearer <BACKEND_INGEST_SECRET>` on the upgrade
- [ ] No secrets in source code or `appsettings.json` — only env-var references / KV reference syntax
- [ ] `Microsoft.Skype.Bots.Media` is referenced (native media SDK is the only supported path for real-time audio access)
- [ ] Application Insights wired with `AddApplicationInsightsTelemetry()` and at least the events listed in the spec
- [ ] JWT validation on incoming Graph notifications is present (not stripped from the sample)

## 2. App Service deployment

- [ ] `app-bot-calltranskript` (or chosen name) exists in `Call_Transkript_Infra`, Linux, .NET 8 runtime
- [ ] **Always On** enabled, **WebSockets** enabled, **HTTP/2** enabled, **TLS 1.2+** enforced
- [ ] System-assigned managed identity enabled
- [ ] MI granted `Key Vault Secrets User` on `kv-calltranskript-prod`
- [ ] App settings include KV references for `BOT_CLIENT_SECRET` and `BACKEND_INGEST_SECRET`
- [ ] Bot starts cleanly: `curl https://<bot-url>/health` returns 200
- [ ] Bot logs show no startup errors in Log Analytics → `AppServiceConsoleLogs`

## 3. Azure Bot Service registration update

- [ ] `bot-calltranskript-prod` calling webhook updated from placeholder to real bot URL:
  ```sh
  az bot msteams show -g Call_Transkript_Infra -n bot-calltranskript-prod --query "properties.properties.callingWebhook"
  ```
  Should match the deployed bot's `/api/calling` endpoint.

## 4. Compliance Recording Policy (Adnan does this via PowerShell)

- [ ] Policy `CallTranskriptRecording` created (`Get-CsTeamsComplianceRecordingPolicy`)
- [ ] Policy contains application instance with App ID `7607addb-4830-4a98-be37-97ac0ebe3f8c`
- [ ] `RequiredBeforeCallEstablishment` is `$false` for initial testing
- [ ] Policy assigned to Adnan's test user
- [ ] 30+ minutes elapsed since assignment (propagation)

## 5. First live test call — single call

- [ ] Adnan places test call to himself (or has someone call him)
- [ ] Within 2s of call connecting, bot logs show:
  - Graph notification received
  - Call joined successfully
  - `BackendWebSocketClient` connected
  - `call.started` frame sent
- [ ] Backend (`app-calltranskript-backend`) logs show `=== BOT WS CONNECTED ===`
- [ ] Within ~3s of speech, agent-ui (running locally on `http://localhost:5173`) shows:
  - Phase pill flipped to "Active"
  - Caller phone number in status bar
  - Live transcript scrolling
- [ ] Within ~10s of meaningful speech, agent-ui shows AI suggestions pulsing
- [ ] Call ends cleanly — both sides log graceful WebSocket close
- [ ] App Insights `customEvents`:
  - `bot_call_received`, `bot_call_joined`, `bot_audio_first_frame_sent`, `bot_call_left` all present with the same correlationId
  - `audio_orchestrator_started`, `audio_orchestrator_stopped` present
  - No `bot_call_error` or `bot_call_join_failed`

## 6. Concurrent calls test — 5 simultaneous

- [ ] 5 callers dial test users assigned the policy within 30s of each other
- [ ] Bot joins all 5 within 2s each (no queuing)
- [ ] All 5 transcripts visible (if 5 agent-ui instances are open) or audit via App Insights
- [ ] Bot's `bot_active_calls` metric peaks at 5
- [ ] No errors in App Insights during the test window
- [ ] All 5 calls end cleanly; `bot_active_calls` returns to 0

## 7. Failure modes

- [ ] **Backend down**: stop `app-calltranskript-backend`, place a call. Bot should log WS connect failure; the call itself should still proceed (caller talks to agent normally, just no AI). Restart backend; subsequent calls work again.
- [ ] **Bad bearer**: temporarily change `BackendIngestSecret` in KV, restart bot. Place call. Bot logs 401 on WS upgrade; metric `bot_backend_ws_failed` increments. Restore secret; subsequent calls work.
- [ ] **Mid-call disconnect**: stop the bot during an active call. Backend logs WS close. Existing transcripts already in agent-ui persist; new transcripts stop. Caller and agent unaffected (Teams call continues normally because `RequiredBeforeCallEstablishment` is `$false`).

## 8. Documentation

- [ ] `bot-net/README.md` has updated content (no longer the stub)
- [ ] Build + deploy commands documented
- [ ] Local dev setup (DevTunnels) documented
- [ ] Operations runbook for the 3 failure modes above
- [ ] One operations diagram (text-based ASCII is fine) showing the bot's place in the system

## 9. Operations handoff

- [ ] CI/CD: GitHub Actions deploy job runs on push to `bot-net` branch, deploys to App Service
- [ ] CI uses OIDC federated identity (no stored secrets)
- [ ] One Q&A session with backend agent and Adnan (~1h) covering troubleshooting

## When all of the above are ✓

Bot is accepted. Merge `bot-net` branch to `master`. Adnan rolls out
the Compliance Recording Policy to additional agents per the runbook.
