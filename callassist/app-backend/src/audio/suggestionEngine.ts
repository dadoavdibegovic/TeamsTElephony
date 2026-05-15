import { AzureOpenAI } from "openai";
import { config } from "../config/config";
import { trackEvent, trackMetric } from "../utils/telemetry";

const SYSTEM_PROMPT =
  "Du bist ein KI-Assistent für Call-Center-Agenten bei SGB Energie. " +
  "Analysiere das laufende Gespräch und gib dem Agenten eine kurze, " +
  "konkrete Empfehlung (max. 2 Sätze). Antworte nur mit der Empfehlung, " +
  "ohne Erklärungen.";

const MAX_BUFFER_CHARS = 2000;
const MIN_DELTA_CHARS  = 50;
const MIN_INTERVAL_MS  = 3000;
const API_VERSION      = "2024-10-21";

export class SuggestionEngine {
  private readonly client: AzureOpenAI;
  private lastTranscript      = "";
  private lastSuggestionAt = 0;

  constructor() {
    this.client = new AzureOpenAI({
      endpoint:   config.openai.endpoint,
      apiKey:     config.openai.key,
      deployment: config.openai.deployment,
      apiVersion: API_VERSION,
    });
  }

  async getSuggestion(transcript: string): Promise<string | null> {
    const trimmed = transcript.length > MAX_BUFFER_CHARS
      ? transcript.slice(-MAX_BUFFER_CHARS)
      : transcript;

    const delta = Math.abs(trimmed.length - this.lastTranscript.length);
    if (delta < MIN_DELTA_CHARS) return null;

    const now = Date.now();
    if (now - this.lastSuggestionAt < MIN_INTERVAL_MS) return null;

    this.lastTranscript      = trimmed;
    this.lastSuggestionAt = now;

    const start = Date.now();
    try {
      const response = await this.client.chat.completions.create({
        model:       config.openai.deployment,
        temperature: 0.3,
        max_tokens:  150,
        messages: [
          { role: "system", content: SYSTEM_PROMPT },
          { role: "user",   content: trimmed },
        ],
      });

      const text = response.choices[0]?.message?.content?.trim() ?? "";
      const latencyMs = Date.now() - start;
      if (text.length > 0) {
        trackEvent("suggestion_generated", {
          latencyMs,
          transcriptLength: trimmed.length,
          suggestionLength: text.length,
        });
        trackMetric("suggestion_latency_ms", latencyMs);
        return text;
      }
      return null;
    } catch (err) {
      console.error("SuggestionEngine OpenAI call failed", {
        error: err instanceof Error ? err.message : String(err),
      });
      trackEvent("suggestion_failed", {
        error: err instanceof Error ? err.message : String(err),
      });
      return null;
    }
  }
}
