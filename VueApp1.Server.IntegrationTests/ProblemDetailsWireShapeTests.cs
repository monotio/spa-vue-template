using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using VueApp1.Server;
using VueApp1.Server.IntegrationTests.Infrastructure;
using VueApp1.Server.Services;
using Xunit;

namespace VueApp1.Server.IntegrationTests;

/// <summary>
/// Pins the serialized wire shape of the global exception handler. The
/// OpenAPI description advertises "RFC 9457 problem details on every error",
/// but unit tests cover the C# objects — this is the only place the JSON that
/// downstream clients actually parse is asserted. Pins intent (which fields
/// exist, which must NOT) rather than byte-exact payloads, so wire-equivalent
/// refactors stay green.
/// </summary>
public class ProblemDetailsWireShapeTests(IntegrationTestWebApplicationFactory factory)
    : IClassFixture<IntegrationTestWebApplicationFactory>
{
    private static readonly TimeSpan _requestTimeout = TimeSpan.FromSeconds(10);

    private static CancellationTokenSource CreateRequestCts()
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(_requestTimeout);
        return cts;
    }

    [Fact]
    public async Task UnhandledException_EmitsRfc9457Problem_WithoutLeakingExceptionDetails()
    {
        // Swap in a throwing service against the existing route — no
        // test-only endpoint needed to exercise the handler end to end.
        using var client = factory
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                services.AddScoped<IWeatherForecastService, ThrowingWeatherForecastService>()))
            .CreateClient();

        using var cts = CreateRequestCts();
        var response = await client.GetAsync("/api/weatherforecast", cts.Token);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var payload = await response.Content.ReadAsStringAsync(cts.Token);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        Assert.Equal(500, root.GetProperty("status").GetInt32());
        Assert.Equal("An unexpected error occurred.", root.GetProperty("title").GetString());

        // traceId is the log-correlation handle the handler promises clients.
        Assert.True(root.TryGetProperty("traceId", out var traceId), $"traceId missing from: {payload}");
        Assert.False(string.IsNullOrWhiteSpace(traceId.GetString()));

        // The Testing environment is production-shaped: exception internals
        // are a Development-only diagnostic and must never reach clients.
        Assert.False(root.TryGetProperty("detail", out _), $"exception detail leaked: {payload}");
        Assert.False(root.TryGetProperty("exceptionType", out _), $"exceptionType leaked: {payload}");
    }

    private sealed class ThrowingWeatherForecastService : IWeatherForecastService
    {
        public Task<ServiceResponse<IReadOnlyList<WeatherForecast>>> GetForecastsAsync(
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Deliberate test failure - must never reach the client.");
    }
}
