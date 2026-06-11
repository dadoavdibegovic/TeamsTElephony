using System.Runtime.InteropServices;
using Microsoft.ApplicationInsights;
using Microsoft.Skype.Bots.Media;

namespace ComplianceRecordingBot.Bot;

/// <summary>
/// Audio sink that receives raw PCM buffers from the Graph Communications media SDK
/// and forwards them to the Node.js backend via <see cref="BackendWebSocketClient"/>.
///
/// Speaker tagging:
///   0x00 = caller  (PSTN-side participant)
///   0x01 = agent   (Teams user with recording policy)
///
/// Audio format delivered by the SDK: 16 kHz, 16-bit signed LE, mono.
/// We pass through without resampling — this matches exactly what the backend expects.
///
/// The IAudioSocket is configured with ReceiveUnmixedMeetingAudio = true so that
/// AudioMediaBuffer.UnmixedAudioBuffers is populated with one entry per active speaker.
/// Each UnmixedAudioBuffer has an ActiveSpeakerId (uint) and a native Data pointer.
///
/// Critical invariant: e.Buffer.Dispose() MUST be called in the finally block of
/// every AudioMediaReceived handler. Failing to do so causes the media SDK to exhaust
/// its native buffer pool and stop delivering audio within seconds.
///
/// Note on 1:1 calls: if ReceiveUnmixedMeetingAudio is true but the SDK delivers
/// a null UnmixedAudioBuffers (e.g., mixed-audio fallback), we fall back to the
/// mixed buffer tagged as caller (0x00). This should not happen in a properly
/// configured compliance recording call but is handled defensively.
/// </summary>
public class BotMediaStream : IDisposable
{
    private readonly IAudioSocket _audioSocket;
    private readonly BackendWebSocketClient _backendWs;
    private readonly TelemetryClient _telemetry;
    private readonly string _correlationId;
    private readonly ILogger<BotMediaStream> _logger;

    // MediaSourceId (uint) of the agent (Teams user); 0 means unknown.
    // Set after call participants resolve; compared against UnmixedAudioBuffer.ActiveSpeakerId.
    private uint _agentMediaSourceId;

    private bool _disposed;
    private int _frameCount;
    private bool _firstFrameSent;

    // Rolling latency window for p95 metric (100 frames).
    private readonly long[] _latencyWindow = new long[100];
    private int _latencyWindowIdx;

    /// <summary>
    /// Creates a BotMediaStream and subscribes to the audio socket's AudioMediaReceived event.
    /// </summary>
    /// <param name="audioSocket">
    ///   The IAudioSocket from ILocalMediaSession.AudioSocket.
    ///   Must be configured with AudioSocketSettings.ReceiveUnmixedMeetingAudio = true.
    /// </param>
    /// <param name="agentMediaSourceId">
    ///   The MediaSourceId (uint) of the Teams agent participant.
    ///   Frames from this source are tagged 0x01; all others are tagged 0x00.
    ///   Pass 0 if not yet known — BotMediaStream falls back to tagging all as caller.
    ///   Call <see cref="UpdateAgentMediaSourceId"/> once participants resolve.
    /// </param>
    /// <param name="backendWs">The WebSocket client to forward audio frames to.</param>
    /// <param name="telemetry">Application Insights telemetry client.</param>
    /// <param name="correlationId">The call's correlation ID, used in telemetry.</param>
    /// <param name="logger">Logger for this instance.</param>
    public BotMediaStream(
        IAudioSocket audioSocket,
        uint agentMediaSourceId,
        BackendWebSocketClient backendWs,
        TelemetryClient telemetry,
        string correlationId,
        ILogger<BotMediaStream> logger)
    {
        _audioSocket = audioSocket;
        _agentMediaSourceId = agentMediaSourceId;
        _backendWs = backendWs;
        _telemetry = telemetry;
        _correlationId = correlationId;
        _logger = logger;

        _audioSocket.AudioMediaReceived += OnAudioMediaReceived;
        _logger.LogInformation(
            "BotMediaStream subscribed to audio socket. CorrelationId={CorrelationId} AgentMediaSourceId={AgentId}",
            _correlationId, _agentMediaSourceId);
    }

    /// <summary>
    /// Updates the agent's MediaSourceId once the participant roster resolves.
    /// Called by CallHandler when the participant list becomes available.
    /// Thread-safe (volatile write; worst case is one mistagged frame during transition).
    /// </summary>
    public void UpdateAgentMediaSourceId(uint mediaSourceId)
    {
        _agentMediaSourceId = mediaSourceId;
        _logger.LogInformation(
            "Agent MediaSourceId updated. CorrelationId={CorrelationId} MediaSourceId={Id}",
            _correlationId, mediaSourceId);
    }

    // ── Audio callback (fires on the media SDK serialization queue) ──────────

    private void OnAudioMediaReceived(object? sender, AudioMediaReceivedEventArgs e)
    {
        if (_disposed)
        {
            // Still must dispose the buffer or we leak native memory.
            e.Buffer.Dispose();
            return;
        }

        var receivedAtTicks = DateTime.UtcNow.Ticks;
        try
        {
            // ── Unmixed buffers (preferred: ReceiveUnmixedMeetingAudio = true) ───
            // Each UnmixedAudioBuffer represents one active speaker.
            // We iterate and send one WS frame per speaker per tick.
            var unmixed = e.Buffer.UnmixedAudioBuffers;
            if (unmixed is not null && unmixed.Length > 0)
            {
                foreach (var ub in unmixed)
                {
                    byte speakerTag = (ub.ActiveSpeakerId == _agentMediaSourceId && _agentMediaSourceId != 0)
                        ? (byte)0x01   // agent
                        : (byte)0x00;  // caller

                    if (ub.Length > 0 && ub.Data != nint.Zero)
                    {
                        var pcm = CopyNativeBuffer(ub.Data, (int)ub.Length);
                        _ = _backendWs.SendAudioFrameAsync(speakerTag, pcm, CancellationToken.None);
                        TrackFirstFrameOnce(speakerTag);
                    }
                }
            }
            else
            {
                // ── Mixed buffer fallback (1:1 call or unmixed not configured) ───
                // If UnmixedAudioBuffers is null/empty, the mixed buffer in e.Buffer.Data
                // contains audio from all participants. We tag it as caller (0x00) since
                // in a compliance recording scenario the mixed audio primarily represents
                // the non-agent side.
                if (e.Buffer.Length > 0 && e.Buffer.Data != nint.Zero)
                {
                    var pcm = CopyNativeBuffer(e.Buffer.Data, (int)e.Buffer.Length);
                    _ = _backendWs.SendAudioFrameAsync(0x00, pcm, CancellationToken.None);
                    TrackFirstFrameOnce(0x00);
                }
            }

            TrackLatency(receivedAtTicks);
            _frameCount++;
        }
        finally
        {
            // CRITICAL: Must be called unconditionally. The media SDK requires explicit
            // disposal of every buffer or it runs out of native buffers within seconds.
            e.Buffer.Dispose();
        }
    }

    // ── Native buffer copy ───────────────────────────────────────────────────

    /// <summary>
    /// Copies a native (unmanaged) audio buffer into a managed byte array.
    /// AudioMediaBuffer.Data and UnmixedAudioBuffer.Data are nint (native pointer).
    /// Marshal.Copy is the correct way to read native memory into managed arrays.
    /// </summary>
    private static byte[] CopyNativeBuffer(nint data, int length)
    {
        var buffer = new byte[length];
        Marshal.Copy(data, buffer, 0, length);
        return buffer;
    }

    // ── Telemetry helpers ────────────────────────────────────────────────────

    private void TrackFirstFrameOnce(byte speakerTag)
    {
        if (_firstFrameSent) return;
        _firstFrameSent = true;
        _telemetry.TrackEvent(
            Utils.Telemetry.EventAudioFirstFrameSent,
            new Dictionary<string, string>
            {
                ["correlationId"] = _correlationId,
                ["speakerTag"] = speakerTag.ToString(),
            });
        _logger.LogInformation(
            "First audio frame sent. CorrelationId={CorrelationId} SpeakerTag={SpeakerTag}",
            _correlationId, speakerTag);
    }

    private void TrackLatency(long receivedAtTicks)
    {
        var latencyMs = (DateTime.UtcNow.Ticks - receivedAtTicks) / TimeSpan.TicksPerMillisecond;
        _latencyWindow[_latencyWindowIdx % 100] = latencyMs;
        _latencyWindowIdx++;

        if (_latencyWindowIdx % 100 == 0)
        {
            // Emit p95 for the last 100 frames.
            var sorted = _latencyWindow.OrderBy(x => x).ToArray();
            var p95 = sorted[94]; // index 94 = 95th percentile of 100 samples
            _telemetry.TrackMetric(Utils.Telemetry.MetricAudioForwardP95Ms, p95);
        }
    }

    // ── IDisposable ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _audioSocket.AudioMediaReceived -= OnAudioMediaReceived;
        _logger.LogInformation(
            "BotMediaStream disposed. CorrelationId={CorrelationId} TotalFrames={FrameCount}",
            _correlationId, _frameCount);
    }
}
