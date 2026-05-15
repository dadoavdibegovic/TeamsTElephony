import { useEffect, useRef } from "react";
import type { TranscriptLine } from "../types/callTypes";

interface Props {
  transcript: TranscriptLine[];
  isActive:   boolean;
}

const styles: Record<string, React.CSSProperties> = {
  panel: {
    background:    "#1e293b",
    border:        "1px solid #334155",
    borderRadius:  10,
    padding:       20,
    color:         "#e2e8f0",
    fontFamily:    "system-ui, sans-serif",
    display:       "flex",
    flexDirection: "column" as const,
    minHeight:     0,
    flex:          1,
  },
  header: {
    fontSize:      12,
    color:         "#94a3b8",
    letterSpacing: 1,
    textTransform: "uppercase" as const,
    marginBottom:  12,
    display:       "flex",
    justifyContent: "space-between",
    alignItems:    "center",
  },
  liveDot: {
    display:      "inline-block",
    width:        8,
    height:       8,
    borderRadius: "50%",
    background:   "#10b981",
    marginRight:  6,
    animation:    "pulse 1.5s ease-in-out infinite",
  },
  scroller: {
    flex:        1,
    overflowY:   "auto" as const,
    maxHeight:   "100%",
    paddingRight: 4,
  },
  line: {
    fontSize:     14,
    lineHeight:   1.45,
    color:        "#f1f5f9",
    marginBottom: 8,
  },
  interim: {
    color: "#94a3b8",
    fontStyle: "italic" as const,
  },
  timestamp: {
    fontSize:    11,
    color:       "#64748b",
    marginRight: 8,
  },
  empty: {
    color:    "#64748b",
    fontSize: 13,
    fontStyle: "italic",
  },
};

function formatTime(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return "";
  return d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" });
}

export function TranscriptPanel({ transcript, isActive }: Props) {
  const scrollerRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const el = scrollerRef.current;
    if (!el) return;
    el.scrollTop = el.scrollHeight;
  }, [transcript]);

  return (
    <div style={styles.panel}>
      <div style={styles.header}>
        <span>Transcript</span>
        {isActive && (
          <span>
            <span style={styles.liveDot} />
            <span style={{ fontSize: 11, color: "#10b981", letterSpacing: 1 }}>LIVE</span>
          </span>
        )}
      </div>
      <div style={styles.scroller} ref={scrollerRef}>
        {transcript.length === 0 ? (
          <div style={styles.empty}>Waiting for speech…</div>
        ) : (
          transcript.map((line) => (
            <div
              key={line.id}
              style={{
                ...styles.line,
                ...(line.isFinal ? {} : styles.interim),
              }}
            >
              <span style={styles.timestamp}>{formatTime(line.timestamp)}</span>
              {line.text}
            </div>
          ))
        )}
      </div>
    </div>
  );
}
