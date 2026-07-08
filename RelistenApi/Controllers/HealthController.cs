using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Relisten.Services.Health;

namespace Relisten.Controllers;

[ApiController]
[Route("health")]
public sealed class HealthController : ControllerBase
{
    private readonly IReadinessCheck _readinessCheck;
    private readonly ILogger<HealthController> _logger;

    public HealthController(IReadinessCheck readinessCheck, ILogger<HealthController> logger)
    {
        _readinessCheck = readinessCheck;
        _logger = logger;
    }

    [HttpGet("live")]
    public IActionResult Live()
    {
        return Ok(new { status = "ok" });
    }

    [HttpGet("ready")]
    public async Task<IActionResult> Ready(CancellationToken cancellationToken)
    {
        try
        {
            await _readinessCheck.CheckAsync(cancellationToken);
            return Ok(new { status = "ok" });
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Database readiness check failed");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { status = "unavailable" });
        }
    }
}
