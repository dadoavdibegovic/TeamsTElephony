// TODO (Session 3): Uncomment when MediaPlatform is initialized (requires public IP + TLS cert).
// using Microsoft.Skype.Bots.Media;

namespace ComplianceRecordingBot.Bot;

/// <summary>
/// Audio sink that receives raw PCM buffers from the Graph Communications media SDK
/// and forwards them to the Node.js backend via <see cref="BackendWebSocketClient"/>.
///
/// Speaker tagging:
///   0x00 = caller  (PSTN-side participant)
///   0x01 = agent   (Teams user with recording policy)
///
/// This class replaces the .wav file write in Microsoft's Compliance Recording sample.
/// It is attached to the IAudioSocket provided by ICall.GetLocalMediaSession().AudioSocket.
///
/// Session 3 TODO:
///   - Add IAudioSocket parameter to constructor
///   - Subscribe to IAudioSocket.AudioMediaReceived
///   - In handler: extract PCM bytes, tag speaker, call _backendWs.SendAudioFrameAsync
///   - Unsubscribe from IAudioSocket in Dispose()
///   - Track bot_audio_first_frame_sent telemetry on first frame
///   - Track bot_audio_forward_p95_ms latency metric per 100 frames
///
/// Why deferred: IAudioSocket requires MediaPlatform.Initialize() which needs the
/// real public IP and a valid TLS certificate — both available only when running in
/// Azure App Service (Session 3). Local dev without DevTunnel has no valid IP/cert.
///
/// Reference: microsoft-graph-comms-samples/.../ComplianceRecordingBot/BotMediaStream.cs
///   Replace the .wav StreamWriter with the BackendWebSocketClient send calls shown in
///   docs/bot-implementation-guide.md §5.
/// </summary>
public class BotMediaStream : IDisposable
{
    // TODO (Session 3): private readonly IAudioSocket _audioSocket;
    private readonly BackendWebSocketClient _backendWs;
    private readonly ILogger<BotMediaStream> _logger;
    private bool _disposed;

    // TODO (Session 3): Add TelemetryClient + _firstFrameSent counter for bot_audio_first_frame_sent event.

    /// <summary>
    /// Creates a BotMediaStream. The IAudioSocket parameter is deferred to Session 3
    /// once MediaPlatform is fully initialized.
    /// </summary>
    /// <param name="backendWs">The WebSocket client to forward audio frames to.</param>
    /// <param name="logger">Logger for this instance.</param>
    public BotMediaStream(BackendWebSocketClient backendWs, ILogger<BotMediaStream> logger)
    {
        _backendWs = backendWs;
        _logger = logger;

        _logger.LogInformation(
            "BotMediaStream created. Audio socket subscription deferred to Session 3.");
    }

    // TODO (Session 3): Upgrade constructor signature to:
    //   public BotMediaStream(IAudioSocket audioSocket, BackendWebSocketClient backendWs,
    //                         TelemetryClient telemetry, string correlationId, ILogger<BotMediaStream> logger)
    //
    //   Then subscribe:
    //     _audioSocket = audioSocket;
    //     _audioSocket.AudioMediaReceived += OnAudioMediaReceived;
    //
    // TODO (Session 3): Implement:
    //   private void OnAudioMediaReceived(object? sender, AudioMediaReceivedEventArgs e)
    //   {
    //       if (_disposed) return;
    //       try
    //       {
    //           // e.Buffer.Data is the PCM payload (16 kHz, 16-bit signed LE, mono).
    //           // e.Buffer.ActiveSpeakerId identifies the participant.
    //           // Map ActiveSpeakerId to 0x00 (caller) or 0x01 (agent) based on call participant list.
    //           byte speakerTag = ResolveTag(e.Buffer.ActiveSpeakerId);
    //           var pcm = new ReadOnlyMemory<byte>(e.Buffer.Data, 0, (int)e.Buffer.Length);
    //           _ = _backendWs.SendAudioFrameAsync(speakerTag, pcm, CancellationToken.None);
    //           TrackFirstFrameOnce();
    //       }
    //       finally
    //       {
    //           e.Buffer.Dispose(); // REQUIRED by the media SDK — buffers must be returned.
    //       }
    //   }
    //
    // WARNING: e.Buffer.Dispose() is MANDATORY. Failing to call it causes the media SDK
    //          to run out of buffers and stop delivering audio within seconds. This is the
    //          single most common bug in compliance recording bots.

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // TODO (Session 3): _audioSocket.AudioMediaReceived -= OnAudioMediaReceived;
        _logger.LogInformation("BotMediaStream disposed.");
    }
}
