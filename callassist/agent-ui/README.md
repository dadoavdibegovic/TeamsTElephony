# agent-ui

React agent panel for CallTranskript. Connects to the backend over SignalR
and shows caller info, live transcript, AI suggestions, and call status.

## Stack

- Vite + React 18 + TypeScript
- `@microsoft/signalr` client (negotiates against the backend, then opens a hub connection)

## Environment

Create `.env.local` (or set the env vars in your shell) before running:

```
VITE_BACKEND_URL=http://localhost:3000
VITE_SIGNALR_HUB=calltranskript
```

In production these point at the deployed backend, e.g.
`https://app-calltranskript-backend.azurewebsites.net`.

## Scripts

```sh
npm run dev     # vite dev server with HMR
npm run build   # type-check + production bundle to dist/
npm run preview # serve the built bundle locally
```

## How it works

`src/hooks/useSignalR.ts` builds a `HubConnection` against
`${VITE_BACKEND_URL}/${VITE_SIGNALR_HUB}`, which the backend resolves via
its `/<hub>/negotiate` endpoint (`signalrRouter.ts` → `buildClientNegotiateResponse`).

The hub pushes these events (see `agent-ui/src/types/callTypes.ts` for shapes):

| Event              | When                                                      |
|--------------------|-----------------------------------------------------------|
| `callAnswered`     | Backend has answered the ACS call                          |
| `callerInfo`       | Entra + CRM enrichment has resolved                        |
| `callConnected`    | ACS `CallConnected` event received                         |
| `callTransferring` | A transfer to a Teams user is in progress                  |
| `callEnded`        | ACS `CallDisconnected` event received                      |
| `transcript`       | Azure Speech recognized (interim or final) a chunk         |
| `aiSuggestion`     | GPT-4o produced a suggestion from the running transcript   |

`App.tsx` wires the events to component state; the components are
purely presentational and re-render on state changes.

## Notes

- The hub connection is currently anonymous — anyone who can reach the
  negotiate endpoint gets a hub token. This is fine behind App Service
  Easy Auth or a private network; review the auth posture before
  exposing publicly.
- All components use inline styles to avoid pulling in a styling library.
  If this grows much further, consider extracting a shared theme object.
