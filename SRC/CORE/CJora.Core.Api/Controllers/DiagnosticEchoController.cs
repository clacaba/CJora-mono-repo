using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace CJora.Core.Api.Controllers;

/// <summary>Development-only, non-persisting payload check; not a business API.</summary>
[ApiController]
[Route("api/v1/cor/diagnostics/echo")]
public sealed class DiagnosticEchoController(IWebHostEnvironment environment) : ControllerBase
{
    [HttpPost]
    [Consumes("application/json")]
    public IActionResult Post([FromBody] JsonElement payload)
    {
        if (!environment.IsDevelopment()) return NotFound();
        return Ok(new { service = "Core", payload,
            credentialHeadersPresent = Request.Headers.ContainsKey("Authorization") || Request.Headers.ContainsKey("Cookie"),
            routingHeadersPresent = Request.Headers.ContainsKey(CJora.Routing.GatewayRouteContext.TokenHeader) || Request.Headers.ContainsKey(CJora.Routing.GatewayRouteContext.RouteHeader) });
    }
}