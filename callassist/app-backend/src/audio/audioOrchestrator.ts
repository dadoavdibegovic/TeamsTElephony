import { SpeechTranscriber, TranscriptEvent } from "./speechTranscriber";
import { SuggestionEngine } from "./suggestionEngine";
import { pushToAgent } from "../signalr/hub";

const MAX_BUFFER_CHARS = 2000;

interface ActiveCall {
  transcriber: SpeechTranscriber;
  engine:      SuggestionEngine;
  buffer:      string;
}

class AudioOrchestrator {
  private readonly active = new Map<string, ActiveCall>();

  async startForCall(
    correlationId: string,
    audioStream:   NodeJS.ReadableStream,
  ): Promise<void> {
    if (this.active.has(correlationId)) {
      throw new Error(`AudioOrchestrator already running for ${correlationId}`);
    }

    const transcriber = new SpeechTranscriber(correlationId);
    const engine      = new SuggestionEngine();
    const call: ActiveCall = { transcriber, engine, buffer: "" };

    transcriber.on("transcript", (event: TranscriptEvent) => {
      this.handleTranscript(correlationId, event).catch((err: unknown) => {
        console.error("Transcript handling failed", {
          correlationId,
          error: err instanceof Error ? err.message : String(err),
        });
      });
    });

    transcriber.on("error", (err: Error) => {
      console.error("SpeechTranscriber error", { correlationId, error: err.message });
    });

    this.active.set(correlationId, call);

    try {
      await transcriber.start(audioStream);
    } catch (err) {
      this.active.delete(correlationId);
      transcriber.stop();
      throw err;
    }
  }

  stopForCall(correlationId: string): void {
    const call = this.active.get(correlationId);
    if (!call) return;
    call.transcriber.stop();
    this.active.delete(correlationId);
  }

  private async handleTranscript(
    correlationId: string,
    event:         TranscriptEvent,
  ): Promise<void> {
    const call = this.active.get(correlationId);
    if (!call) return;

    await pushToAgent("transcript", {
      correlationId,
      text:    event.text,
      isFinal: event.isFinal,
    });

    if (!event.isFinal) return;

    const next = call.buffer.length > 0
      ? `${call.buffer} ${event.text}`
      : event.text;
    call.buffer = next.length > MAX_BUFFER_CHARS
      ? next.slice(-MAX_BUFFER_CHARS)
      : next;

    const suggestion = await call.engine.getSuggestion(call.buffer);
    if (!suggestion) return;

    await pushToAgent("aiSuggestion", {
      correlationId,
      suggestion,
      transcript: call.buffer,
    });
  }
}

export const audioOrchestrator = new AudioOrchestrator();
