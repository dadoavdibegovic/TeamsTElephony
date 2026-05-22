using ComplianceRecordingBot.Configuration;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Options;

namespace ComplianceRecordingBot.Bot;

/// <summary>
/// Per-call lifecycle handler. One instance per active call.
///
/// Responsibilities:
///   - Identify the recorded agent and the caller from the call's participant list
///   - Open a <see cref="BackendWebSocketClient"/> and send call.started
///   - Wire up <see cref="BotMediaStream"/> to receive audio and forward it
///   - Handle call state changes (established, terminated)
///   - Emit Application Insights telemetry events per spec
///   - Clean up WS connection and media stream on call end
///
/// TODO (Session 2): Inherit from HeartbeatHandler (required by Graph Communications SDK
///   to keep the call alive — see pitfall #6 in bot-implementation-guide.md).
///   Constructor should accept ICall (from Microsoft.Graph.Communications.Calls).
///   Wire ICall.OnUpdated event and ICall.Participants events.
///
/// TODO (Session 2): Full implementation following the reference sample at
///   microsoft-graph-comms-samples/.../ComplianceRecordingBot/Bot/CallHandler.cs
///   with the wav-write replaced by BackendWebSocketClient.SendAudioFrameAsync.
/// </summary>
public class CallHandler : IAsyncDisposable
{
    // TODO (Session 2): Replace stub fields with real ICall, BotMediaStream, BackendWebSocketClient.
    private readonly string _correlationId;
    private readonly ILogger<CallHandler> _logger;
    private readonly TelemetryClient _telemetry;
    private readonly BotConfig _botConfig;
    private readonly BackendConfig _backendConfig;

    public string CorrelationId => _correlationId;

    public CallHandler(
        string correlationId,
        IOptions<BotConfig> botConfig,
        IOptions<BackendConfig> backendConfig,
        TelemetryClient telemetry,
        ILogger<CallHandler> logger)
    {
        _correlationId = correlationId;
        _botConfig = botConfig.Value;
        _backendConfig = backendConfig.Value;
        _telemetry = telemetry;
        _logger = logger;
    }

    // TODO (Session 2): Add OnCallUpdated, OnParticipantsUpdated handlers.
    // TODO (Session 2): Add audio stream setup after bot joins call.

    public async ValueTask DisposeAsync()
    {
        // TODO (Session 2): Send call.ended to backend, dispose BackendWebSocketClient and BotMediaStream.
        await Task.CompletedTask;
    }
}
