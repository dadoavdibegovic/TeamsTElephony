using System.Net;
using ComplianceRecordingBot.Bot;
using ComplianceRecordingBot.Utils;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Graph.Communications.Client;

namespace ComplianceRecordingBot.Controllers;

/// <summary>
/// POST /api/calling — Microsoft Graph Communications notification endpoint.
///
/// This is the webhook URL registered in Azure Bot Service (bot-calltranskript-prod).
/// Microsoft Teams' policy engine POSTs here for every compliance-recording event:
///   - IncomingCall: a user covered by the recording policy received a call
///   - CallStateChanged: call established, terminated, participants changed, etc.
///
/// Request flow:
///   1. Validate the JWT in the Authorization header via
///      <see cref="ICommunicationsClient.ProcessNotificationAsync"/> which internally
///      calls our <see cref="Authentication.AuthenticationProvider.ValidateInboundRequestAsync"/>.
///   2. The SDK parses the notification payload, updates call state, and fires registered
///      event handlers (OnIncoming, OnUpdated, etc.) on the <see cref="ICallCollection"/>.
///   3. Return the SDK-generated HTTP response (200 or 202 + Location for async ops).
///
/// Authentication:
///   - Microsoft signs every notification with a BotFramework JWT.
///   - Returning 200 without validating the JWT would allow any HTTP client to
///     inject fake call notifications. We never skip this check.
///
/// Error handling:
///   - 401: JWT missing or invalid (auth guard).
///   - 500: SDK dispatch exception (logged + tracked).
/// </summary>
[ApiController]
[Route("api/calling")]
public class PlatformCallController : ControllerBase
{
    private readonly ICommunicationsClient _commsClient;
    private readonly TelemetryClient _telemetry;
    private readonly ILogger<PlatformCallController> _logger;

    public PlatformCallController(
        ComplianceRecordingBotService botService,
        TelemetryClient telemetry,
        ILogger<PlatformCallController> logger)
    {
        _commsClient = botService.Client;
        _telemetry = telemetry;
        _logger = logger;
    }

    /// <summary>
    /// Receives Graph Communications notifications from Microsoft Teams.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Post()
    {
        _logger.LogInformation("POST /api/calling received");

        // Emit the arrival event immediately (before JWT check) so we can observe
        // unauthenticated probes vs real notifications in App Insights.
        _telemetry.TrackEvent(Telemetry.EventCallReceived);

        try
        {
            // Convert the incoming ASP.NET request to System.Net.Http.HttpRequestMessage
            // as required by the Graph Communications SDK extension.
            var requestMessage = await BuildHttpRequestMessageAsync(HttpContext.Request)
                .ConfigureAwait(false);

            // Delegate to the SDK. This:
            //   (a) Calls AuthenticationProvider.ValidateInboundRequestAsync to verify JWT
            //   (b) Parses the CommsNotifications payload
            //   (c) Routes to ICallCollection.OnIncoming (or state-change events)
            //   (d) Returns an HttpResponseMessage with the correct status code and body
            var responseMessage = await _commsClient
                .ProcessNotificationAsync(requestMessage)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "POST /api/calling dispatched. SDK returned {StatusCode}",
                responseMessage.StatusCode);

            // Map the SDK HttpResponseMessage to an ASP.NET IActionResult.
            return await MapSdkResponseAsync(responseMessage).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException uae)
        {
            // SDK raises this when JWT validation fails.
            _logger.LogWarning(uae, "Graph notification rejected: JWT invalid");
            return Unauthorized(new { error = "invalid_token", message = uae.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Graph notification");
            _telemetry.TrackCallEvent(
                Telemetry.EventCallError,
                correlationId: "unknown",
                new Dictionary<string, string> { ["error"] = ex.Message });
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { error = "dispatch_failed", message = ex.Message });
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a System.Net.Http.HttpRequestMessage from the incoming ASP.NET request.
    /// The Graph Communications SDK extension method requires this type.
    /// </summary>
    private static async Task<HttpRequestMessage> BuildHttpRequestMessageAsync(
        HttpRequest request)
    {
        var baseUri = new Uri($"{request.Scheme}://{request.Host}");
        var requestUri = new Uri(baseUri, request.Path.Value + request.QueryString.Value);

        var httpRequest = new HttpRequestMessage(
            new HttpMethod(request.Method),
            requestUri);

        // Copy all request headers (includes Authorization with the JWT).
        foreach (var (key, values) in request.Headers)
        {
            // Content headers go on the content object, not the request.
            if (key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                continue;

            httpRequest.Headers.TryAddWithoutValidation(key, (IEnumerable<string>)values);
        }

        // Copy body.
        if (request.ContentLength > 0 || request.Headers.ContainsKey("Transfer-Encoding"))
        {
            var ms = new MemoryStream();
            await request.Body.CopyToAsync(ms).ConfigureAwait(false);
            ms.Position = 0;
            httpRequest.Content = new StreamContent(ms);

            // Re-add content-type header.
            if (request.ContentType is not null)
                httpRequest.Content.Headers.TryAddWithoutValidation(
                    "Content-Type", request.ContentType);
        }

        return httpRequest;
    }

    /// <summary>
    /// Converts a System.Net.Http.HttpResponseMessage from the SDK to an
    /// ASP.NET IActionResult, preserving body and status code.
    /// </summary>
    private static async Task<IActionResult> MapSdkResponseAsync(
        HttpResponseMessage sdkResponse)
    {
        var body = await sdkResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (sdkResponse.StatusCode == HttpStatusCode.Unauthorized)
        {
            return new ObjectResult(new { error = "unauthorized" })
            {
                StatusCode = StatusCodes.Status401Unauthorized,
            };
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return new StatusCodeResult((int)sdkResponse.StatusCode);
        }

        return new ContentResult
        {
            Content = body,
            ContentType = sdkResponse.Content.Headers.ContentType?.ToString() ?? "application/json",
            StatusCode = (int)sdkResponse.StatusCode,
        };
    }
}
