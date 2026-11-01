using Microsoft.AspNetCore.Mvc;

namespace CJora.IdentityManagement.Api.Controllers;

/// <summary>Process liveness only; does not assert database or identity-provider readiness.</summary>
[ApiController]
[Route("health")]
[Route("api/v1/idm/health")]
public sealed class HealthController : ControllerBase
{
    [HttpGet]
    [HttpGet("live")]
    public IActionResult Get() => Ok(new { service = "IdentityManagement", status = "alive" });
}