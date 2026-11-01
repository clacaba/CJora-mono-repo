using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;

namespace CJora.Documents.Api.Controllers;

/// <summary>Development-only multipart verification; does not save or publish documents.</summary>
[ApiController]
[Route("api/v1/doc/diagnostics/upload")]
public sealed class DiagnosticUploadController(IWebHostEnvironment environment) : ControllerBase
{
    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> Post([FromForm] IFormCollection form, CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment()) return NotFound();
        if (form.Files.Count == 0) return BadRequest(new { code = "FILE_REQUIRED" });
        var files = new List<object>();
        foreach (var file in form.Files)
        {
            await using var stream = file.OpenReadStream();
            var digest = await SHA256.HashDataAsync(stream, cancellationToken);
            files.Add(new { fieldName = file.Name, length = file.Length, sha256 = Convert.ToHexString(digest).ToLowerInvariant() });
        }
        return Ok(new { service = "Documents", files,
            fields = form.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray()) });
    }
}