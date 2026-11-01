using Microsoft.AspNetCore.Mvc;

namespace CJora.IdentityManagement.Api.Controllers;

/// <summary>Returns fictional weather data for API smoke checks.</summary>
[ApiController]
[Route("[controller]")]
public sealed class WeatherForecastController : ControllerBase
{
    private static readonly string[] Summaries =
        ["Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"];

    [HttpGet]
    public ActionResult<WeatherForecast[]> Get() => Ok(
        Enumerable.Range(1, 5).Select(day => new WeatherForecast(
            DateOnly.FromDateTime(DateTime.UtcNow.AddDays(day)),
            Random.Shared.Next(-20, 55),
            Summaries[Random.Shared.Next(Summaries.Length)])).ToArray());
}