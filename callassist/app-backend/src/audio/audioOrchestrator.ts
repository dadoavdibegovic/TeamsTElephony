import { PassThrough } from "stream";
import { SpeechTranscriber, TranscriptEvent } from "./speechTranscriber";
import { SuggestionEngine } from "./suggestionEngine";
import { pushToAgent } from "../signalr/hub";
import { trackEvent, trackException } from "../utils/telemetry";

const MAX_BUFFER_CHARS = 2000;

type Speaker = "caller" | "agent";

interface ActiveSpeaker {
  transcriber: SpeechTranscriber;
  stream:      PassThrough;
}

interface ActiveCall {
  speakers: Record<Speaker, ActiveSpeaker>;
  engine:   SuggestionEngine;
  buffer:   string;
}

class AudioOrchestrator {
  private readonly active = new Map<string, ActiveCall>();

  /**
   * Start audio processing for a call. Source-agnostic: the caller passes
   * raw PCM audio in via writeCallerAudio() / writeAgentAudio(). Designed
   * to be driven by either an ACS media stream (historical) or a Teams
   * Compliance Recording bot (current).
   */
  async startForCall(correlationId: string): Promise<void> {
    if (this.active.has(correlationId)) {
      console.warn("audioOrchestrator: already started for", correlationId);
      return;
    }

    const engine = new SuggestionEngine();
    const speakers: Record<Speaker, ActiveSpeaker> = {
      caller: await this.startSpeaker(correlationId, "caller"),
      agent:  await this.startSpeaker(correlationId, "agent"),
    };

    const call: ActiveCall = { speakers, engine, buffer: "" };
    this.active.set(correlationId, call);

    for (const sp of ["caller", "agent"] as Speaker[]) {
      speakers[sp].transcriber.on("transcript", (event: TranscriptEvent) => {
        this.handleTranscript(correlationId, sp, event).catch((err: unknown) => {
          const msg = err instanceof Error ? err.message : String(err);
          console.error("transcript handling failed", { correlationId, speaker: sp, msg });
          trackException(err, { correlationId, stage: "transcript", speaker: sp });
        });
      });

      speakers[sp].transcriber.on("error", (err: Error) => {
        console.error("transcriber error", { correlationId, speaker: sp, err: err.message });
        trackException(err, { correlationId, stage: "speech", speaker: sp });
      });
    }

    trackEvent("audio_orchestrator_started", { correlationId });
  }

  private async startSpeaker(correlationId: string, speaker: Speaker): Promise<ActiveSpeaker> {
    const stream = new PassThrough();
    const transcriber = new SpeechTranscriber(`${correlationId}:${speaker}`);
    await transcriber.start(stream);
    return { transcriber, stream };
  }

  writeCallerAudio(correlationId: string, pcm: Buffer): void {
    const call = this.active.get(correlationId);
    if (!call) return;
    call.speakers.caller.stream.write(pcm);
  }

  writeAgentAudio(correlationId: string, pcm: Buffer): void {
    const call = this.active.get(correlationId);
    if (!call) return;
    call.speakers.agent.stream.write(pcm);
  }

  stopForCall(correlationId: string): void {
    const call = this.active.get(correlationId);
    if (!call) return;

    for (const sp of ["caller", "agent"] as Speaker[]) {
      try { call.speakers[sp].stream.end(); } catch { /* ignore */ }
      try { call.speakers[sp].transcriber.stop(); } catch { /* ignore */ }
    }
    this.active.delete(correlationId);
    trackEvent("audio_orchestrator_stopped", { correlationId });
  }

  size(): number {
    return this.active.size;
  }

  private async handleTranscript(
    correlationId: string,
    speaker: Speaker,
    event: TranscriptEvent,
  ): Promise<void> {
    const call = this.active.get(correlationId);
    if (!call) return;

    await pushToAgent("transcript", {
      correlationId,
      speaker,
      text:    event.text,
      isFinal: event.isFinal,
    });

    if (!event.isFinal) return;

    // Combined buffer across both speakers for AI context. Prefix each line
    // so the model knows who said what.
    const line = `${speaker === "caller" ? "K" : "A"}: ${event.text}`;
    const next = call.buffer.length > 0 ? `${call.buffer}\n${line}` : line;
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
