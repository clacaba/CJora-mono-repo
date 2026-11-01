using System.Text.Json;
using CJora.Payments.Api;
using CJora.Payments.Api.Controllers;
using Microsoft.AspNetCore.Mvc;

namespace CJora.Payments.UnitTests;

public sealed class ControllerTests
{
    [Fact]
    public void Health_reports_its_own_service_and_process_liveness()
    {
        var response = Assert.IsType<OkObjectResult>(new HealthController().Get());
        var body = JsonSerializer.SerializeToElement(response.Value);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("Payments", body.GetProperty("service").GetString());
        Assert.Equal("alive", body.GetProperty("status").GetString());
    }

    [Fact]
    public void Weather_returns_five_future_daily_samples_with_valid_values()
    {
        // Permit midnight rollover without making the test depend on exact random output.
        var earliestToday = DateOnly.FromDateTime(DateTime.UtcNow);
        var response = new WeatherForecastController().Get();
        var latestToday = DateOnly.FromDateTime(DateTime.UtcNow);
        var result = Assert.IsType<OkObjectResult>(response.Result);
        var forecasts = Assert.IsType<WeatherForecast[]>(result.Value);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(5, forecasts.Length);
        for (var index = 0; index < forecasts.Length; index++)
        {
            var forecast = forecasts[index];
            Assert.InRange(forecast.Date, earliestToday.AddDays(index + 1), latestToday.AddDays(index + 1));
            Assert.InRange(forecast.TemperatureC, -20, 54);
            Assert.False(string.IsNullOrWhiteSpace(forecast.Summary));
            if (index > 0) Assert.True(forecast.Date > forecasts[index - 1].Date);
        }
    }

    [Theory]
    [InlineData(-40, -40)]
    [InlineData(0, 32)]
    [InlineData(1, 34)]
    [InlineData(-1, 30)]
    [InlineData(-20, -4)]
    [InlineData(20, 68)]
    [InlineData(100, 212)]
    public void Weather_converts_known_Celsius_values(int celsius, int fahrenheit)
    {
        var forecast = new WeatherForecast(new DateOnly(2026, 1, 1), celsius, "Sample");
        Assert.Equal(fahrenheit, forecast.TemperatureF);
    }
}