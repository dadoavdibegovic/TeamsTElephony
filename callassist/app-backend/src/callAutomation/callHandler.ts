import {
  CallAutomationClient,
  AnswerCallOptions,
  MediaStreamingOptions,
} from "@azure/communication-call-automation";
import { getAcsClient } from "./acsClient";
import { callStore } from "./callStore";
import { enrichCaller } from "../enrichment/enrichmentOrchestrator";
import { pushToAgent } from "../signalr/hub";
import { config } from "../config/config";

function mediaTransportUrl(correlationId: string): string {
  const base = config.acs.callbackBaseUrl.replace(/^https:\/\//i, "wss://").replace(/^http:\/\//i, "ws://");
  return `${base.replace(/\/+$/, "")}/media/${encodeURIComponent(correlationId)}`;
}

export interface IncomingCallPayload {
  rawPhone:            string;
  correlationId:       string;
  incomingCallContext: string;
  callerDisplayName:   string | null;
}

export async function handleIncomingCall(payload: IncomingCallPayload): Promise<void> {
  const { rawPhone, correlationId, incomingCallContext } = payload;
  console.log("=== handleIncomingCall START ===", { correlationId, rawPhone });
  const callbackUri = `${config.acs.callbackBaseUrl}/calls/callback/${correlationId}`;

  // 1. Set initial state immediately
  callStore.set(correlationId, {
    correlationId,
    callConnectionId:  null,
    callerPhoneNumber: rawPhone,
    callerInfo:        null,
    phase:             "incoming",
    startedAt:         new Date(),
    answeredAt:        null,
    endedAt:           null,
    assignedAgentId:   null,
    callbackUri,
  });

  // 2. Start enrichment — DO NOT await here
  const enrichPromise = enrichCaller(rawPhone).then(r => {
    callStore.update(correlationId, { callerInfo: r.callerInfo });
    pushToAgent("callerInfo", {
      correlationId,
      callerInfo: r.callerInfo as unknown as Record<string, unknown>,
    }).catch(console.error);
  }).catch(err => console.error("=== ENRICH ERROR ===", err));

  // 3. Answer call immediately — does not wait for enrichment
  try {
    const client: CallAutomationClient = getAcsClient();
    const mediaStreamingOptions: MediaStreamingOptions = {
      transportType:       "websocket",
      audioChannelType:    "mixed",
      transportUrl:        mediaTransportUrl(correlationId),
      contentType:         "audio",
      startMediaStreaming: true,
    };
    const answerOptions: AnswerCallOptions = { mediaStreamingOptions };
    const result = await client.answerCall(incomingCallContext, callbackUri, answerOptions);

    const props = await result.callConnection.getCallConnectionProperties();
    const callConnectionId = props.callConnectionId ?? "";

    callStore.update(correlationId, {
      callConnectionId,
      phase:      "active",
      answeredAt: new Date(),
    });

    await pushToAgent("callAnswered", {
      correlationId,
      callConnectionId,
      callerPhone:       rawPhone,
      callerDisplayName: payload.callerDisplayName,
      answeredAt:        new Date().toISOString(),
    });

  } catch (err) {
    console.error("=== ANSWER ERROR ===", err);
    callStore.update(correlationId, { phase: "ended", endedAt: new Date() });
  }

  // 4. Await enrichment — likely already done in background
  await enrichPromise;
}