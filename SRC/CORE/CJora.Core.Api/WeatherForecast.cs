namespace CJora.Core.Api;

/// <summary>Generated sample data used to verify this API; unrelated to financial activity.</summary>
public sealed record WeatherForecast(DateOnly Date, int TemperatureC, string Summary)
{
    public int TemperatureF => (int)Math.Round(TemperatureC * 9d / 5d + 32d, MidpointRounding.AwayFromZero);
}