namespace ComplianceRecordingBot.Authentication;

/// <summary>
/// Validates the JWT in the Authorization header of every incoming Graph Communications
/// notification (POST /api/calling).
///
/// Microsoft signs each notification with its own signing keys. The bot MUST verify
/// these JWTs before processing any notification payload — failure to do so would
/// allow anyone to spoof calls to the bot.
///
/// TODO (Session 2): Implement JWT validation using the Graph Communications SDK's
///   built-in authentication helpers. Microsoft's sample includes a working
///   AuthenticationProvider — adopt it rather than writing custom JWT validation.
///
/// Reference:
///   microsoft-graph-comms-samples/.../ComplianceRecordingBot/Authentication/
///   See also: docs/bot-implementation-guide.md pitfall #2 (JWT must be validated).
///
/// WARNING: Do NOT skip this validation in production. Missing it means any HTTP
/// client can inject fake call notifications to the bot.
/// </summary>
public class AuthenticationProvider
{
    // TODO (Session 2): Inject IConfiguration (for AppId / TenantId) and ILogger.
    // TODO (Session 2): Implement ValidateInboundRequestAsync(HttpRequest) -> bool/ClaimsPrincipal.
}
