import { Router, Request, Response } from "express";
import { audioOrchestrator } from "../audio/audioOrchestrator";

export const audioRouter = Router();

function getCorrelationId(req: Request): string | null {
  const raw = req.params["correlationId"];
  const value = Array.isArray(raw) ? raw[0] : raw;
  return value && value.length > 0 ? value : null;
}

audioRouter.post("/start/:correlationId", async (req: Request, res: Response) => {
  const correlationId = getCorrelationId(req);
  if (!correlationId) {
    res.status(400).json({ error: "correlationId is required" });
    return;
  }

  try {
    await audioOrchestrator.startForCall(correlationId, req);
    res.status(200).json({ ok: true, correlationId });
  } catch (err: unknown) {
    const message = err instanceof Error ? err.message : String(err);
    console.error("audio start failed", { correlationId, error: message });
    res.status(500).json({ error: message });
  }
});

audioRouter.post("/stop/:correlationId", (req: Request, res: Response) => {
  const correlationId = getCorrelationId(req);
  if (!correlationId) {
    res.status(400).json({ error: "correlationId is required" });
    return;
  }

  audioOrchestrator.stopForCall(correlationId);
  res.status(200).json({ ok: true, correlationId });
});
