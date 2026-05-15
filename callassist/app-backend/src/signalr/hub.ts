import axios from "axios";
import jwt from "jsonwebtoken";
import { config } from "../config/config";

const HUB_NAME = "calltranskript";

interface ParsedConnection {
  endpoint:  string;
  accessKey: string;
}

let _parsed: ParsedConnection | null = null;

function parseConnectionString(raw: string): ParsedConnection {
  const parts = raw.split(";").map((p) => p.trim()).filter(Boolean);
  let endpoint  = "";
  let accessKey = "";
  for (const part of parts) {
    const eq = part.indexOf("=");
    if (eq > 0) {
      const key = part.slice(0, eq).trim().toLowerCase();
      const val = part.slice(eq + 1).trim();
      if (key === "endpoint")  { endpoint  = val; continue; }
      if (key === "accesskey") { accessKey = val; continue; }
    }
    if (!endpoint && /^https?:\/\//i.test(part)) {
      endpoint = part;
    }
  }
  if (!endpoint || !accessKey) {
    throw new Error("SIGNALR_CONNECTION_STRING is missing Endpoint or AccessKey");
  }
  return { endpoint: endpoint.replace(/\/+$/, ""), accessKey };
}

function getParsed(): ParsedConnection {
  if (_parsed) return _parsed;
  _parsed = parseConnectionString(config.signalr.connectionString);
  return _parsed;
}

function signToken(audience: string, accessKey: string, ttlSeconds = 3600): string {
  return jwt.sign(
    {
      aud: audience,
      exp: Math.floor(Date.now() / 1000) + ttlSeconds,
    },
    accessKey,
    { algorithm: "HS256" },
  );
}

export async function pushToAgent(
  eventName: string,
  payload:   Record<string, unknown>,
): Promise<void> {
  try {
    const { endpoint, accessKey } = getParsed();
    const url   = `${endpoint}/api/v1/hubs/${HUB_NAME}`;
    const token = signToken(url, accessKey);

    await axios.post(
      url,
      { target: eventName, arguments: [payload] },
      {
        headers: {
          Authorization:  `Bearer ${token}`,
          "Content-Type": "application/json",
        },
      },
    );
  } catch (err) {
    const detail = axios.isAxiosError(err) && err.response
      ? { status: err.response.status, data: err.response.data }
      : err instanceof Error ? err.message : String(err);
    console.error("SignalR push failed", eventName, detail);
  }
}

export function buildClientNegotiateResponse(): { url: string; accessToken: string } {
  const { endpoint, accessKey } = getParsed();
  const audience  = `${endpoint}/client/?hub=${HUB_NAME}`;
  const accessToken = signToken(audience, accessKey);
  return { url: audience, accessToken };
}
