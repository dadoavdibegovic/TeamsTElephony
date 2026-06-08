namespace ComplianceRecordingBot.Bot;

/// <summary>
/// Base class for per-call handlers that need to send periodic heartbeats.
///
/// The Graph Communications SDK will terminate a call that doesn't receive
/// a KeepAlive within roughly 10 minutes (pitfall #6 in bot-implementation-guide.md).
/// This class wraps a timer that fires the <see cref="HeartbeatAsync"/> callback at
/// the requested interval.
///
/// Subclasses override <see cref="HeartbeatAsync"/> to call
/// <see cref="Microsoft.Graph.Communications.Calls.ICall.KeepAliveAsync"/>.
/// </summary>
public abstract class HeartbeatHandler : IAsyncDisposable
{
    private readonly Timer _timer;
    private readonly ILogger _logger;
    private int _disposed;

    protected HeartbeatHandler(TimeSpan interval, ILogger logger)
    {
        _logger = logger;
        _timer = new Timer(
            callback: _ => FireHeartbeatAsync(),
            state: null,
            dueTime: interval,
            period: interval);
    }

    /// <summary>
    /// Called every <c>interval</c>. Subclass sends KeepAlive to the SDK.
    /// </summary>
    protected abstract Task HeartbeatAsync(CancellationToken cancellationToken);

    private void FireHeartbeatAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 0, 0) != 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await HeartbeatAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Heartbeat failed");
            }
        });
    }

    public virtual async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _timer.DisposeAsync();
    }
}
