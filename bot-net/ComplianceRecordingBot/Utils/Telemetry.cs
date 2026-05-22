using Microsoft.ApplicationInsights;

namespace ComplianceRecordingBot.Utils;

/// <summary>
/// Telemetry helpers. Wraps TelemetryClient with the event / metric names
/// defined in docs/bot-contractor-spec.md §"Telemetry expectations".
///
/// Every custom event and metric emits correlationId as a property so all
/// call-related signals can be correlated in Application Insights / KQL.
/// </summary>
public static class Telemetry
{
    // ── Custom event names ────────────────────────────────────────────────────

    public const string EventCallReceived          = "bot_call_received";
    public const string EventCallJoined            = "bot_call_joined";
    public const string EventCallJoinFailed        = "bot_call_join_failed";
    public const string EventBackendWsConnected    = "bot_backend_ws_connected";
    public const string EventBackendWsFailed       = "bot_backend_ws_failed";
    public const string EventAudioFirstFrameSent   = "bot_audio_first_frame_sent";
    public const string EventCallLeft              = "bot_call_left";
    public const string EventCallError             = "bot_call_error";

    // ── Custom metric names ───────────────────────────────────────────────────

    public const string MetricJoinLatencyMs        = "bot_join_latency_ms";
    public const string MetricFirstFrameLatencyMs  = "bot_first_frame_latency_ms";
    public const string MetricAudioForwardP95Ms    = "bot_audio_forward_p95_ms";
    public const string MetricActiveCalls          = "bot_active_calls";

    // ── Helper methods ────────────────────────────────────────────────────────

    /// <summary>Tracks a custom event with correlationId and optional extra properties.</summary>
    public static void TrackCallEvent(
        this TelemetryClient client,
        string eventName,
        string correlationId,
        IDictionary<string, string>? extraProperties = null)
    {
        var props = new Dictionary<string, string> { ["correlationId"] = correlationId };
        if (extraProperties is not null)
        {
            foreach (var kv in extraProperties)
                props[kv.Key] = kv.Value;
        }
        client.TrackEvent(eventName, props);
    }

    /// <summary>Tracks a latency metric with correlationId.</summary>
    public static void TrackCallMetric(
        this TelemetryClient client,
        string metricName,
        double valueMs,
        string correlationId)
    {
        client.TrackMetric(metricName, valueMs,
            new Dictionary<string, string> { ["correlationId"] = correlationId });
    }
}
