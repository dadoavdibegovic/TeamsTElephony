using Microsoft.AspNetCore.Mvc;

namespace ComplianceRecordingBot.Controllers;

/// <summary>
/// POST /api/calling — Microsoft Graph Communications notification endpoint.
///
/// This is the webhook URL registered in Azure Bot Service
/// (bot-calltranskript-prod, callingWebhook).  Microsoft Teams' policy engine
/// POSTs here whenever:
///   - An IncomingCall notification is triggered for a user covered by the
///     CallTranskriptRecording compliance recording policy
///   - A call state changes (established, terminated, etc.)
///
/// Session 1 status: STUB only — accepts and returns 200 for every request.
///
/// TODO (Session 2): Wire Microsoft.Graph.Communications SDK handler:
///   1. Validate the JWT in the Authorization header (Graph Communications
///      signs every notification; reject without valid sig).
///   2. Deserialize the CommsNotifications payload.
///   3. Dispatch to the registered ICommunicationsClient which routes to
///      the appropriate CallHandler (join call, start audio stream, etc.).
///   See docs/bot-implementation-guide.md §6 and Microsoft's reference sample:
///   https://github.com/microsoftgraph/microsoft-graph-comms-samples/tree/master/Samples/V1.0Samples/LocalMediaSamples/ComplianceRecordingBot
/// </summary>
[ApiController]
[Route("api/calling")]
public class PlatformCallController : ControllerBase
{
    private readonly ILogger<PlatformCallController> _logger;

    public PlatformCallController(ILogger<PlatformCallController> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Receives Graph Communications notifications from Microsoft Teams.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Post()
    {
        // TODO (Session 2): Replace stub with real Graph Communications dispatch.
        // IMPORTANT: Do NOT remove JWT validation when implementing — every
        // incoming notification must have its Authorization header verified
        // against Microsoft's signing keys. Microsoft's sample includes this;
        // don't strip it out (see pitfall #2 in bot-implementation-guide.md).

        _logger.LogInformation("POST /api/calling received (stub — no handling yet)");
        return Ok();
    }
}
