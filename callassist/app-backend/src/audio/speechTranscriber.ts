import { EventEmitter } from "events";
import * as sdk from "microsoft-cognitiveservices-speech-sdk";
import { config } from "../config/config";

export interface TranscriptEvent {
  correlationId: string;
  text:          string;
  isFinal:       boolean;
  timestamp:     Date;
}

export class SpeechTranscriber extends EventEmitter {
  private readonly correlationId: string;
  private recognizer: sdk.SpeechRecognizer | null = null;
  private pushStream: sdk.PushAudioInputStream | null = null;
  private audioStream: NodeJS.ReadableStream | null = null;
  private stopped = false;

  constructor(correlationId: string) {
    super();
    this.correlationId = correlationId;
  }

  async start(audioStream: NodeJS.ReadableStream): Promise<void> {
    if (this.recognizer) {
      throw new Error(`SpeechTranscriber already started for ${this.correlationId}`);
    }

    const speechConfig = sdk.SpeechConfig.fromSubscription(
      config.speech.key,
      config.speech.region,
    );
    speechConfig.speechRecognitionLanguage = config.speech.locale;

    const format = sdk.AudioStreamFormat.getWaveFormatPCM(16000, 16, 1);
    this.pushStream = sdk.AudioInputStream.createPushStream(format);
    const audioConfig = sdk.AudioConfig.fromStreamInput(this.pushStream);

    this.recognizer = new sdk.SpeechRecognizer(speechConfig, audioConfig);

    this.recognizer.recognizing = (_s, e) => {
      if (!e.result.text) return;
      const event: TranscriptEvent = {
        correlationId: this.correlationId,
        text:          e.result.text,
        isFinal:       false,
        timestamp:     new Date(),
      };
      this.emit("transcript", event);
    };

    this.recognizer.recognized = (_s, e) => {
      if (e.result.reason !== sdk.ResultReason.RecognizedSpeech) return;
      if (!e.result.text) return;
      const event: TranscriptEvent = {
        correlationId: this.correlationId,
        text:          e.result.text,
        isFinal:       true,
        timestamp:     new Date(),
      };
      this.emit("transcript", event);
    };

    this.recognizer.canceled = (_s, e) => {
      this.emit("error", new Error(`Speech recognition canceled: ${e.errorDetails}`));
    };

    this.audioStream = audioStream;
    audioStream.on("data", (chunk: Buffer) => {
      if (this.stopped || !this.pushStream) return;
      const ab = chunk.buffer.slice(
        chunk.byteOffset,
        chunk.byteOffset + chunk.byteLength,
      ) as ArrayBuffer;
      this.pushStream.write(ab);
    });
    audioStream.on("end", () => {
      this.pushStream?.close();
    });
    audioStream.on("error", (err) => {
      this.emit("error", err);
    });

    await new Promise<void>((resolve, reject) => {
      this.recognizer!.startContinuousRecognitionAsync(
        () => resolve(),
        (err) => reject(new Error(err)),
      );
    });
  }

  stop(): void {
    if (this.stopped) return;
    this.stopped = true;

    this.pushStream?.close();
    this.pushStream = null;

    if (this.recognizer) {
      const recognizer = this.recognizer;
      this.recognizer = null;
      recognizer.stopContinuousRecognitionAsync(
        () => recognizer.close(),
        () => recognizer.close(),
      );
    }

    this.audioStream = null;
    this.removeAllListeners();
  }
}
