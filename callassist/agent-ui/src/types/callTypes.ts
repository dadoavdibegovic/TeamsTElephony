export interface EntraProfile {
  id:          string;
  displayName: string;
  mail:        string | null;
  jobTitle:    string | null;
  department:  string | null;
  companyName: string | null;
  manager:     string | null;
}

export interface CrmProfile {
  customerId:      string | null;
  fullName:        string | null;
  accountName:     string | null;
  openTickets:     number | null;
  lastContactDate: string | null;
  notes:           string | null;
  rawData:         Record<string, unknown>;
}

export interface CallerInfo {
  phoneNumber: string;
  displayName: string | null;
  entra:       EntraProfile | null;
  crm:         CrmProfile | null;
  enrichedAt:  string;
}

export type CallPhase =
  | "incoming"
  | "active"
  | "transferring"
  | "ended";

export const ConnectionState = {
  Disconnected: "Disconnected",
  Connecting:   "Connecting",
  Connected:    "Connected",
  Reconnecting: "Reconnecting",
} as const;
export type ConnectionState =
  (typeof ConnectionState)[keyof typeof ConnectionState];

export interface TranscriptLine {
  id:        string;
  text:      string;
  isFinal:   boolean;
  timestamp: string;
}

export interface Suggestion {
  id:         string;
  suggestion: string;
  transcript: string;
  timestamp:  string;
}

export interface CallAnsweredEvent {
  correlationId:     string;
  callConnectionId:  string;
  callerPhone:       string;
  callerDisplayName: string | null;
  answeredAt:        string;
}

export interface CallerInfoEvent {
  correlationId: string;
  callerInfo:    CallerInfo;
}

export interface CallConnectedEvent {
  correlationId: string;
}

export interface CallTransferringEvent {
  correlationId: string;
}

export interface CallEndedEvent {
  correlationId: string;
  endedAt:       string;
}

export interface TranscriptEvent {
  correlationId: string;
  text:          string;
  isFinal:       boolean;
}

export interface AiSuggestionEvent {
  correlationId: string;
  suggestion:    string;
  transcript:    string;
}

export type CallEvent =
  | { name: "callAnswered";     payload: CallAnsweredEvent }
  | { name: "callerInfo";       payload: CallerInfoEvent }
  | { name: "callConnected";    payload: CallConnectedEvent }
  | { name: "callTransferring"; payload: CallTransferringEvent }
  | { name: "callEnded";        payload: CallEndedEvent }
  | { name: "transcript";       payload: TranscriptEvent }
  | { name: "aiSuggestion";     payload: AiSuggestionEvent };

export type CallEventName = CallEvent["name"];

export type CallEventPayload<N extends CallEventName> = Extract<
  CallEvent,
  { name: N }
>["payload"];
