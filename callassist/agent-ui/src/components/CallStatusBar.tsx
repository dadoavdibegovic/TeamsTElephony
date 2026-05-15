import { useEffect, useState } from "react";
import type { CallPhase } from "../types/callTypes";
import { ConnectionState } from "../types/callTypes";

interface Props {
  phase:           CallPhase | null;
  callerPhone:     string | null;
  answeredAt:      string | null;
  connectionState: ConnectionState;
}

const phaseColors: Record<CallPhase, { bg: string; fg: string; label: string }> = {
  incoming:     { bg: "#1e3a8a", fg: "#bfdbfe", label: "Incoming"     },
  active:       { bg: "#0f4f8a", fg: "#bfdbfe", label: "Active"       },
  transferring: { bg: "#78350f", fg: "#fde68a", label: "Transferring" },
  ended:        { bg: "#374151", fg: "#cbd5e1", label: "Ended"        },
};

const styles: Record<string, React.CSSProperties> = {
  bar: {
    display:        "flex",
    alignItems:     "center",
    justifyContent: "space-between",
    padding:        "10px 18px",
    background:     "#1e293b",
    borderRadius:   10,
    border:         "1px solid #334155",
    color:          "#e2e8f0",
    fontFamily:     "system-ui, sans-serif",
    gap:            16,
  },
  left: {
    display:    "flex",
    alignItems: "center",
    gap:        14,
  },
  pill: {
    padding:      "4px 10px",
    borderRadius: 999,
    fontSize:     12,
    fontWeight:   600,
    letterSpacing: 0.5,
    textTransform: "uppercase" as const,
  },
  phone: {
    fontSize:   16,
    fontWeight: 500,
    color:      "#f1f5f9",
  },
  duration: {
    fontFamily: "ui-monospace, monospace",
    fontSize:   16,
    color:      "#f1f5f9",
  },
  conn: {
    fontSize: 11,
    color:    "#94a3b8",
  },
  connOk: {
    color: "#34d399",
  },
  connBad: {
    color: "#fb7185",
  },
};

function formatDuration(ms: number): string {
  const total   = Math.max(0, Math.floor(ms / 1000));
  const hours   = Math.floor(total / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const seconds = total % 60;
  const mm      = String(minutes).padStart(2, "0");
  const ss      = String(seconds).padStart(2, "0");
  return hours > 0 ? `${hours}:${mm}:${ss}` : `${mm}:${ss}`;
}

export function CallStatusBar({ phase, callerPhone, answeredAt, connectionState }: Props) {
  const [now, setNow] = useState(Date.now());

  useEffect(() => {
    if (!answeredAt || phase === "ended") return undefined;
    const id = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(id);
  }, [answeredAt, phase]);

  const startedMs = answeredAt ? new Date(answeredAt).getTime() : null;
  const durationMs = startedMs ? now - startedMs : 0;

  const meta = phase ? phaseColors[phase] : null;
  const connOk = connectionState === ConnectionState.Connected;

  return (
    <div style={styles.bar}>
      <div style={styles.left}>
        {meta ? (
          <span style={{ ...styles.pill, background: meta.bg, color: meta.fg }}>
            {meta.label}
          </span>
        ) : (
          <span style={{ ...styles.pill, background: "#374151", color: "#cbd5e1" }}>
            Idle
          </span>
        )}
        <span style={styles.phone}>{callerPhone ?? "—"}</span>
      </div>
      <div style={styles.left}>
        {answeredAt && (
          <span style={styles.duration}>{formatDuration(durationMs)}</span>
        )}
        <span style={{ ...styles.conn, ...(connOk ? styles.connOk : styles.connBad) }}>
          {connectionState}
        </span>
      </div>
    </div>
  );
}
