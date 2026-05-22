using ComplianceRecordingBot.Configuration;
using Microsoft.Extensions.Options;

namespace ComplianceRecordingBot.Bot;

/// <summary>
/// Central bot service. Owns the ICommunicationsClient (Graph Communications SDK)
/// and the active call registry.
///
/// PlatformCallController forwards incoming Graph notifications here.
/// This service dispatches them to per-call CallHandler instances.
///
/// TODO (Session 2): Initialize Microsoft.Graph.Communications.Client.ICommunicationsClient.
///   Wire bot app credentials (AppId, AppSecret, TenantId) from BotConfig.
///   Register notification handlers for IncomingCall, CallStateChanged, etc.
///   Maintain a ConcurrentDictionary<string, CallHandler> of active calls.
///   Implement HandleNotificationAsync(HttpRequest) which is called by PlatformCallController.
///
/// TODO (Session 2): Reference the official sample:
///   microsoft-graph-comms-samples/.../ComplianceRecordingBot/Bot/Bot.cs
///
/// Current session 1 status: stub — just holds config references.
/// </summary>
public class ComplianceRecordingBotService
{
    private readonly BotConfig _botConfig;
    private readonly BackendConfig _backendConfig;
    private readonly ILogger<ComplianceRecordingBotService> _logger;

    // TODO (Session 2): Add ICommunicationsClient _client;
    // TODO (Session 2): Add ConcurrentDictionary<string, CallHandler> _callHandlers;

    public ComplianceRecordingBotService(
        IOptions<BotConfig> botConfig,
        IOptions<BackendConfig> backendConfig,
        ILogger<ComplianceRecordingBotService> logger)
    {
        _botConfig = botConfig.Value;
        _backendConfig = backendConfig.Value;
        _logger = logger;

        _logger.LogInformation(
            "ComplianceRecordingBotService initialised. AppId={AppId} ServiceCname={ServiceCname}",
            _botConfig.AppId,
            _botConfig.ServiceCname);
    }

    // TODO (Session 2): public Task HandleNotificationAsync(HttpRequest request) { ... }
}
