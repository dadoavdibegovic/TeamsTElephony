using ComplianceRecordingBot.Configuration;
using ComplianceRecordingBot.Utils;
using Microsoft.ApplicationInsights;
using Microsoft.Graph.Communications.Calls;
using Microsoft.Graph.Communications.Resources;
using Microsoft.Graph.Models;

namespace ComplianceRecordingBot.Bot;

/// <summary>
/// Per-call lifecycle handler. One instance per active call.
///
/// Inherits <see cref="HeartbeatHandler"/> which sends ICall.KeepAliveAsync every 9 minutes
/// to keep the call alive (per spec pitfall #6).
///
/// Lifecycle:
///   1. Constructor: subscribe to call state changes, connect backend WebSocket, send call.started.
///   2. <see cref="HeartbeatAsync"/>: called every 9 min — sends KeepAlive to SDK.
///   3. <see cref="OnCallUpdated"/>: listens for Terminated state → send call.ended, dispose.
///   4. <see cref="DisposeAsync"/>: cancels heartbeat, disposes media stream and WebSocket.
///
/// Audio forwarding (BotMediaStream → BackendWebSocketClient) is hooked here but the
/// actual byte-level forwarding is a TODO for Session 3 (see BotMediaStream).
/// </summary>
public class CallHandler : HeartbeatHandler
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(9);

    private readonly ICall _call;
    private readonly BotConfig _botConfig;
    private readonly BackendConfig _backendConfig;
    private readonly TelemetryClient _telemetry;
    private readonly ILogger<CallHandler> _logger;
    private readonly string _correlationId;

    private BackendWebSocketClient? _backendWs;
    private BotMediaStream? _mediaStream;
    private readonly DateTime _receivedAt = DateTime.UtcNow;

    // TaskCompletionSource so ComplianceRecordingBotService can await termination.
    private readonly TaskCompletionSource _terminated = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string CorrelationId => _correlationId;

    public CallHandler(
        ICall call,
        BotConfig botConfig,
        BackendConfig backendConfig,
        TelemetryClient telemetry,
        ILogger<CallHandler> logger,
        ILogger<BotMediaStream> mediaLogger)
        : base(HeartbeatInterval, logger)
    {
        _call = call;
        _botConfig = botConfig;
        _backendConfig = backendConfig;
        _telemetry = telemetry;
        _logger = logger;
        _correlationId = call.Id;

        // Subscribe to call state changes.
        _call.OnUpdated += OnCallUpdated;

        // Kick off async answer + backend connect. Fire-and-forget; exceptions logged inside.
        _ = AnswerAndConnectBackendAsync(mediaLogger);
    }

    // ── Call answer and backend connection ───────────────────────────────────

    private async Task AnswerAndConnectBackendAsync(ILogger<BotMediaStream> mediaLogger)
    {
        try
        {
            // Identify caller and agent from participants.
            var (callerPhone, callerDisplayName, agentUpn) = ExtractParticipantInfo();

            // Connect the backend WebSocket and send call.started first.
            _backendWs = new BackendWebSocketClient(
                _backendConfig.IngestWss,
                _correlationId,
                _backendConfig.IngestSecret);

            _logger.LogInformation(
                "Connecting backend WebSocket for call {CorrelationId}", _correlationId);

            await _backendWs.ConnectAsync().ConfigureAwait(false);

            _telemetry.TrackCallEvent(Telemetry.EventBackendWsConnected, _correlationId);

            await _backendWs.SendCallStartedAsync(callerPhone, callerDisplayName, agentUpn)
                .ConfigureAwait(false);

            // Measure join latency (notification arrival → call.started sent).
            var joinLatencyMs = (DateTime.UtcNow - _receivedAt).TotalMilliseconds;
            _telemetry.TrackCallMetric(Telemetry.MetricJoinLatencyMs, joinLatencyMs, _correlationId);

            _telemetry.TrackCallEvent(
                Telemetry.EventCallJoined,
                _correlationId,
                new Dictionary<string, string>
                {
                    ["callerPhone"] = callerPhone ?? "",
                    ["agentUpn"] = agentUpn ?? "",
                    ["joinLatencyMs"] = joinLatencyMs.ToString("F0"),
                });

            _logger.LogInformation(
                "Call {CorrelationId} backend connected. JoinLatency={JoinLatencyMs}ms",
                _correlationId, joinLatencyMs);

            // Attach BotMediaStream to the call's audio socket.
            // TODO (Session 3): pass the real IAudioSocket from _call.GetLocalMediaSession().AudioSocket
            // once MediaPlatform is initialized with a real public IP + cert.
            // For now: create BotMediaStream without audio socket so it compiles and is wired.
            _mediaStream = new BotMediaStream(_backendWs, mediaLogger);

            _logger.LogInformation(
                "BotMediaStream created for call {CorrelationId}. " +
                "Audio socket attachment is TODO for Session 3.", _correlationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to answer call or connect backend for {CorrelationId}", _correlationId);
            _telemetry.TrackCallEvent(
                Telemetry.EventCallJoinFailed,
                _correlationId,
                new Dictionary<string, string> { ["error"] = ex.Message });

            // Try to notify the backend of the error so it can clean up.
            if (_backendWs is not null)
            {
                try
                {
                    await _backendWs.SendCallErrorAsync(ex.Message, "join_failed")
                        .ConfigureAwait(false);
                }
                catch { /* best-effort */ }
            }

            _terminated.TrySetException(ex);
        }
    }

    // ── Call state listener ──────────────────────────────────────────────────

    private async void OnCallUpdated(ICall sender, ResourceEventArgs<Microsoft.Graph.Models.Call> args)
    {
        var newState = args.NewResource?.State;
        _logger.LogDebug(
            "Call {CorrelationId} state change: {OldState} → {NewState}",
            _correlationId, args.OldResource?.State, newState);

        if (newState == Microsoft.Graph.Models.CallState.Terminated)
        {
            _logger.LogInformation("Call {CorrelationId} terminated.", _correlationId);
            _telemetry.TrackCallEvent(Telemetry.EventCallLeft, _correlationId);

            try
            {
                if (_backendWs is not null)
                {
                    await _backendWs.SendCallEndedAsync("normal").ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error sending call.ended for {CorrelationId}", _correlationId);
            }

            _terminated.TrySetResult();
            await DisposeAsync().ConfigureAwait(false);
        }
    }

    // ── HeartbeatHandler ─────────────────────────────────────────────────────

    protected override async Task HeartbeatAsync(CancellationToken cancellationToken)
    {
        if (_call.Resource?.State == Microsoft.Graph.Models.CallState.Established)
        {
            _logger.LogDebug("Sending KeepAlive for call {CorrelationId}", _correlationId);
            await _call.KeepAliveAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // ── Await termination ────────────────────────────────────────────────────

    /// <summary>
    /// Returns a Task that completes when the call is terminated.
    /// Used by <see cref="ComplianceRecordingBotService"/> to clean up the call registry.
    /// </summary>
    public Task WaitForTerminationAsync() => _terminated.Task;

    // ── IAsyncDisposable ─────────────────────────────────────────────────────

    public override async ValueTask DisposeAsync()
    {
        _call.OnUpdated -= OnCallUpdated;

        _mediaStream?.Dispose();

        if (_backendWs is not null)
        {
            await _backendWs.DisposeAsync().ConfigureAwait(false);
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    // ── Participant helpers ───────────────────────────────────────────────────

    private (string? callerPhone, string? callerDisplayName, string? agentUpn)
        ExtractParticipantInfo()
    {
        // The Participants collection may not be populated yet when the call arrives.
        // We do a best-effort extraction; the backend tolerates null values.

        string? callerPhone = null;
        string? callerDisplayName = null;
        string? agentUpn = null;

        try
        {
            var participants = _call.Participants;
            if (participants is null) return (null, null, null);

            foreach (var p in participants)
            {
                // ParticipantInfo.Identity is IdentitySet; for calls it is actually
                // CommunicationsIdentitySet which adds Phone (and other calling fields).
                var baseIdentity = p.Resource?.Info?.Identity;
                if (baseIdentity is null) continue;

                var commsIdentity = baseIdentity as CommunicationsIdentitySet;

                // Bot itself — skip by application ID.
                if (baseIdentity.Application?.Id == _botConfig.AppId)
                    continue;

                // PSTN caller — CommunicationsIdentitySet.Phone is non-null for PSTN.
                if (commsIdentity?.Phone is not null)
                {
                    callerPhone = commsIdentity.Phone.Id;
                    object? dnObj = null;
                    baseIdentity.AdditionalData?.TryGetValue("displayName", out dnObj);
                    callerDisplayName = dnObj as string;
                    continue;
                }

                // Teams user — take as agent (first non-bot, non-PSTN participant).
                if (baseIdentity.User is not null && agentUpn is null)
                {
                    object? upnObj = null;
                    baseIdentity.User.AdditionalData?.TryGetValue("userPrincipalName", out upnObj);
                    agentUpn = upnObj as string;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract participant info for {CorrelationId}", _correlationId);
        }

        return (callerPhone, callerDisplayName, agentUpn);
    }
}
