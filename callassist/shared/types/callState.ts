import { CallerInfo } from "./callerInfo";

export type CallPhase =
  | "incoming"
  | "enriching"
  | "answering"
  | "active"
  | "transferring"
  | "ended";

export interface CallState {
  correlationId:     string;
  callConnectionId:  string | null;
  callerPhoneNumber: string;
  callerInfo:        CallerInfo | null;
  phase:             CallPhase;
  startedAt:         Date;
  answeredAt:        Date | null;
  endedAt:           Date | null;
  assignedAgentId:   string | null;
  callbackUri:       string;
}