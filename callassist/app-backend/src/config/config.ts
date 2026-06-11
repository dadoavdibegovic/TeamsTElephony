function req(k: string): string {
  const v = process.env[k];
  if (!v) throw new Error(`Missing env var: ${k}`);
  return v;
}

function opt(k: string): string | undefined {
  const v = process.env[k];
  return v && v.length > 0 ? v : undefined;
}

export const config = {
  // CRM transcript webhook (transport B): we POST call + transcript events here.
  // Optional on purpose — the backend must boot before the CRM receiver endpoint
  // exists; the publisher no-ops until CRM_WEBHOOK_URL is set.
  crmWebhook: {
    url:    opt("CRM_WEBHOOK_URL"),
    apiKey: opt("CRM_WEBHOOK_KEY"),
  },
  speech: {
    key:    req("AZURE_SPEECH_KEY"),
    region: req("AZURE_SPEECH_REGION"),
    locale: process.env["AZURE_SPEECH_LOCALE"] ?? "de-DE",
  },
  app: {
    port:    parseInt(process.env["PORT"] ?? "3000"),
    nodeEnv: process.env["NODE_ENV"] ?? "development",
  },
};
