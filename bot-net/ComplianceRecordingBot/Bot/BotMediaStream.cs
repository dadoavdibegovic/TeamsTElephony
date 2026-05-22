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
/// TODO (Session 2): Hook AudioMediaReceived / VideoMediaReceived events from IAudioSocket.
///   Subscribe in constructor; unsubscribe in Dispose.
///   For each received AudioMediaBuffer: call _backendWs.SendAudioFrameAsync(speakerTag, pcm).
///   Identify caller vs agent by matching participant identity against the call's participant
///   list (see CallHandler for how agent identity is resolved).
///   Reference: microsoft-graph-comms-samples/.../ComplianceRecordingBot/BotMediaStream.cs
/// </summary>
public class BotMediaStream : IDisposable
{
    private readonly BackendWebSocketClient _backendWs;
    private readonly ILogger<BotMediaStream> _logger;
    private bool _disposed;

    // TODO (Session 2): Accept IAudioSocket (from Microsoft.Skype.Bots.Media) as a parameter.
    // Signature will be:
    //   public BotMediaStream(IAudioSocket audioSocket, BackendWebSocketClient backendWs, ILogger logger)

    public BotMediaStream(BackendWebSocketClient backendWs, ILogger<BotMediaStream> logger)
    {
        _backendWs = backendWs;
        _logger = logger;
    }

    // TODO (Session 2): AudioMediaReceived handler
    // private void OnAudioMediaReceived(object? sender, AudioMediaReceivedEventArgs e) { ... }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // TODO (Session 2): Unsubscribe from audioSocket events here.
    }
}
