import { Router, Request, Response } from "express";
import { buildClientNegotiateResponse } from "../signalr/hub";

export const signalrRouter = Router();

// The @microsoft/signalr JS client POSTs to <hub>/negotiate; some other
// integrations still GET. Handle both.
function handleNegotiate(_req: Request, res: Response): void {
  try {
    const body = buildClientNegotiateResponse();
    res.status(200).json(body);
  } catch (err) {
    const message = err instanceof Error ? err.message : String(err);
    console.error("negotiate failed", err);
    res.status(500).json({ error: message });
  }
}

signalrRouter.get("/calltranskript/negotiate",  handleNegotiate);
signalrRouter.post("/calltranskript/negotiate", handleNegotiate);
