using Microsoft.AspNetCore.Mvc;

namespace ComplianceRecordingBot.Controllers;

/// <summary>
/// GET /health — lightweight liveness probe.
/// Used by Azure App Service health checks and load balancer probes.
/// </summary>
[ApiController]
[Route("health")]
public class HealthController : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Get()
    {
        return Ok(new
        {
            status = "healthy",
            service = "ComplianceRecordingBot",
            timestamp = DateTime.UtcNow.ToString("o"),
        });
    }
}
