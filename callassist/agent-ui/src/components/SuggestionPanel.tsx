import { useEffect, useRef, useState } from "react";
import type { Suggestion } from "../types/callTypes";

interface Props {
  suggestions: Suggestion[];
}

const styles: Record<string, React.CSSProperties> = {
  panel: {
    background:    "#1e293b",
    border:        "1px solid #334155",
    borderRadius:  10,
    padding:       20,
    color:         "#e2e8f0",
    fontFamily:    "system-ui, sans-serif",
  },
  header: {
    fontSize:      12,
    color:         "#94a3b8",
    letterSpacing: 1,
    textTransform: "uppercase" as const,
    marginBottom:  12,
  },
  latest: {
    background:   "#0b3a64",
    border:       "1px solid #1e6dad",
    borderRadius: 8,
    padding:      14,
    fontSize:     15,
    lineHeight:   1.5,
    color:        "#f1f5f9",
    position:     "relative" as const,
  },
  latestPulse: {
    boxShadow: "0 0 0 0 rgba(59, 130, 246, 0.6)",
    animation: "suggestion-pulse 1.2s ease-out 1",
  },
  copyBtn: {
    background:   "transparent",
    border:       "1px solid #475569",
    color:        "#cbd5e1",
    borderRadius: 4,
    padding:      "2px 8px",
    fontSize:     11,
    cursor:       "pointer",
    marginLeft:   8,
  },
  previousList: {
    marginTop:   16,
    paddingTop:  12,
    borderTop:   "1px solid #334155",
  },
  previousTitle: {
    fontSize:      11,
    color:         "#64748b",
    textTransform: "uppercase" as const,
    letterSpacing: 1,
    marginBottom:  8,
  },
  previousItem: {
    fontSize:     12,
    color:        "#94a3b8",
    marginBottom: 8,
    lineHeight:   1.4,
    display:      "flex",
    justifyContent: "space-between",
    gap:          8,
  },
  empty: {
    color:    "#64748b",
    fontSize: 13,
    fontStyle: "italic",
  },
  topRow: {
    display:      "flex",
    justifyContent: "space-between",
    alignItems:    "flex-start",
    gap:           8,
  },
};

function copy(text: string): void {
  navigator.clipboard?.writeText(text).catch(() => {});
}

export function SuggestionPanel({ suggestions }: Props) {
  const [pulse, setPulse] = useState(false);
  const lastIdRef = useRef<string | null>(null);

  useEffect(() => {
    const latestId = suggestions[0]?.id ?? null;
    if (latestId && latestId !== lastIdRef.current) {
      lastIdRef.current = latestId;
      setPulse(true);
      const t = setTimeout(() => setPulse(false), 1200);
      return () => clearTimeout(t);
    }
    return undefined;
  }, [suggestions]);

  if (suggestions.length === 0) {
    return (
      <div style={styles.panel}>
        <div style={styles.header}>AI Suggestions</div>
        <div style={styles.empty}>No suggestions yet</div>
      </div>
    );
  }

  const [latest, ...older] = suggestions;
  const previous = older.slice(0, 4);

  return (
    <div style={styles.panel}>
      <div style={styles.header}>AI Suggestion</div>
      <div style={{ ...styles.latest, ...(pulse ? styles.latestPulse : {}) }}>
        <div style={styles.topRow}>
          <span style={{ flex: 1 }}>{latest.suggestion}</span>
          <button
            type="button"
            style={styles.copyBtn}
            onClick={() => copy(latest.suggestion)}
          >
            Copy
          </button>
        </div>
      </div>

      {previous.length > 0 && (
        <div style={styles.previousList}>
          <div style={styles.previousTitle}>Earlier</div>
          {previous.map((s) => (
            <div key={s.id} style={styles.previousItem}>
              <span style={{ flex: 1 }}>{s.suggestion}</span>
              <button
                type="button"
                style={styles.copyBtn}
                onClick={() => copy(s.suggestion)}
              >
                Copy
              </button>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
