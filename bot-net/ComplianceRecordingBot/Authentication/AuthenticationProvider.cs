using System.Net.Http.Headers;
using ComplianceRecordingBot.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Graph.Communications.Client.Authentication;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;

namespace ComplianceRecordingBot.Authentication;

/// <summary>
/// Implements <see cref="IRequestAuthenticationProvider"/> for the Graph Communications SDK.
///
/// Responsibilities:
///   1. <see cref="ValidateInboundRequestAsync"/> — verifies the JWT Microsoft attaches to
///      every incoming Graph Communications notification (POST /api/calling).  Uses the
///      OpenID Connect discovery endpoint for the SGB tenant to fetch signing keys.
///   2. <see cref="AuthenticateOutboundRequestAsync"/> — adds a client-credentials access
///      token to every outbound call to the Graph Calling API (answer, mute, etc.).
///
/// JWT validation details (required by spec §Security and pitfall #2):
///   - Issuer:   https://api.botframework.com  (Graph Communications notif issuer)
///   - Audience: our bot's AppId
///   - Signing keys: fetched from BotFramework's OpenID metadata, cached by
///                   <see cref="ConfigurationManager{T}"/> and refreshed automatically.
///
/// Outbound auth uses MSAL confidential-client flow (client_credentials) against the
/// SGB tenant.  The token is cached by MSAL's in-memory token cache.
/// </summary>
public class AuthenticationProvider : IRequestAuthenticationProvider
{
    // BotFramework / Graph Communications uses this issuer for signing notification JWTs.
    private const string BotFrameworkMetadataUrl =
        "https://login.botframework.com/v1/.well-known/openidconfiguration";

    // For emulator / test: accept this issuer too (Graph dev calls come from here).
    private const string BotFrameworkEmulatorIssuer = "https://sts.windows.net/d6d49420-f39b-4df7-a1dc-d59a935871db/";

    private const string GraphCallingAudience = "https://graph.microsoft.com";

    private readonly BotConfig _config;
    private readonly ILogger<AuthenticationProvider> _logger;

    // Caches the BotFramework OpenID config and refreshes signing keys on demand.
    private readonly IConfigurationManager<OpenIdConnectConfiguration> _openIdManager;

    // MSAL confidential client for outbound Graph Calling requests.
    private readonly Microsoft.Identity.Client.IConfidentialClientApplication _msal;

    public AuthenticationProvider(
        IOptions<BotConfig> botConfig,
        ILogger<AuthenticationProvider> logger)
    {
        _config = botConfig.Value;
        _logger = logger;

        // Lazy-fetch BotFramework signing keys; 1-day refresh interval is typical.
        _openIdManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            BotFrameworkMetadataUrl,
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever { RequireHttps = true });

        // Build MSAL confidential client for outbound Graph Calling auth.
        _msal = Microsoft.Identity.Client.ConfidentialClientApplicationBuilder
            .Create(_config.AppId)
            .WithClientSecret(_config.AppSecret)
            .WithAuthority(
                Microsoft.Identity.Client.AzureCloudInstance.AzurePublic,
                _config.TenantId)
            .Build();
    }

    /// <summary>
    /// Validates the JWT on an inbound Graph Communications notification.
    /// Called by the SDK before raising any call events.
    /// Returns <see cref="RequestValidationResult.IsValid"/> = false on failure;
    /// the controller must return 401 in that case.
    /// </summary>
    public async Task<RequestValidationResult> ValidateInboundRequestAsync(
        HttpRequestMessage request)
    {
        try
        {
            // Extract bearer from Authorization header.
            if (!TryExtractBearer(request, out var token))
            {
                _logger.LogWarning("Inbound request has no Bearer token");
                return new RequestValidationResult { IsValid = false };
            }

            // Fetch current signing keys (cached by ConfigurationManager).
            var openIdConfig = await _openIdManager.GetConfigurationAsync(CancellationToken.None)
                .ConfigureAwait(false);

            var validationParams = new TokenValidationParameters
            {
                ValidIssuers = new[]
                {
                    "https://api.botframework.com",
                    BotFrameworkEmulatorIssuer
                },
                ValidAudiences = new[] { _config.AppId },
                IssuerSigningKeys = openIdConfig.SigningKeys,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ClockSkew = TimeSpan.FromMinutes(5),
            };

            var handler = new JwtSecurityTokenHandler();
            var principal = handler.ValidateToken(
                token, validationParams, out var validatedToken);

            // Pull the tenant from the token's tid claim.
            var tid = (validatedToken as JwtSecurityToken)
                ?.Claims.FirstOrDefault(c => c.Type == "tid")
                ?.Value;

            _logger.LogDebug(
                "Inbound JWT validated. Issuer={Issuer} TenantId={TenantId}",
                (validatedToken as JwtSecurityToken)?.Issuer, tid);

            return new RequestValidationResult
            {
                IsValid = true,
                TenantId = tid ?? _config.TenantId,
            };
        }
        catch (SecurityTokenException ex)
        {
            _logger.LogWarning(ex, "Inbound JWT validation failed");
            return new RequestValidationResult { IsValid = false };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error validating inbound JWT");
            return new RequestValidationResult { IsValid = false };
        }
    }

    /// <summary>
    /// Adds an access token to outbound Graph Calling API requests.
    /// Called by the SDK for every outbound call (answer, KeepAlive, etc.).
    /// </summary>
    public async Task AuthenticateOutboundRequestAsync(
        HttpRequestMessage request,
        string tenant)
    {
        try
        {
            var result = await _msal
                .AcquireTokenForClient(new[] { $"{GraphCallingAudience}/.default" })
                .ExecuteAsync()
                .ConfigureAwait(false);

            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", result.AccessToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to acquire outbound Graph Calling token for tenant {Tenant}", tenant);
            throw;
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static bool TryExtractBearer(HttpRequestMessage request, out string token)
    {
        token = string.Empty;
        var authHeader = request.Headers.Authorization;
        if (authHeader is null
            || !string.Equals(authHeader.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(authHeader.Parameter))
        {
            return false;
        }
        token = authHeader.Parameter;
        return true;
    }
}
