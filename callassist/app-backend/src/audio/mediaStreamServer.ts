import { IncomingMessage, Server as HttpServer } from "http";
import { Duplex, PassThrough } from "stream";
import { WebSocketServer, WebSocket, RawData } from "ws";
import { StreamingData } from "@azure/communication-call-automation";
import { audioOrchestrator } from "./audioOrchestrator";
import { callStore } from "../callAutomation/callStore";

const MEDIA_PATH = /^\/media\/([^/?]+)\/?$/;

export function attachMediaStreamServer(httpServer: HttpServer): WebSocketServer {
  const wss = new WebSocketServer({ noServer: true });

  httpServer.on("upgrade", (req: IncomingMessage, socket: Duplex, head: Buffer) => {
    const rawUrl = req.url ?? "";
    const path = rawUrl.split("?")[0] ?? "";
    const match = MEDIA_PATH.exec(path);
    if (!match) {
      socket.destroy();
      return;
    }
    const correlationId = decodeURIComponent(match[1] ?? "");
    if (!correlationId) {
      socket.destroy();
      return;
    }
    if (!callStore.get(correlationId)) {
      console.warn("media ws upgrade for unknown call", { correlationId });
      socket.write("HTTP/1.1 404 Not Found\r\n\r\n");
      socket.destroy();
      return;
    }
    wss.handleUpgrade(req, socket, head, (ws) => {
      handleMediaConnection(ws, correlationId).catch((err: unknown) => {
        const message = err instanceof Error ? err.message : String(err);
        console.error("media connection setup failed", { correlationId, error: message });
        try { ws.close(1011, "setup failed"); } catch { /* ignore */ }
      });
    });
  });

  return wss;
}

async function handleMediaConnection(ws: WebSocket, correlationId: string): Promise<void> {
  console.log("=== MEDIA WS CONNECTED ===", { correlationId });

  const stream = new PassThrough();
  let stopped = false;
  let framesIn = 0;
  let bytesIn = 0;

  ws.on("message", (data: RawData, isBinary: boolean) => {
    if (stopped) return;
    try {
      const payload: string | ArrayBuffer = isBinary
        ? toArrayBuffer(data)
        : toUtf8(data);
      StreamingData.parse(payload);
      const kind = StreamingData.getStreamingKind();
      if (kind === "AudioData") {
        const audio = StreamingData.parse(payload) as { data: string; isSilent?: boolean };
        if (!audio.data) return;
        const pcm = Buffer.from(audio.data, "base64");
        framesIn += 1;
        bytesIn += pcm.length;
        stream.write(pcm);
      } else if (kind === "AudioMetadata") {
        const meta = StreamingData.parse(payload) as {
          subscriptionId: string;
          encoding: string;
          sampleRate: number;
        };
        console.log("media metadata", { correlationId, meta });
      } else {
        console.log("ignored media frame kind", { correlationId, kind });
      }
    } catch (err: unknown) {
      const message = err instanceof Error ? err.message : String(err);
      console.error("media frame parse failed", { correlationId, error: message });
    }
  });

  ws.on("close", (code: number, reason: Buffer) => {
    console.log("=== MEDIA WS CLOSED ===", {
      correlationId,
      code,
      reason: reason.toString("utf-8"),
      framesIn,
      bytesIn,
    });
    if (stopped) return;
    stopped = true;
    stream.end();
    audioOrchestrator.stopForCall(correlationId);
  });

  ws.on("error", (err: Error) => {
    console.error("media ws error", { correlationId, error: err.message });
  });

  try {
    await audioOrchestrator.startForCall(correlationId, stream);
  } catch (err: unknown) {
    const message = err instanceof Error ? err.message : String(err);
    console.error("audioOrchestrator.startForCall failed", { correlationId, error: message });
    stopped = true;
    stream.end();
    try { ws.close(1011, "orchestrator failed"); } catch { /* ignore */ }
  }
}

function toArrayBuffer(data: RawData): ArrayBuffer {
  if (data instanceof ArrayBuffer) return data;
  if (Array.isArray(data)) {
    const buf = Buffer.concat(data);
    return buf.buffer.slice(buf.byteOffset, buf.byteOffset + buf.byteLength) as ArrayBuffer;
  }
  // Buffer
  return data.buffer.slice(data.byteOffset, data.byteOffset + data.byteLength) as ArrayBuffer;
}

function toUtf8(data: RawData): string {
  if (Array.isArray(data)) return Buffer.concat(data).toString("utf-8");
  if (data instanceof ArrayBuffer) return Buffer.from(data).toString("utf-8");
  return data.toString("utf-8");
}
