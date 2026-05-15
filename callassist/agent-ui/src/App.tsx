import { useEffect, useState } from "react";
import { useSignalR } from "./hooks/useSignalR";
import type {
  CallAnsweredEvent,
  CallConnectedEvent,
  CallEndedEvent,
  CallTransferringEvent,
  CallerInfo,
  CallerInfoEvent,
  CallPhase,
  Suggestion,
  TranscriptEvent,
  TranscriptLine,
  AiSuggestionEvent,
} from "./types/callTypes";
import { CallerCard } from "./components/CallerCard";
import { TranscriptPanel } from "./components/TranscriptPanel";
import { SuggestionPanel } from "./components/SuggestionPanel";
import { CallStatusBar } from "./components/CallStatusBar";

const styles: Record<string, React.CSSProperties> = {
  app: {
    minHeight:     "100vh",
    background:    "#0f172a",
    color:         "#e2e8f0",
    fontFamily:    "system-ui, sans-serif",
    padding:       20,
    boxSizing:     "border-box" as const,
    display:       "flex",
    flexDirection: "column" as const,
    gap:           16,
  },
  layout: {
    display:              "grid",
    gridTemplateColumns:  "minmax(300px, 380px) 1fr",
    gap:                  16,
    flex:                 1,
    minHeight:            0,
  },
  rightCol: {
    display:        "flex",
    flexDirection:  "column" as const,
    gap:            16,
    minHeight:      0,
  },
};

function uid(): string {
  return Math.random().toString(36).slice(2) + Date.now().toString(36);
}

function App() {
  const { on, off, connectionState } = useSignalR();

  const [phase,         setPhase]         = useState<CallPhase | null>(null);
  const [callerPhone,   setCallerPhone]   = useState<string | null>(null);
  const [answeredAt,    setAnsweredAt]    = useState<string | null>(null);
  const [callerInfo,    setCallerInfo]    = useState<CallerInfo | null>(null);
  const [enriching,     setEnriching]     = useState(false);
  const [transcript,    setTranscript]    = useState<TranscriptLine[]>([]);
  const [suggestions,   setSuggestions]   = useState<Suggestion[]>([]);

  useEffect(() => {
    const onCallAnswered = (p: CallAnsweredEvent) => {
      setPhase("active");
      setCallerPhone(p.callerPhone);
      setAnsweredAt(p.answeredAt);
      setCallerInfo((current) =>
        current ?? {
          phoneNumber: p.callerPhone,
          displayName: p.callerDisplayName,
          entra:       null,
          crm:         null,
          enrichedAt:  new Date().toISOString(),
        },
      );
      setEnriching(true);
      setTranscript([]);
      setSuggestions([]);
    };

    const onCallerInfo = (p: CallerInfoEvent) => {
      setCallerInfo(p.callerInfo);
      setEnriching(false);
    };

    const onCallConnected = (_p: CallConnectedEvent) => {
      setPhase("active");
    };

    const onCallTransferring = (_p: CallTransferringEvent) => {
      setPhase("transferring");
    };

    const onCallEnded = (_p: CallEndedEvent) => {
      setPhase("ended");
      setEnriching(false);
    };

    const onTranscript = (p: TranscriptEvent) => {
      setTranscript((prev) => {
        const ts = new Date().toISOString();
        const withoutInterim = prev.filter((l) => l.isFinal);
        if (p.isFinal) {
          return [...withoutInterim, { id: uid(), text: p.text, isFinal: true, timestamp: ts }];
        }
        return [...withoutInterim, { id: "interim", text: p.text, isFinal: false, timestamp: ts }];
      });
    };

    const onAiSuggestion = (p: AiSuggestionEvent) => {
      setSuggestions((prev) =>
        [
          {
            id:         uid(),
            suggestion: p.suggestion,
            transcript: p.transcript,
            timestamp:  new Date().toISOString(),
          },
          ...prev,
        ].slice(0, 20),
      );
    };

    on("callAnswered",     onCallAnswered);
    on("callerInfo",       onCallerInfo);
    on("callConnected",    onCallConnected);
    on("callTransferring", onCallTransferring);
    on("callEnded",        onCallEnded);
    on("transcript",       onTranscript);
    on("aiSuggestion",     onAiSuggestion);

    return () => {
      off("callAnswered",     onCallAnswered);
      off("callerInfo",       onCallerInfo);
      off("callConnected",    onCallConnected);
      off("callTransferring", onCallTransferring);
      off("callEnded",        onCallEnded);
      off("transcript",       onTranscript);
      off("aiSuggestion",     onAiSuggestion);
    };
  }, [on, off]);

  return (
    <div style={styles.app}>
      <CallStatusBar
        phase={phase}
        callerPhone={callerPhone}
        answeredAt={answeredAt}
        connectionState={connectionState}
      />
      <div style={styles.layout}>
        <CallerCard callerInfo={callerInfo} isLoading={enriching} />
        <div style={styles.rightCol}>
          <SuggestionPanel suggestions={suggestions} />
          <TranscriptPanel
            transcript={transcript}
            isActive={phase === "active" || phase === "transferring"}
          />
        </div>
      </div>
    </div>
  );
}

export default App;
