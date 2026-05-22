# bot-net — .NET 8 Compliance Recording bot

This directory is owned by the bot agent (a Claude agent in a separate
terminal). It will contain the .NET 8 Compliance Recording bot that
joins Microsoft Teams calls and forwards audio to the existing
Node.js backend at `../callassist/app-backend`.

## Status: greenfield

Nothing built yet. Use this directory for:

- The .NET solution (`*.sln`) and project files
- Source code under conventional .NET layout
- `appsettings.json` templates
- Local dev / DevTunnels scripts
- Deploy artifact build script

## Required reading before starting

In this order:

1. [`../docs/bot-contractor-spec.md`](../docs/bot-contractor-spec.md) — **what to build** (the contract)
2. [`../docs/bot-implementation-guide.md`](../docs/bot-implementation-guide.md) — **how to build it** (code-level guidance)
3. [`../docs/compliance-recording-policy.md`](../docs/compliance-recording-policy.md) — what happens after you deploy
4. [`../CONTRIBUTING.md`](../CONTRIBUTING.md) — multi-agent workspace conventions
5. [`../callassist/app-backend/src/bot/audioIngestServer.ts`](../callassist/app-backend/src/bot/audioIngestServer.ts) — the receiver-side of the WebSocket protocol you implement

## Workspace boundaries

- You own everything under `bot-net/`
- You can read everything in the repo
- You can `az`-create your own App Service (`bot-net-prod` or similar)
- You **don't** modify `callassist/` without coordinating via Adnan
- You **don't** modify shared docs (`docs/bot-contractor-spec.md` etc.)
  without coordinating; you can add new docs under `bot-net/docs/` freely

## Reporting progress

Commit early and often to branch `bot-net`. Push regularly. Adnan
relays your status to the backend agent when relevant.
