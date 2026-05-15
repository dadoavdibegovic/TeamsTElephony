import { Router, Request, Response } from "express";
import { callStore } from "../callAutomation/callStore";

export const healthRouter = Router();

healthRouter.get("/", (_req: Request, res: Response) => {
  res.json({
    status:      "healthy",
    activeCalls: callStore.size(),
    timestamp:   new Date().toISOString(),
  });
});
