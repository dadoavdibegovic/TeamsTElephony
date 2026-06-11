using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace ComplianceRecordingBot.Bot;

/// <summary>
/// Manages the WebSocket connection to the Node.js audio-ingest backend for a single call.
///
/// Wire protocol (defined in docs/bot-contractor-spec.md §"Protocol you implement"):
///   1. On connect: send JSON text frame  { type: "call.started", ... }
///   2. Per audio buffer: send binary frame  [1 byte speakerTag][N bytes PCM 16kHz/16bit/mono]
///      speakerTag 0x00 = caller (PSTN), 0x01 = agent (Teams user)
///   3. On call end: send JSON text frame  { type: "call.ended", ... }  then close.
///   4. On error: send JSON text frame  { type: "call.error", ... }    then close.
///
/// One instance per call. Do NOT share across calls.
/// Constructed and owned by CallHandler; disposed when the call terminates.
///
/// TODO (Session 2): Implement ConnectAsync, SendAudioFrameAsync, reconnect logic,
///   back-pressure handling for slow backend (channel-based queue or drop-with-metric).
/// </summary>
public class BackendWebSocketClient : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly Uri _endpoint;
    private readonly string _correlationId;
    private readonly CancellationTokenSource _cts = new();

    // TODO (Session 2): Add TelemetryClient injection for per-frame latency metrics.

    public BackendWebSocketClient(string baseWss, string correlationId, string bearer)
    {
        _correlationId = correlationId;
        _endpoint = new Uri($"{baseWss.TrimEnd('/')}/{Uri.EscapeDataString(correlationId)}");
        _ws.Options.SetRequestHeader("Authorization", $"Bearer {bearer}");
    }

    /// <summary>Opens the WebSocket to the backend.</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _ws.ConnectAsync(_endpoint, cancellationToken);
    }

    /// <summary>Sends the call.started metadata frame (must be sent before any audio).</summary>
    public async Task SendCallStartedAsync(
        string? callerPhone,
        string? callerDisplayName,
        string? agentUpn,
        CancellationToken cancellationToken = default)
    {
        var msg = new
        {
            type = "call.started",
            correlationId = _correlationId,
            callerPhone,
            callerDisplayName,
            agentUpn,
            startedAt = DateTime.UtcNow.ToString("o"),
        };
        await SendTextAsync(msg, cancellationToken);
    }

    /// <summary>
    /// Forwards a PCM audio buffer to the backend.
    /// speakerTag: 0x00 = caller, 0x01 = agent.
    /// pcm: 16 kHz, 16-bit signed LE, mono — pass through without resampling.
    /// </summary>
    public async Task SendAudioFrameAsync(
        byte speakerTag,
        ReadOnlyMemory<byte> pcm,
        CancellationToken cancellationToken = default)
    {
        // Layout: [1 byte speakerTag][N bytes PCM]
        var buffer = new byte[1 + pcm.Length];
        buffer[0] = speakerTag;
        pcm.CopyTo(buffer.AsMemory(1));
        await _ws.SendAsync(buffer, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);
    }

    /// <summary>Sends call.ended and closes the WebSocket cleanly.</summary>
    public async Task SendCallEndedAsync(
        string reason = "normal",
        CancellationToken cancellationToken = default)
    {
        var msg = new
        {
            type = "call.ended",
            correlationId = _correlationId,
            endedAt = DateTime.UtcNow.ToString("o"),
            reason,
        };

        if (_ws.State == WebSocketState.Open)
        {
            await SendTextAsync(msg, cancellationToken);
            await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "call.ended", cancellationToken);
        }
    }

    /// <summary>Sends call.error and closes the WebSocket.</summary>
    public async Task SendCallErrorAsync(
        string message,
        string code,
        CancellationToken cancellationToken = default)
    {
        var msg = new
        {
            type = "call.error",
            correlationId = _correlationId,
            message,
            code,
        };

        if (_ws.State == WebSocketState.Open)
        {
            await SendTextAsync(msg, cancellationToken);
            await _ws.CloseAsync(WebSocketCloseStatus.InternalServerError, "call.error", cancellationToken);
        }
    }

    private async Task SendTextAsync(object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_ws.State == WebSocketState.Open || _ws.State == WebSocketState.CloseReceived)
        {
            try
            {
                await _ws.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "disposing",
                    CancellationToken.None);
            }
            catch { /* best-effort */ }
        }
        _ws.Dispose();
        _cts.Dispose();
    }
}
