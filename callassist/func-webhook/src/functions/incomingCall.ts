import { app, HttpRequest, HttpResponseInit, InvocationContext } from "@azure/functions";

interface EventGridEvent {
  id?:        string;
  topic?:     string;
  subject?:   string;
  eventType?: string;
  eventTime?: string;
  data?:      Record<string, unknown>;
}

interface SubscriptionValidationData {
  validationCode: string;
  validationUrl?: string;
}

interface IncomingCallData {
  from?: {
    rawId?:       string;
    phoneNumber?: { value?: string };
    kind?:        string;
  };
  to?: {
    rawId?:       string;
    phoneNumber?: { value?: string };
  };
  callerDisplayName?:   string;
  incomingCallContext?: string;
  correlationId?:       string;
}

interface IncomingCallForwardPayload {
  rawPhone:            string;
  correlationId:       string;
  incomingCallContext: string;
  callerDisplayName:   string | null;
}

const SUBSCRIPTION_VALIDATION_EVENT = "Microsoft.EventGrid.SubscriptionValidationEvent";
const INCOMING_CALL_EVENT           = "Microsoft.Communication.IncomingCall";

async function incomingCall(
  req:     HttpRequest,
  context: InvocationContext,
): Promise<HttpResponseInit> {
  let body: unknown;
  try {
    body = await req.json();
  } catch (err) {
    context.log("Failed to parse JSON body", err);
    return { status: 200 };
  }

  const events: EventGridEvent[] = Array.isArray(body)
    ? (body as EventGridEvent[])
    : [body as EventGridEvent];

  for (const event of events) {
    if (event.eventType === SUBSCRIPTION_VALIDATION_EVENT) {
      const data = event.data as SubscriptionValidationData | undefined;
      const validationCode = data?.validationCode ?? "";
      context.log("Event Grid subscription validation handshake", { validationCode });
      return {
        status:  200,
        jsonBody: { validationResponse: validationCode },
      };
    }
  }

  const backendUrl = process.env["BACKEND_URL"];
  if (!backendUrl) {
    context.log("BACKEND_URL is not set — skipping forward");
    return { status: 200 };
  }

  for (const event of events) {
    if (event.eventType !== INCOMING_CALL_EVENT) {
      context.log("Ignoring non-IncomingCall event", { eventType: event.eventType });
      continue;
    }

    const data = event.data as IncomingCallData | undefined;
    const rawPhone =
      data?.from?.phoneNumber?.value ??
      data?.from?.rawId ??
      "";
    const correlationId       = data?.correlationId ?? "";
    const incomingCallContext = data?.incomingCallContext ?? "";
    const callerDisplayName   = data?.callerDisplayName ?? null;

    if (!correlationId || !incomingCallContext) {
      context.log("IncomingCall event missing required fields", {
        hasCorrelationId:       Boolean(correlationId),
        hasIncomingCallContext: Boolean(incomingCallContext),
      });
      continue;
    }

    const payload: IncomingCallForwardPayload = {
      rawPhone,
      correlationId,
      incomingCallContext,
      callerDisplayName,
    };

    const target = `${backendUrl.replace(/\/+$/, "")}/calls/incoming`;

    fetch(target, {
      method:  "POST",
      headers: { "content-type": "application/json" },
      body:    JSON.stringify(payload),
    })
      .then((resp) => {
        context.log("Forwarded IncomingCall", {
          correlationId,
          status: resp.status,
        });
      })
      .catch((err: unknown) => {
        context.log("Failed to forward IncomingCall", {
          correlationId,
          error: err instanceof Error ? err.message : String(err),
        });
      });
  }

  return { status: 200 };
}

app.http("incomingCall", {
  methods:    ["POST", "OPTIONS"],
  authLevel:  "anonymous",
  route:      "incoming-call",
  handler:    incomingCall,
});
