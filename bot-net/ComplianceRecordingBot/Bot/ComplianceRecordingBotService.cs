using System.Collections.Concurrent;
using ComplianceRecordingBot.Authentication;
using ComplianceRecordingBot.Configuration;
using ComplianceRecordingBot.Utils;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Options;
using Microsoft.Graph.Communications.Calls;
using Microsoft.Graph.Communications.Calls.Media;
using Microsoft.Graph.Communications.Client;
using Microsoft.Graph.Communications.Common.Telemetry;
using Microsoft.Graph.Communications.Resources;
using Microsoft.Skype.Bots.Media;

namespace ComplianceRecordingBot.Bot;

/// <summary>
/// Central bot service. Owns the <see cref="ICommunicationsClient"/> (Graph Communications SDK)
/// and the active call registry.
///
/// Lifetime: singleton. Constructed once at startup; disposes on app shutdown via
/// <see cref="IHostedService"/> (see <see cref="StopAsync"/>).
///
/// The SDK client is built with:
///   - Our <see cref="AuthenticationProvider"/> for inbound JWT validation and outbound
///     Graph Calling auth.
///   - <see cref="MediaPlatformSettings"/> for the local media platform (required by
///     Microsoft.Skype.Bots.Media to establish audio sessions).
///   - <see cref="ICallCollection.OnIncoming"/> subscription to create a
///     <see cref="CallHandler"/> for every new inbound compliance-recording call.
///
/// Usage: inject this service and call <see cref="Client"/> to get the
/// <see cref="ICommunicationsClient"/> used by <see cref="PlatformCallController"/>.
/// </summary>
public class ComplianceRecordingBotService : IHostedService, IDisposable
{
    private readonly BotConfig _botConfig;
    private readonly BackendConfig _backendConfig;
    private readonly ILogger<ComplianceRecordingBotService> _logger;
    private readonly TelemetryClient _telemetry;
    private readonly IServiceProvider _services;

    // Active per-call handlers, keyed by call ID.
    private readonly ConcurrentDictionary<string, CallHandler> _callHandlers = new();

    // The singleton Graph Communications client — null until StartAsync completes.
    private ICommunicationsClient? _client;

    public ICommunicationsClient Client =>
        _client ?? throw new InvalidOperationException("Bot service not started.");

    public ComplianceRecordingBotService(
        IOptions<BotConfig> botConfig,
        IOptions<BackendConfig> backendConfig,
        TelemetryClient telemetry,
        IServiceProvider services,
        ILogger<ComplianceRecordingBotService> logger)
    {
        _botConfig = botConfig.Value;
        _backendConfig = backendConfig.Value;
        _telemetry = telemetry;
        _services = services;
        _logger = logger;
    }

    // ── IHostedService ───────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Starting ComplianceRecordingBotService. AppId={AppId} Cname={Cname}",
            _botConfig.AppId, _botConfig.ServiceCname);

        // Build the Graph Communications logger (wraps ILogger).
        var graphLogger = new GraphLogger(
            component: "ComplianceRecordingBot",
            properties: null,
            redirectToTrace: false);

        // Build the auth provider.
        var authProvider = _services.GetRequiredService<AuthenticationProvider>();

        // Initialize the media platform.
        // NOTE: MediaPlatformSettings requires a real hostname and cert in production.
        // For local dev (no real media session), this is a best-effort init.
        // Session 3 will set up the full media configuration once we have a DevTunnel URL.
        InitializeMediaPlatform(graphLogger);

        // Build the ICommunicationsClient.
        var builder = new CommunicationsClientBuilder(
            appName: "ComplianceRecordingBot",
            appId: _botConfig.AppId,
            logger: graphLogger);

        builder.SetAuthenticationProvider(authProvider);
        builder.SetNotificationUrl(new Uri(_botConfig.CallingWebHookEndpoint));
        builder.SetServiceBaseUrl(new Uri("https://graph.microsoft.com/v1.0"));

        _client = builder.Build();

        // Subscribe to incoming calls.
        _client.Calls().OnIncoming += OnIncomingCall;

        _logger.LogInformation("ICommunicationsClient built and listening for incoming calls.");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping ComplianceRecordingBotService");

        // Dispose all active call handlers.
        foreach (var (callId, handler) in _callHandlers)
        {
            _logger.LogInformation("Disposing CallHandler for call {CallId}", callId);
            try { await handler.DisposeAsync(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing CallHandler {CallId}", callId);
            }
        }
        _callHandlers.Clear();

        // Terminate the client (ends all outstanding calls gracefully).
        if (_client is not null)
        {
            try { await _client.TerminateAsync(TimeSpan.FromSeconds(30)); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error terminating ICommunicationsClient");
            }
        }
    }

    // ── Incoming call handler ────────────────────────────────────────────────

    private void OnIncomingCall(ICallCollection sender, CollectionEventArgs<ICall> args)
    {
        // Answer each newly-arrived call.
        foreach (var call in args.AddedResources)
        {
            _logger.LogInformation("Incoming call. CallId={CallId}", call.Id);
            _telemetry.TrackCallEvent(
                Telemetry.EventCallReceived,
                call.Id,
                new Dictionary<string, string> { ["callId"] = call.Id });

            // Spin up a CallHandler (async fire-and-forget — answer must be fast).
            // Any unhandled exception from AnswerAsync will be logged inside CallHandler.
            var handler = new CallHandler(
                call,
                _botConfig,
                _backendConfig,
                _telemetry,
                _services.GetRequiredService<ILogger<CallHandler>>(),
                _services.GetRequiredService<ILogger<BotMediaStream>>());

            _callHandlers[call.Id] = handler;

            // Remove from registry when the call terminates.
            _ = MonitorCallTerminationAsync(call.Id, handler);
        }
    }

    private async Task MonitorCallTerminationAsync(string callId, CallHandler handler)
    {
        try
        {
            await handler.WaitForTerminationAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CallHandler {CallId} terminated with exception", callId);
        }
        finally
        {
            _callHandlers.TryRemove(callId, out _);
            _telemetry.TrackMetric(
                Telemetry.MetricActiveCalls,
                _callHandlers.Count);
            _logger.LogInformation(
                "CallHandler removed for {CallId}. Active calls: {Count}",
                callId, _callHandlers.Count);
        }
    }

    // ── Media platform init ──────────────────────────────────────────────────

    private void InitializeMediaPlatform(IGraphLogger graphLogger)
    {
        // TODO (Session 3): populate with real IP, port, and TLS cert from config.
        // The MediaPlatform must be initialized with the host's public IP and a TLS
        // cert so the Skype media transport can establish SRTP sessions.
        // For now: guard with a try/catch so the bot can still start for non-media
        // tests (health check, auth rejection check, etc.).
        //
        // Real values needed (from Azure App Service instance or local machine):
        //   InstancePublicIPAddress — the public IP of this app instance
        //   CertificateThumbprint   — a TLS cert registered in the OS cert store
        //   InstancePublicPort      — the HTTPS port (443 for App Service)
        try
        {
            // Skip media platform initialization if not configured.
            // In production, these will be set via App Service config.
            if (string.IsNullOrWhiteSpace(_botConfig.ServiceCname) ||
                _botConfig.ServiceCname.Contains("<your-devtunnel>"))
            {
                _logger.LogWarning(
                    "MediaPlatform initialization skipped: ServiceCname is not set. " +
                    "Set Bot:ServiceCname and Bot:MediaServiceCertSubject to enable real media sessions.");
                return;
            }

            // We do NOT call MediaPlatform.Initialize here because we need the real
            // public IP and cert thumbprint — both runtime/environment values.
            // The actual Initialize call lives in Session 3 once we have those.
            _logger.LogInformation(
                "MediaPlatform init deferred to Session 3 (needs public IP + TLS cert). " +
                "Bot can still receive Graph notifications and return proper HTTP responses.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MediaPlatform initialization failed");
        }
    }

    // ── IDisposable ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        // ICommunicationsClient is disposed via StopAsync in the hosted-service lifecycle.
        _client = null;
    }
}
