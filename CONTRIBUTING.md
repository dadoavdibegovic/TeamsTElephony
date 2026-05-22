# Contributing — multi-agent workspace conventions

This repo is currently worked by two Claude agents running in separate
terminals on the same machine. To avoid stepping on each other,
follow these conventions.

## Workspace ownership

| Path | Owned by |
|---|---|
| `callassist/` | **Backend agent** (Node.js / Express, Speech, OpenAI, SignalR, agent-ui) |
| `bot-net/` | **Bot agent** (.NET 8 Compliance Recording bot) |
| `docs/` | shared — coordinate before changing existing docs; new docs are fine |
| `ops/` | shared — KQL queries |
| `.github/workflows/` | shared — coordinate; the bot agent adds their own deploy job |
| `README.md`, `CONTRIBUTING.md` | shared — coordinate |
| Azure resources via `az` CLI | shared subscription `Call_Transkript`; communicate before destructive ops |

**Strict rule**: agents must not modify files inside the other agent's
ownership path without explicit coordination via the human (Adnan).

## Branching

- `master` is the integration branch
- Each agent works on a dedicated branch:
  - Backend agent: works directly on `master` (was here first; existing pattern)
  - Bot agent: works on `bot-net` — push there, open a PR when ready
- The human merges PRs after reviewing both agents agree

Why this asymmetry: the backend has 10+ commits already on master.
Forcing it to a branch now would be churn. The .NET bot is greenfield
so it gets a clean branch from day one.

## Commit conventions

- Imperative subject line, ~70 chars max
- Body explains the **why**, not the what (diff shows the what)
- Co-author the assisting Claude:
  ```
  Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
  ```

## Azure resources

Shared subscription: `Call_Transkript` (`0c3d1568-2bf4-4eb6-b037-1b94eb8b5061`).
Same `az login` session is shared across both terminals.

| Resource | Created by | Don't touch without coordinating |
|---|---|---|
| `app-calltranskript-backend` | Backend agent | yes |
| `kv-calltranskript-prod` | Backend agent | yes (read-only for bot agent — fine to read secrets) |
| `bot-calltranskript-prod` (Azure Bot Service) | Backend agent (provisioned) | yes — its calling webhook is the one thing the bot agent updates |
| `sigr-calltranskript-prod`, `speech-calltranskript-prod`, `oai-calltranskript-prod` | Backend agent | yes |
| `bot-net-prod` (App Service for .NET bot) | Bot agent (will provision) | bot agent owns it |
| Compliance Recording policy in Teams | Human (Adnan) via PowerShell | post-deploy step |

### Token expiration

The shared `az` token expires every ~9 hours due to conditional access.
When this happens to either agent, ask Adnan to run `az login` in any
PowerShell — the token cache is shared, so both terminals pick up
fresh credentials immediately.

## Communication

- The human (Adnan) is the bridge. If you need something from the
  other agent, say so in your output and Adnan relays.
- **Don't make assumptions about what the other agent did** — read the
  git log / files to verify state before acting.
- **Don't auto-merge PRs.** Adnan does the merging.

## Definition of done

A change is done when:
- It's committed with a clear message
- Pushed to `origin/<branch>`
- Tests + typecheck pass for the affected workspace
- README or relevant doc is updated if behaviour changed
- For the bot agent specifically: the acceptance checklist in
  `docs/bot-contractor-spec.md` is satisfied
