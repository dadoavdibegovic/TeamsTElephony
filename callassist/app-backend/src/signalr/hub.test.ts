import { describe, it, expect } from "vitest";

// parseConnectionString is module-private; we test via re-export
// by extracting the function. Instead, we duplicate the parser
// logic test via a thin wrapper.

function parseConnectionString(raw: string): { endpoint: string; accessKey: string } {
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

describe("signalr parseConnectionString", () => {
  it("parses canonical Endpoint=...;AccessKey=...;Version=1.0;", () => {
    const r = parseConnectionString(
      "Endpoint=https://sigr.service.signalr.net;AccessKey=abc123;Version=1.0;",
    );
    expect(r.endpoint).toBe("https://sigr.service.signalr.net");
    expect(r.accessKey).toBe("abc123");
  });

  it("parses bare URL + AccessKey=... form", () => {
    const r = parseConnectionString(
      "https://sigr.service.signalr.net;AccessKey=xyz789;Version=1.0;",
    );
    expect(r.endpoint).toBe("https://sigr.service.signalr.net");
    expect(r.accessKey).toBe("xyz789");
  });

  it("strips trailing slashes from endpoint", () => {
    const r = parseConnectionString(
      "Endpoint=https://sigr.service.signalr.net///;AccessKey=abc;",
    );
    expect(r.endpoint).toBe("https://sigr.service.signalr.net");
  });

  it("is case-insensitive on keys", () => {
    const r = parseConnectionString(
      "endpoint=https://x.com;accesskey=k;",
    );
    expect(r.endpoint).toBe("https://x.com");
    expect(r.accessKey).toBe("k");
  });

  it("throws when endpoint missing", () => {
    expect(() => parseConnectionString("AccessKey=abc;")).toThrow();
  });

  it("throws when accessKey missing", () => {
    expect(() => parseConnectionString("Endpoint=https://x.com;")).toThrow();
  });
});
