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
        // Skip if ServiceCname is unset or still a placeholder.
        if (string.IsNullOrWhiteSpace(_botConfig.ServiceCname) ||
            _botConfig.ServiceCname.Contains("<"))
        {
            _logger.LogWarning(
                "MediaPlatform initialization skipped: Bot:ServiceCname is not configured. " +
                "Set Bot__ServiceCname in App Service settings to the deployed hostname.");
            return;
        }

        try
        {
            // ── Resolve public IP ────────────────────────────────────────────────
            // App Service Linux does not expose the instance's public IP as an environment
            // variable. We resolve it via DNS at startup. The Graph Communications SDK
            // requires a real IPAddress in MediaPlatformInstanceSettings.
            // This runs synchronously at startup (blocking is acceptable here — we're in
            // IHostedService.StartAsync, called once before the app accepts traffic).
            var addresses = System.Net.Dns.GetHostAddresses(_botConfig.ServiceCname);
            var publicIp = addresses.FirstOrDefault(a =>
                a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

            if (publicIp is null)
            {
                _logger.LogWarning(
                    "MediaPlatform initialization skipped: could not resolve IPv4 for {Cname}. " +
                    "Bot will handle Graph notifications but media sessions won't work until " +
                    "DNS resolves to a reachable IPv4 address.",
                    _botConfig.ServiceCname);
                return;
            }

            _logger.LogInformation(
                "MediaPlatform resolved public IP {Ip} for {Cname}",
                publicIp, _botConfig.ServiceCname);

            // ── Load cert (search order — first non-null wins) ───────────────────
            //
            //   0. Bot:MediaCertPfxBase64 (KV ref) — most portable, bypasses cert store entirely.
            //      No thumbprint needed; thumbprint is read from the loaded cert object.
            //   1. /var/ssl/private/<THUMBPRINT>.p8 — Linux App Service WEBSITE_LOAD_CERTIFICATES
            //   2. CurrentUser\My X509 store
            //   3. LocalMachine\My X509 store (Windows only)
            //
            // Paths 1-3 require a thumbprint. Path 0 does not.
            System.Security.Cryptography.X509Certificates.X509Certificate2? cert = null;

            // 0. KV-stored PFX base64 — primary path, works on Linux and Windows
            if (!string.IsNullOrWhiteSpace(_botConfig.MediaCertPfxBase64))
            {
                try
                {
                    var pfxBytes = Convert.FromBase64String(_botConfig.MediaCertPfxBase64);
                    cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(
                        pfxBytes,
                        _botConfig.MediaCertPfxPassword,
                        System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.MachineKeySet |
                        System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.Exportable);
                    _logger.LogInformation(
                        "MediaPlatform cert loaded from Bot__MediaCertPfxBase64 (thumbprint: {Thumbprint})",
                        cert.Thumbprint);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to load cert from Bot__MediaCertPfxBase64; falling back to file/store methods.");
                    cert = null;
                }
            }

            // Paths 1-3 need a thumbprint — resolve it only if PFX path didn't load a cert.
            if (cert is null)
            {
                var certThumbprint = _botConfig.MediaServiceCertThumbprint;

                if (string.IsNullOrWhiteSpace(certThumbprint))
                {
                    // Fallback: try WEBSITE_LOAD_CERTIFICATES (first thumbprint in the list).
                    var loadCerts = Environment.GetEnvironmentVariable("WEBSITE_LOAD_CERTIFICATES");
                    if (!string.IsNullOrWhiteSpace(loadCerts))
                    {
                        certThumbprint = loadCerts.Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(t => t.Trim())
                            .FirstOrDefault(t => t.Length == 40) ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(certThumbprint))
                            _logger.LogInformation(
                                "MediaPlatform: using cert thumbprint from WEBSITE_LOAD_CERTIFICATES: {Thumbprint}",
                                certThumbprint);
                    }
                }

                if (string.IsNullOrWhiteSpace(certThumbprint))
                {
                    _logger.LogWarning(
                        "MediaPlatform initialization skipped: no TLS cert available. " +
                        "Set Bot__MediaCertPfxBase64 (KV ref) or Bot__MediaServiceCertThumbprint. " +
                        "See docs/bot-implementation-guide.md for cert provisioning steps.");
                    return;
                }

                // 1. Linux App Service file path
                if (cert is null && !OperatingSystem.IsWindows())
                {
                    var p8Path = $"/var/ssl/private/{certThumbprint.ToUpperInvariant()}.p8";
                    if (File.Exists(p8Path))
                    {
                        try
                        {
                            cert = System.Security.Cryptography.X509Certificates.X509Certificate2
                                .CreateFromPemFile(p8Path);
                            _logger.LogInformation(
                                "MediaPlatform cert loaded from file {Path}. HasPrivateKey={HasKey}",
                                p8Path, cert.HasPrivateKey);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex,
                                "Failed to load cert from {Path}; will fall back to X509Store.", p8Path);
                        }
                    }
                    else
                    {
                        _logger.LogInformation(
                            "MediaPlatform cert file not found at {Path}; will try X509Store.", p8Path);
                    }
                }

                // 2. CurrentUser\My store
                if (cert is null)
                {
                    using var store = new System.Security.Cryptography.X509Certificates.X509Store(
                        System.Security.Cryptography.X509Certificates.StoreName.My,
                        System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser);
                    store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly);
                    var found = store.Certificates.Find(
                        System.Security.Cryptography.X509Certificates.X509FindType.FindByThumbprint,
                        certThumbprint, validOnly: false);
                    cert = found.Count > 0 ? found[0] : null;
                    if (cert is not null)
                        _logger.LogInformation(
                            "MediaPlatform cert loaded from CurrentUser\\My store. HasPrivateKey={HasKey}",
                            cert.HasPrivateKey);
                }

                // 3. LocalMachine\My store (Windows only)
                if (cert is null && OperatingSystem.IsWindows())
                {
                    using var store = new System.Security.Cryptography.X509Certificates.X509Store(
                        System.Security.Cryptography.X509Certificates.StoreName.My,
                        System.Security.Cryptography.X509Certificates.StoreLocation.LocalMachine);
                    store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly);
                    var found = store.Certificates.Find(
                        System.Security.Cryptography.X509Certificates.X509FindType.FindByThumbprint,
                        certThumbprint, validOnly: false);
                    cert = found.Count > 0 ? found[0] : null;
                    if (cert is not null)
                        _logger.LogInformation(
                            "MediaPlatform cert loaded from LocalMachine\\My store. HasPrivateKey={HasKey}",
                            cert.HasPrivateKey);
                }

                if (cert is null)
                {
                    _logger.LogError(
                        "MediaPlatform initialization aborted: certificate with thumbprint {Thumbprint} " +
                        "not found in /var/ssl/private/<thumbprint>.p8, CurrentUser\\My{Platform}. " +
                        "Ensure WEBSITE_LOAD_CERTIFICATES is set to that thumbprint and the app has restarted.",
                        certThumbprint,
                        OperatingSystem.IsWindows() ? " or LocalMachine\\My" : string.Empty);
                    return;
                }
            }

            _logger.LogInformation(
                "MediaPlatform cert ready. Subject={Subject} Thumbprint={Thumbprint8}... HasPrivateKey={HasKey}",
                cert.Subject, cert.Thumbprint[..8], cert.HasPrivateKey);

            // ── MediaPlatform.Initialize() ───────────────────────────────────────
            // VM hosting: a Standard public IP maps 1:1 to the NIC's private IP, preserving
            // ports (no PAT), so the media port is the same internally and publicly. It must
            // be DISTINCT from the 443 signaling/notification endpoint that Kestrel owns, and
            // open in the NSG (Allow-Media-TCP = 8445).
            const int internalPort = 8445;
            const int publicPort = 8445;

            var mediaPlatformSettings = new MediaPlatformSettings
            {
                MediaPlatformInstanceSettings = new MediaPlatformInstanceSettings
                {
                    // Pass the pre-loaded X509Certificate2 object directly so the SDK does
                    // NOT attempt to open LocalMachine\My (unsupported on Linux).
                    Certificate = cert,
                    InstanceInternalPort = internalPort,
                    InstancePublicIPAddress = publicIp,
                    InstancePublicPort = publicPort,
                    ServiceFqdn = _botConfig.ServiceCname,
                },
                ApplicationId = _botConfig.AppId,
            };

            MediaPlatform.Initialize(mediaPlatformSettings);

            _logger.LogInformation(
                "MediaPlatform.Initialize succeeded. Fqdn={Fqdn} PublicIp={Ip} PublicPort={Port} CertThumbprint={Cert}",
                _botConfig.ServiceCname, publicIp, publicPort, cert.Thumbprint[..8] + "...");
        }
        catch (Exception ex)
        {
            // Media platform init failure is not fatal for the bot's HTTP/notification path.
            // Log the error and continue — the bot can still receive Graph notifications
            // and return proper HTTP responses. Media sessions will fail until this is fixed.
            _logger.LogError(ex,
                "MediaPlatform initialization failed. Bot will handle Graph notifications " +
                "but media sessions won't work. Check cert + IP configuration.");
        }
    }

    // ── IDisposable ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        // ICommunicationsClient is disposed via StopAsync in the hosted-service lifecycle.
        _client = null;
    }
}
