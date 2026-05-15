import { Request, Response } from "express";
import { callStore } from "./callStore";
import { pushToAgent } from "../signalr/hub";
import { audioOrchestrator } from "../audio/audioOrchestrator";

export async function handleCallbackEvent(req: Request, res: Response): Promise<void> {
  res.status(200).send();

  const events = Array.isArray(req.body) ? req.body : [req.body];
  const correlationId = Array.isArray(req.params["correlationId"])
    ? req.params["correlationId"][0]
    : req.params["correlationId"];

  for (const e of events) {
    const eventType: string = e.type ?? e.eventType ?? "";

    switch (eventType) {
      case "Microsoft.Communication.CallConnected":
        callStore.update(correlationId, { phase: "active" });
        await pushToAgent("callConnected", { correlationId });
        break;
      case "Microsoft.Communication.CallTransferAccepted":
        callStore.update(correlationId, { phase: "transferring" });
        await pushToAgent("callTransferring", { correlationId });
        break;
      case "Microsoft.Communication.CallTransferFailed":
        await pushToAgent("callTransferFailed", {
          correlationId,
          reason: e.data?.resultInformation?.message ?? "Unknown",
        });
        break;
      case "Microsoft.Communication.CallDisconnected":
        callStore.update(correlationId, { phase: "ended", endedAt: new Date() });
        audioOrchestrator.stopForCall(correlationId);
        await pushToAgent("callEnded", { correlationId, endedAt: new Date().toISOString() });
        setTimeout(() => callStore.delete(correlationId), 5 * 60 * 1000);
        break;
      default:
        console.log("Unhandled ACS event", { eventType, correlationId });
    }
  }
}