import { IncomingMessage, Server as HttpServer } from "http";
import { Duplex } from "stream";
import { WebSocketServer, WebSocket, RawData } from "ws";
import { audioOrchestrator } from "../audio/audioOrchestrator";
import { callStore } from "../state/callStore";
import { pushToAgent } from "../signalr/hub";
import { trackEvent, trackException } from "../utils/telemetry";

const PATH_PATTERN = /^\/bot\/audio\/([^/?]+)\/?$/;
const SPEAKER_CALLER = 0x00;
const SPEAKER_AGENT  = 0x01;

interface CallStartedMsg {
  type:               "call.started";
  correlationId:      string;
  callerPhone?:       string | null;
  callerDisplayName?: string | null;
  agentUpn?:          string | null;
  startedAt?:         string;
}

interface CallEndedMsg {
  type:          "call.ended";
  correlationId: string;
  endedAt?:      string;
  reason?:       string;
}

interface CallErrorMsg {
  type:          "call.error";
  correlationId: string;
  message?:      string;
  code?:         string;
}

type ControlMsg = CallStartedMsg | CallEndedMsg | CallErrorMsg;

export function attachAudioIngestServer(httpServer: HttpServer): WebSocketServer {
  const wss = new WebSocketServer({ noServer: true });
  const expectedSecret = process.env["BACKEND_INGEST_SECRET"];

  httpServer.on("upgrade", (req: IncomingMessage, socket: Duplex, head: Buffer) => {
    const rawUrl = req.url ?? "";
    const path = rawUrl.split("?")[0] ?? "";
    const match = PATH_PATTERN.exec(path);
    if (!match) return; // not for this server; another upgrade handler may take it

    const correlationId = decodeURIComponent(match[1] ?? "");
    if (!correlationId) {
      socket.write("HTTP/1.1 400 Bad Request\r\n\r\n");
      socket.destroy();
      return;
    }

    if (!expectedSecret) {
      console.error("BACKEND_INGEST_SECRET not configured; rejecting all bot upgrades");
      socket.write("HTTP/1.1 503 Service Unavailable\r\n\r\n");
      socket.destroy();
      return;
    }

    const authHeader = req.headers.authorization ?? "";
    const bearer = authHeader.startsWith("Bearer ") ? authHeader.slice(7) : "";
    if (bearer !== expectedSecret) {
      socket.write("HTTP/1.1 401 Unauthorized\r\n\r\n");
      socket.destroy();
      return;
    }

    wss.handleUpgrade(req, socket, head, (ws) => {
      handleConnection(ws, correlationId).catch((err: unknown) => {
        const message = err instanceof Error ? err.message : String(err);
        console.error("bot ws setup failed", { correlationId, message });
        try { ws.close(1011, "setup failed"); } catch { /* ignore */ }
      });
    });
  });

  return wss;
}

async function handleConnection(ws: WebSocket, correlationId: string): Promise<void> {
  console.log("=== BOT WS CONNECTED ===", { correlationId });
  trackEvent("bot_ws_connected", { correlationId });

  let started = false;
  let bytesIn = 0;
  let framesIn = 0;

  ws.on("message", async (data: RawData, isBinary: boolean) => {
    try {
      if (!isBinary) {
        // Control message (JSON text frame)
        const text = bufferOf(data).toString("utf-8");
        let msg: ControlMsg;
        try {
          msg = JSON.parse(text) as ControlMsg;
        } catch {
          console.warn("bot ws invalid JSON control frame", { correlationId, text });
          return;
        }
        if (msg.type === "call.started") {
          await handleCallStarted(msg);
          started = true;
        } else if (msg.type === "call.ended") {
          console.log("bot ws call.ended", { correlationId, reason: msg.reason });
          trackEvent("bot_call_ended_signal", { correlationId, reason: msg.reason });
          // Cleanup happens on ws close; nothing further here.
        } else if (msg.type === "call.error") {
          console.error("bot ws call.error", { correlationId, msg });
          trackEvent("bot_call_error_signal", { correlationId, code: msg.code, message: msg.message });
        } else {
          console.log("bot ws unknown control msg", { correlationId, msg });
        }
        return;
      }

      // Binary audio frame: [1 byte speaker][N bytes PCM]
      const buf = bufferOf(data);
      if (buf.length < 2) return;
      const speakerTag = buf[0];
      const pcm = buf.subarray(1);
      framesIn += 1;
      bytesIn += pcm.length;

      if (!started) {
        // Audio arrived before call.started — buffer for orchestrator anyway,
        // but log once
        if (framesIn === 1) {
          console.warn("bot ws audio before call.started", { correlationId });
        }
      }

      if (speakerTag === SPEAKER_CALLER) {
        audioOrchestrator.writeCallerAudio(correlationId, pcm);
      } else if (speakerTag === SPEAKER_AGENT) {
        audioOrchestrator.writeAgentAudio(correlationId, pcm);
      } else {
        if (framesIn === 1) {
          console.warn("bot ws unknown speaker tag", { correlationId, tag: speakerTag });
        }
      }
    } catch (err) {
      trackException(err, { correlationId, stage: "bot_ws_message" });
      console.error("bot ws message handler failed", { correlationId, err });
    }
  });

  ws.on("close", (code, reason) => {
    console.log("=== BOT WS CLOSED ===", {
      correlationId,
      code,
      reason: reason.toString("utf-8"),
      framesIn,
      bytesIn,
    });
    trackEvent("bot_ws_closed", { correlationId, code, framesIn, bytesIn });
    audioOrchestrator.stopForCall(correlationId);
    callStore.update(correlationId, { phase: "ended", endedAt: new Date() });
    pushToAgent("callEnded", { correlationId, endedAt: new Date().toISOString() })
      .catch((err) => console.error("callEnded push failed", err));
  });

  ws.on("error", (err: Error) => {
    console.error("bot ws error", { correlationId, error: err.message });
    trackException(err, { correlationId, stage: "bot_ws_error" });
  });
}

async function handleCallStarted(msg: CallStartedMsg): Promise<void> {
  const { correlationId } = msg;
  console.log("=== BOT CALL STARTED ===", msg);
  trackEvent("bot_call_started", {
    correlationId,
    callerPhone: msg.callerPhone,
    agentUpn:    msg.agentUpn,
  });

  callStore.set(correlationId, {
    correlationId,
    callConnectionId:  null,
    callerPhoneNumber: msg.callerPhone ?? "",
    callerInfo:        null,
    phase:             "active",
    startedAt:         msg.startedAt ? new Date(msg.startedAt) : new Date(),
    answeredAt:        new Date(),
    endedAt:           null,
    assignedAgentId:   msg.agentUpn ?? null,
    callbackUri:       "",
  });

  await audioOrchestrator.startForCall(correlationId);

  await pushToAgent("callAnswered", {
    correlationId,
    callConnectionId:  null,
    callerPhone:       msg.callerPhone ?? null,
    callerDisplayName: msg.callerDisplayName ?? null,
    answeredAt:        new Date().toISOString(),
  });
}

function bufferOf(data: RawData): Buffer {
  if (Buffer.isBuffer(data)) return data;
  if (Array.isArray(data)) return Buffer.concat(data);
  return Buffer.from(data);
}
