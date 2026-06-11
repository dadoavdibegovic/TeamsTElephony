# CRM webhook contract — live call + transcript delivery (for Konrad)

The CallTranskript backend **POSTs** call lifecycle + live transcript events to a
single CRM endpoint. The CRM uses them to open a window on the right agent's screen
(keyed by caller number) and fill a live-transcript pane. Suggestions/processing are
the CRM's side and out of scope here.

## Transport
- **Method:** `POST` (one endpoint receives all three event types).
- **URL:** you provide it → we set it as `CRM_WEBHOOK_URL` (test: on `crm-test.sgb-energie.de`).
- **Auth:** `Authorization: Bearer <API key>` (you mint the key; we store it as `CRM_WEBHOOK_KEY`).
- **Body:** `Content-Type: application/json`, one event object per request.
- **No cookies, no CSRF** (same Bearer style as the ticket API).
- **Expected response:** any `2xx`. A non-2xx (or timeout >4s) makes us retry *reliable* events.

## Events (discriminated by the `event` field)

### 1. `call.started` — open the window
```json
{
  "event":      "call.started",
  "callId":     "a1b2c3-...",        // unique per call; ties all later events to this window
  "callerId":   "+493411234567",     // caller's number (E.164) or null — open the window / find customer by this
  "callerName": "Max Mustermann",    // display name or null
  "agentUpn":   "agent@sgb-energie.de", // WHICH agent answered → open the window in that user's session
  "startedAt":  "2026-06-11T07:15:30.000Z"
}
```

### 2. `transcript` — fill the live pane (streamed during the call)
```json
{
  "event":     "transcript",
  "callId":    "a1b2c3-...",
  "speaker":   "caller",             // "caller" | "agent"
  "text":      "Ich habe eine Frage zur Rechnung",
  "isFinal":   true,                 // false = interim (may be revised); true = stable segment
  "timestamp": "2026-06-11T07:15:34.120Z"
}
```

### 3. `call.ended` — close/finalize
```json
{
  "event":   "call.ended",
  "callId":  "a1b2c3-...",
  "endedAt": "2026-06-11T07:18:02.000Z"
}
```

## Delivery semantics (important for your receiver)
- **Keying:** every event carries `callId`. `call.started` carries `callerId` (the number)
  and `agentUpn` (whose screen). Bind the window to `callId`; open it by `agentUpn` + `callerId`.
- **Interim vs final:** `isFinal:false` segments are live/provisional and get superseded —
  render them as the "currently being spoken" line and replace on the next segment;
  `isFinal:true` is the committed line. Interim is throttled to ≤1 per 500 ms per
  speaker; finals are always sent.
- **Reliability / retries:** `call.started`, `call.ended`, and **final** transcripts are
  retried up to 3× on failure. **Interim** transcripts are best-effort (no retry).
  ⇒ Your receiver should be **idempotent** (a retry may redeliver the same event).
- **Ordering:** events are emitted in order but sent asynchronously; tolerate minor
  reordering and use `timestamp` for sequencing within a `callId`.
- **Fire-and-forget:** we never block the call on your response; just return `2xx` fast.
- **Lifecycle:** exactly one `call.started` and one `call.ended` per `callId`; zero-or-more
  `transcript` events in between. A `transcript` could (rarely) arrive a beat before
  `call.started` under load — treat `call.started` as "ensure window exists".

## What we need from you to wire the test
1. The **POST URL** on `crm-test.sgb-energie.de` that accepts the above.
2. A **test API key** (Bearer) — scope per your side.

Then we set `CRM_WEBHOOK_URL` + `CRM_WEBHOOK_KEY`, place a test call, and you should see:
`call.started` (window opens by number) → live `transcript` events → `call.ended`.
