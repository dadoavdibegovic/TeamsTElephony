import { describe, it, expect, beforeEach } from "vitest";
import { callStore } from "./callStore";
import type { CallState } from "../../../shared/types/callState";

function mkState(id: string, phase: CallState["phase"]): CallState {
  return {
    correlationId:     id,
    callConnectionId:  null,
    callerPhoneNumber: "+49000",
    callerInfo:        null,
    phase,
    startedAt:         new Date(),
    answeredAt:        null,
    endedAt:           null,
    assignedAgentId:   null,
    callbackUri:       "https://x.test/cb",
  };
}

describe("callStore", () => {
  beforeEach(() => {
    // Reset the singleton store between tests
    for (const id of ["a", "b", "c", "d"]) callStore.delete(id);
  });

  it("size() counts only non-ended calls", () => {
    callStore.set("a", mkState("a", "active"));
    callStore.set("b", mkState("b", "transferring"));
    callStore.set("c", mkState("c", "ended"));
    callStore.set("d", mkState("d", "incoming"));
    expect(callStore.size()).toBe(3);
    expect(callStore.total()).toBe(4);
  });

  it("update() patches existing entries", () => {
    callStore.set("a", mkState("a", "incoming"));
    callStore.update("a", { phase: "active", callConnectionId: "cc-1" });
    const s = callStore.get("a");
    expect(s?.phase).toBe("active");
    expect(s?.callConnectionId).toBe("cc-1");
  });

  it("update() is a no-op for unknown ids", () => {
    callStore.update("missing", { phase: "ended" });
    expect(callStore.get("missing")).toBeUndefined();
  });
});
