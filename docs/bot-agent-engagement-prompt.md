# Engagement prompt for the bot agent (Claude #2)

The block below is what Adnan pastes verbatim as the **first message**
to the new Claude terminal. Self-contained: assumes the new agent has
no context from the backend agent's conversation.

Adnan: just copy everything between the `---PROMPT-START---` and
`---PROMPT-END---` markers below into the new terminal.

---PROMPT-START---

You are the **bot agent** for a multi-agent project at SGB Energie GmbH called **CallTranskript**. A second Claude agent (the **backend agent**) has already built a Node.js / Speech / OpenAI / SignalR pipeline that delivers real-time AI assistance to call-center agents during their Teams calls. Your job is to build the **Microsoft Teams Compliance Recording bot** in **.NET 8 / C#** that supplies that pipeline with live call audio.

Both agents run on the same Windows machine, same `az login` session, same git repo. Coordinate via Adnan (the human user) when needed.

## Your first 30 minutes — orient

1. Confirm you can see the repo at `C:\GIT\TeamsAudioAi`. Run `git log --oneline -10` to see recent backend-agent commits.

2. **Read these files in order** before writing any code:
   - `CONTRIBUTING.md` (workspace conventions — strictly observe)
   - `bot-net/README.md` (your workspace)
   - `docs/bot-contractor-spec.md` (the contract — what to build, deliverables, acceptance)
   - `docs/bot-implementation-guide.md` (code-level guidance, NuGet packages, project structure, the WebSocket forward protocol, common pitfalls)
   - `docs/compliance-recording-policy.md` (what Adnan runs after you deploy)
   - `callassist/app-backend/src/bot/audioIngestServer.ts` (the receiver — your WebSocket connects to this; understand the wire format)

3. Confirm the Azure resources mentioned in the spec exist:
   ```sh
   az account show --query name -o tsv
   az ad app show --id 7607addb-4830-4a98-be37-97ac0ebe3f8c --query displayName -o tsv
   az bot show -g Call_Transkript_Infra -n bot-calltranskript-prod --query name -o tsv
   ```

   They should print `Call_Transkript`, `CallTranskript-ComplianceBot`, `bot-calltranskript-prod`. If any fails with auth errors, ask Adnan to run `az login --tenant d5663c64-53b6-427d-bd45-ad3d3b91764e` in any PowerShell window — the token cache is shared.

## Your workspace

Everything you create goes under `bot-net/`. Do NOT modify `callassist/` (that's the backend agent's). Doc additions under `bot-net/docs/` are fine; coordinate before touching existing `docs/*`.

## Your branching

Create and work on branch `bot-net`:

```sh
git checkout -b bot-net
```

Push regularly. When ready for review, open a PR against `master` and ask Adnan to bring me (the backend agent) in to review the integration touchpoints.

## Your definition of done

Per `docs/bot-contractor-spec.md` "Acceptance criteria" section. Headline: a real PSTN call to a Teams user with the recording policy assigned is joined by your bot, audio reaches `/bot/audio/<correlationId>` on the existing backend, agent-ui (already running for the backend agent's tests) shows live transcript + AI suggestions, you handle 5 concurrent calls cleanly.

## What's already done for you

- Bot Entra app `CallTranskript-ComplianceBot` registered, App ID `7607addb-4830-4a98-be37-97ac0ebe3f8c`, 3 calling permissions admin-consented
- Azure Bot Service `bot-calltranskript-prod` registered, Teams channel + calling enabled
- Key Vault `kv-calltranskript-prod` has your secrets:
  - `BotClientSecret` (the Entra app's client secret)
  - `BackendIngestSecret` (the bearer your bot sends to authenticate the WebSocket)
- Node.js audio-ingest endpoint at `wss://app-calltranskript-backend.azurewebsites.net/bot/audio/<correlationId>` — already deployed and waiting for your bot to connect
- App Insights, Log Analytics, Speech, OpenAI, SignalR all provisioned and pay-per-use

## What you must do

- Create the .NET 8 solution under `bot-net/`
- Use Microsoft's official Compliance Recording sample as the starting point (see `bot-implementation-guide.md` for the exact repo path)
- Implement the WebSocket forward to our backend per the protocol in the spec
- Add Application Insights telemetry per the spec (events listed there)
- Provision your own Azure App Service for hosting (suggested name `app-bot-calltranskript`, Linux, P1v3, .NET 8, system MI with `Key Vault Secrets User` on `kv-calltranskript-prod`)
- Wire deploy via GitHub Actions (extend `.github/workflows/ci.yml` — add a `bot-net` job. Use OIDC federated credentials; ask Adnan to set up the federated credential on a new Entra app for GitHub Actions)
- Update Azure Bot Service `bot-calltranskript-prod` calling webhook to your deployed bot URL — Adnan can run this command for you:
  ```sh
  az bot msteams create --resource-group Call_Transkript_Infra --name bot-calltranskript-prod --enable-calling true --calling-web-hook "<your-bot-url>/api/calling"
  ```
- After your bot is deployed and the webhook is updated, Adnan runs the PowerShell in `docs/compliance-recording-policy.md` to assign the recording policy to his user — at which point you test end-to-end.

## Things you'll hit, fair warning

1. `az` token expires every ~9 hours due to SGB's conditional access. When commands suddenly fail with `AADSTS70043`, ask Adnan to `az login` again.
2. `Microsoft.Skype.Bots.Media` (the native media SDK) is Linux x64 / Windows x64 only. On Apple Silicon dev machines this is a pain. You're on Windows so should be fine.
3. The Microsoft Graph Calling notifications endpoint must validate the JWT on every incoming request. Microsoft's sample includes this; don't strip it out.
4. The Compliance Recording sample writes audio to .wav files — **replace that with the WebSocket forward**. That's the key adaptation.
5. The bot's `appsettings.Production.json` must NOT contain secrets — use App Service configuration with KV references (per `docs/bot-contractor-spec.md` config table).

## Communication

- Adnan is the human bridge. Ask him to relay if you need backend-side changes (e.g., "I need the WebSocket protocol extended to support X").
- I (the backend agent) am available via Adnan; if you have integration questions, write them out and Adnan brings them to me.
- Commit early and often. Push to `origin/bot-net`. Your commits are how I see your progress.

Get started by running step 1-3 above and confirming you can read the docs. Tell Adnan when you're done orienting and ready to start implementing.

---PROMPT-END---
