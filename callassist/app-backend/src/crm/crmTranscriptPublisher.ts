import axios from "axios";
import { config } from "../config/config";
import { trackEvent } from "../utils/telemetry";

/**
 * Transport B — POST call + transcript events to the CRM webhook (Bearer key).
 *
 * Contract delivered to the CRM:
 *   call.started{callId, callerId, agentUpn, ...} → CRM opens the window on THAT
 *       agent's session and finds the customer by callerId (the caller's number).
 *   transcript{callId, speaker, text, isFinal, ...} → CRM fills the live pane.
 *   call.ended{callId, ...}.
 *
 * The CRM owns the window UI and any later processing/suggestions (out of scope).
 *
 * Fire-and-forget: publishing never blocks the audio/STT path. Final transcript
 * segments and the call lifecycle events retry; interim segments are best-effort
 * and throttled so we don't hammer the CRM.
 */

export interface CallStartedEvent {
  event:      "call.started";
  callId:     string;
  callerId:   string | null;   // E.164 — the number the CRM opens the window with
  callerName: string | null;
  agentUpn:   string | null;   // which agent's session the CRM targets
  startedAt:  string;
}
export interface TranscriptEventOut {
  event:     "transcript";
  callId:    string;
  speaker:   "caller" | "agent";
  text:      string;
  isFinal:   boolean;
  timestamp: string;
}
export interface CallEndedEvent {
  event:   "call.ended";
  callId:  string;
  endedAt: string;
}
type CrmEvent = CallStartedEvent | TranscriptEventOut | CallEndedEvent;

const INTERIM_MIN_INTERVAL_MS = 500;
const lastInterimAt = new Map<string, number>(); // `${callId}:${speaker}` -> ts

function post(evt: CrmEvent, reliable: boolean): void {
  const { url, apiKey } = config.crmWebhook;
  if (!url) return; // CRM receiver not configured yet — no-op

  const attempts = reliable ? 3 : 1;
  void (async () => {
    for (let i = 0; i < attempts; i++) {
      try {
        await axios.post(url, evt, {
          headers: {
            ...(apiKey ? { Authorization: `Bearer ${apiKey}` } : {}),
            "Content-Type": "application/json",
          },
          timeout: 4000,
        });
        return;
      } catch (err) {
        if (i < attempts - 1) {
          await new Promise((r) => setTimeout(r, 250 * (i + 1)));
          continue;
        }
        const status = axios.isAxiosError(err) ? err.response?.status ?? null : null;
        console.error(
          "CRM webhook post failed",
          evt.event,
          status,
          err instanceof Error ? err.message : String(err),
        );
        trackEvent("crm_webhook_failed", { event: evt.event, status });
      }
    }
  })();
}

export function publishCallStarted(e: Omit<CallStartedEvent, "event">): void {
  post({ event: "call.started", ...e }, true);
}

export function publishTranscript(e: Omit<TranscriptEventOut, "event">): void {
  if (!e.isFinal) {
    const key  = `${e.callId}:${e.speaker}`;
    const now  = Date.now();
    const last = lastInterimAt.get(key) ?? 0;
    if (now - last < INTERIM_MIN_INTERVAL_MS) return; // throttle interim
    lastInterimAt.set(key, now);
  }
  post({ event: "transcript", ...e }, e.isFinal);
}

export function publishCallEnded(e: Omit<CallEndedEvent, "event">): void {
  // Drop per-call interim throttle state.
  for (const k of lastInterimAt.keys()) {
    if (k.startsWith(`${e.callId}:`)) lastInterimAt.delete(k);
  }
  post({ event: "call.ended", ...e }, true);
}
