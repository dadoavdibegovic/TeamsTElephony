using ComplianceRecordingBot.Configuration;
using ComplianceRecordingBot.Utils;
using Microsoft.ApplicationInsights;
using Microsoft.Graph.Communications.Calls;
using Microsoft.Graph.Communications.Calls.Media;
using Microsoft.Graph.Communications.Resources;
using Microsoft.Graph.Models;
using Microsoft.Skype.Bots.Media;

namespace ComplianceRecordingBot.Bot;

/// <summary>
/// Per-call lifecycle handler. One instance per active call.
///
/// Inherits <see cref="HeartbeatHandler"/> which sends ICall.KeepAliveAsync every 9 minutes
/// to keep the call alive (per spec pitfall #6).
///
/// Lifecycle:
///   1. Constructor: subscribe to call state changes, kick off AnswerAndConnectBackendAsync.
///   2. AnswerAndConnectBackendAsync: create media session, answer the call, connect backend
///      WebSocket, create BotMediaStream attached to the audio socket.
///   3. HeartbeatAsync: called every 9 min — sends KeepAlive to SDK.
///   4. OnCallUpdated: listens for Terminated state → send call.ended, dispose.
///   5. DisposeAsync: cancels heartbeat, disposes media stream and WebSocket.
///
/// Audio architecture:
///   - AudioSocketSettings.ReceiveUnmixedMeetingAudio = true so the SDK delivers
///     separate per-speaker buffers (UnmixedAudioBuffer[]) in each AudioMediaReceived event.
///   - BotMediaStream maps each buffer's ActiveSpeakerId (uint) to a speaker tag (0x00/0x01)
///     and forwards the raw PCM to BackendWebSocketClient.
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
            // ── Step 1: Create media session and answer the call ─────────────────
            // AudioSocketSettings.ReceiveUnmixedMeetingAudio = true enables per-speaker
            // audio buffers in AudioMediaBuffer.UnmixedAudioBuffers. This is required to
            // distinguish caller (PSTN) from agent (Teams user) audio streams.
            var audioSettings = new AudioSocketSettings
            {
                StreamDirections = StreamDirection.Recvonly,
                ReceiveUnmixedMeetingAudio = true,
            };

            // CreateMediaSession is an extension method in Microsoft.Graph.Communications.Calls.Media.
            // It creates an ILocalMediaSession containing the IAudioSocket.
            var mediaSession = _call.CreateMediaSession(
                audioSocketSettings: audioSettings,
                videoSocketSettings: Array.Empty<VideoSocketSettings>(),
                vbssSocketSettings: null,
                dataSocketSettings: null,
                mediaSessionId: Guid.NewGuid());

            // Answer the compliance recording call with our media session.
            // The SDK handles the SDP negotiation; we just provide the socket config.
            await _call.AnswerAsync(
                mediaSession: mediaSession,
                participantCapacity: _botConfig.MediaInstanceCapacity)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Call {CorrelationId} answered with media session.", _correlationId);

            // ── Step 2: Connect the backend WebSocket ────────────────────────────
            var (callerPhone, callerDisplayName, agentUpn) = ExtractParticipantInfo();

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

            // ── Step 3: Attach BotMediaStream to the audio socket ────────────────
            // The agent's MediaSourceId is used by BotMediaStream to tag audio frames 0x01.
            // At this point the participant roster may not be fully populated yet —
            // BotMediaStream falls back to tagging all frames as caller (0x00) until
            // UpdateAgentMediaSourceId is called once the roster resolves.
            uint agentMediaSourceId = ResolveAgentMediaSourceId();

            _mediaStream = new BotMediaStream(
                mediaSession.AudioSocket,
                agentMediaSourceId,
                _backendWs,
                _telemetry,
                _correlationId,
                mediaLogger);

            _logger.LogInformation(
                "BotMediaStream attached to audio socket. CorrelationId={CorrelationId} AgentMediaSourceId={AgentId}",
                _correlationId, agentMediaSourceId);

            // ── Step 4: Subscribe to participant updates to resolve agent MSI ────
            // If we couldn't resolve the agent MediaSourceId at answer time,
            // re-check when the call is updated (roster populated).
            _call.Participants.OnUpdated += OnParticipantsUpdated;
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

    private async void OnCallUpdated(ICall sender, ResourceEventArgs<Call> args)
    {
        var newState = args.NewResource?.State;
        _logger.LogDebug(
            "Call {CorrelationId} state change: {OldState} → {NewState}",
            _correlationId, args.OldResource?.State, newState);

        if (newState == CallState.Terminated)
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

    private void OnParticipantsUpdated(
        IParticipantCollection sender,
        CollectionEventArgs<IParticipant> args)
    {
        // Re-resolve agent media source ID once participants are available.
        if (_mediaStream is null) return;

        var agentMsi = ResolveAgentMediaSourceId();
        if (agentMsi != 0)
        {
            _mediaStream.UpdateAgentMediaSourceId(agentMsi);
            // Unsubscribe once we've resolved — no need to keep re-checking.
            _call.Participants.OnUpdated -= OnParticipantsUpdated;
        }
    }

    // ── HeartbeatHandler ─────────────────────────────────────────────────────

    protected override async Task HeartbeatAsync(CancellationToken cancellationToken)
    {
        if (_call.Resource?.State == CallState.Established)
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

        // Best-effort unsubscribe; may have already been removed in OnParticipantsUpdated.
        try { _call.Participants.OnUpdated -= OnParticipantsUpdated; }
        catch { /* ignore */ }

        _mediaStream?.Dispose();

        if (_backendWs is not null)
        {
            await _backendWs.DisposeAsync().ConfigureAwait(false);
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    // ── Agent MediaSourceId resolution ───────────────────────────────────────

    /// <summary>
    /// Returns the MediaSourceId (uint) of the agent (Teams user) in this call.
    ///
    /// The media SDK delivers unmixed audio frames with an ActiveSpeakerId (uint) that
    /// matches the participant's MSI (Media Source Identifier). We find the MSI for the
    /// Teams agent so BotMediaStream can tag their frames as 0x01.
    ///
    /// The MSI is available on the participant's media streams list. If not yet available
    /// (participants not loaded), returns 0 as a sentinel — BotMediaStream will tag all
    /// frames as caller (0x00) until UpdateAgentMediaSourceId is called.
    /// </summary>
    private uint ResolveAgentMediaSourceId()
    {
        try
        {
            var participants = _call.Participants;
            if (participants is null) return 0;

            foreach (var p in participants)
            {
                var identity = p.Resource?.Info?.Identity;
                if (identity is null) continue;

                // Skip the bot itself.
                if (identity.Application?.Id == _botConfig.AppId) continue;

                // Skip PSTN callers.
                var commsIdentity = identity as CommunicationsIdentitySet;
                if (commsIdentity?.Phone is not null) continue;

                // First Teams user found = agent.
                if (identity.User is not null)
                {
                    // The MSI is in the participant's MediaStreams.
                    // MediaStream.SourceId is the uint MediaSourceId the media SDK uses.
                    var audioStream = p.Resource?.MediaStreams?
                        .FirstOrDefault(ms => ms.MediaType == Modality.Audio);

                    if (audioStream?.SourceId is not null &&
                        uint.TryParse(audioStream.SourceId, out var msi))
                    {
                        _logger.LogInformation(
                            "Resolved agent MediaSourceId={Msi} for call {CorrelationId}",
                            msi, _correlationId);
                        return msi;
                    }

                    // Participant found but MSI not yet populated; caller will retry.
                    _logger.LogDebug(
                        "Agent participant found but MSI not yet available for {CorrelationId}.",
                        _correlationId);
                    return 0;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve agent MediaSourceId for {CorrelationId}", _correlationId);
        }

        _logger.LogDebug(
            "Agent participant not found yet for {CorrelationId}. Will retry on participant update.",
            _correlationId);
        return 0;
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
