function req(k: string): string {
  const v = process.env[k];
  if (!v) throw new Error(`Missing env var: ${k}`);
  return v;
}

export const config = {
  acs: {
    connectionString: req("ACS_CONNECTION_STRING"),
    callbackBaseUrl:  req("CALLBACK_BASE_URL"),
  },
  entra: {
    tenantId:     req("ENTRA_TENANT_ID"),
    clientId:     req("ENTRA_CLIENT_ID"),
    clientSecret: req("ENTRA_CLIENT_SECRET"),
  },
  crm: {
    baseUrl: req("CRM_BASE_URL"),
    apiKey:  req("CRM_API_KEY"),
  },
  signalr: {
    connectionString: req("SIGNALR_CONNECTION_STRING"),
  },
  openai: {
    endpoint:   req("AZURE_OPENAI_ENDPOINT"),
    key:        req("AZURE_OPENAI_KEY"),
    deployment: req("AZURE_OPENAI_DEPLOYMENT"),
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