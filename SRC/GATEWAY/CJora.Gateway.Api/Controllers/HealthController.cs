using Microsoft.AspNetCore.Mvc;

namespace CJora.Gateway.Api.Controllers;

/// <summary>Process liveness only; does not assert database or identity-provider readiness.</summary>
[ApiController]
[Route("health")]

public sealed class HealthController : ControllerBase
{
    [HttpGet]
    [HttpGet("live")]
    public IActionResult Get() => Ok(new { service = "Gateway", status = "alive" });
}