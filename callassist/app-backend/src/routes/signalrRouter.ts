import { Router, Request, Response } from "express";
import { buildClientNegotiateResponse } from "../signalr/hub";

export const signalrRouter = Router();

signalrRouter.get("/calltranskript/negotiate", (_req: Request, res: Response) => {
  try {
    const body = buildClientNegotiateResponse();
    res.status(200).json(body);
  } catch (err) {
    const message = err instanceof Error ? err.message : String(err);
    console.error("negotiate failed", err);
    res.status(500).json({ error: message });
  }
});
