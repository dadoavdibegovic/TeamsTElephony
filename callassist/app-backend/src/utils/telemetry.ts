import * as appInsights from "applicationinsights";

function client(): appInsights.TelemetryClient | null {
  return appInsights.defaultClient ?? null;
}

function stringifyProps(
  props: Record<string, unknown> | undefined,
): Record<string, string> | undefined {
  if (!props) return undefined;
  const out: Record<string, string> = {};
  for (const [k, v] of Object.entries(props)) {
    if (v === null || v === undefined) continue;
    out[k] = typeof v === "string" ? v : JSON.stringify(v);
  }
  return out;
}

export function trackEvent(
  name:  string,
  props?: Record<string, unknown>,
): void {
  const c = client();
  if (!c) return;
  c.trackEvent({ name, properties: stringifyProps(props) });
}

export function trackMetric(
  name:  string,
  value: number,
  props?: Record<string, unknown>,
): void {
  const c = client();
  if (!c) return;
  c.trackMetric({ name, value, properties: stringifyProps(props) });
}

export function trackException(
  err:   unknown,
  props?: Record<string, unknown>,
): void {
  const c = client();
  if (!c) return;
  const exception = err instanceof Error ? err : new Error(String(err));
  c.trackException({ exception, properties: stringifyProps(props) });
}
