import { Router, Request, Response } from "express";
import { handleIncomingCall } from "../callAutomation/callHandler";
import { handleCallbackEvent } from "../callAutomation/callEvents";
import { transferCallToTeams } from "../callAutomation/transferhandler";

export const callbackRouter = Router();

callbackRouter.post("/incoming", (req: Request, res: Response) => {
  console.log("=== INCOMING CALL REQUEST RECEIVED ===", req.body);
  res.status(200).send();

  handleIncomingCall(req.body).catch((err: unknown) => {
    console.error("handleIncomingCall failed", err);
  });
});

callbackRouter.post("/callback/:correlationId", handleCallbackEvent);

callbackRouter.post("/transfer", async (req: Request, res: Response) => {
  const { correlationId, teamsUserId } = req.body ?? {};

  if (typeof correlationId !== "string" || typeof teamsUserId !== "string") {
    res.status(400).json({ error: "correlationId and teamsUserId are required" });
    return;
  }

  try {
    await transferCallToTeams(correlationId, teamsUserId);
    res.status(200).json({ ok: true });
  } catch (err: unknown) {
    const message = err instanceof Error ? err.message : String(err);
    console.error("transferCallToTeams failed", { correlationId, error: message });
    res.status(500).json({ error: message });
  }
});
