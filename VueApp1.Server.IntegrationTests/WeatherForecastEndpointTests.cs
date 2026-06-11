using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using VueApp1.Server;
using VueApp1.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace VueApp1.Server.IntegrationTests;

public class WeatherForecastEndpointTests(IntegrationTestWebApplicationFactory factory)
    : IClassFixture<IntegrationTestWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();
    private static readonly TimeSpan _requestTimeout = TimeSpan.FromSeconds(10);

    private static CancellationTokenSource CreateRequestCts()
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(_requestTimeout);
        return cts;
    }

    [Fact]
    public async Task GetWeatherForecast_ReturnsOkWithForecasts()
    {
        using var cts = CreateRequestCts();
        var response = await _client.GetAsync("/api/weatherforecast", cts.Token);

        response.EnsureSuccessStatusCode();
        var forecasts = await response.Content.ReadFromJsonAsync<WeatherForecast[]>(cts.Token);
        Assert.NotNull(forecasts);
        Assert.Equal(5, forecasts.Length);
    }

    [Fact]
    public async Task GetWeatherForecast_ReturnsJsonContentType()
    {
        using var cts = CreateRequestCts();
        var response = await _client.GetAsync("/api/weatherforecast", cts.Token);

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task GetWeatherForecast_IncludesServerTimingHeader()
    {
        using var cts = CreateRequestCts();
        var response = await _client.GetAsync("/api/weatherforecast", cts.Token);

        Assert.True(response.Headers.Contains("Server-Timing"));
    }

    [Fact]
    public async Task LivenessProbe_ReturnsHealthy()
    {
        using var cts = CreateRequestCts();
        var response = await _client.GetAsync("/health/live", cts.Token);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        Assert.Equal("Healthy", body);
    }

    [Fact]
    public async Task ReadinessProbe_ReturnsHealthy()
    {
        using var cts = CreateRequestCts();
        var response = await _client.GetAsync("/health/ready", cts.Token);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        Assert.Equal("Healthy", body);
    }

    [Fact]
    public async Task HealthAlias_ReturnsHealthy()
    {
        // /health stays mapped (readiness semantics) so existing consumers —
        // uptime monitors, platform defaults that probe a single path — keep
        // working after the live/ready split.
        using var cts = CreateRequestCts();
        var response = await _client.GetAsync("/health", cts.Token);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        Assert.Equal("Healthy", body);
    }

    [Fact]
    public async Task FailingReadyTaggedCheck_FailsReadinessButNeverLiveness()
    {
        // The split's actual contract: a failing "ready"-tagged check (the
        // DbContext seam) drains traffic — readiness and the alias go 503 —
        // but must never trip liveness, or the orchestrator would restart a
        // healthy process on a dependency blip. With no checks registered the
        // three probes are behaviorally identical, so only a failing tagged
        // check pins the liveness predicate and the tag filter.
        using var faultedFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddHealthChecks()
                    .AddCheck("ready-dep", () => HealthCheckResult.Unhealthy(), tags: ["ready"])));
        using var client = faultedFactory.CreateClient();
        using var cts = CreateRequestCts();

        var live = await client.GetAsync("/health/live", cts.Token);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal("Healthy", await live.Content.ReadAsStringAsync(cts.Token));

        var ready = await client.GetAsync("/health/ready", cts.Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal("Unhealthy", await ready.Content.ReadAsStringAsync(cts.Token));

        var alias = await client.GetAsync("/health", cts.Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, alias.StatusCode);
        Assert.Equal("Unhealthy", await alias.Content.ReadAsStringAsync(cts.Token));
    }

    [Fact]
    public async Task FailingUntaggedCheck_DoesNotAffectReadinessOrAlias()
    {
        // Pins the filter itself: readiness and the alias run ONLY the
        // "ready"-tagged checks. If either were an unfiltered catch-all (the
        // foot-gun the alias comment warns about), this unrelated failing
        // check would flip them to 503.
        using var faultedFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddHealthChecks()
                    .AddCheck("untagged-dep", () => HealthCheckResult.Unhealthy())));
        using var client = faultedFactory.CreateClient();
        using var cts = CreateRequestCts();

        var ready = await client.GetAsync("/health/ready", cts.Token);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Healthy", await ready.Content.ReadAsStringAsync(cts.Token));

        var alias = await client.GetAsync("/health", cts.Token);
        Assert.Equal(HttpStatusCode.OK, alias.StatusCode);
        Assert.Equal("Healthy", await alias.Content.ReadAsStringAsync(cts.Token));
    }

    [Fact]
    public async Task NonExistentApiEndpoint_ReturnsNotFound()
    {
        using var cts = CreateRequestCts();
        var response = await _client.GetAsync("/api/nonexistent", cts.Token);

        // Wire-shape pin: unmatched /api routes must carry the full RFC 9457
        // contract — without the dedicated /api fallback they would fall
        // through to the SPA shell and return index.html with a 200.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var payload = await response.Content.ReadAsStringAsync(cts.Token);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        Assert.Equal(404, root.GetProperty("status").GetInt32());
        Assert.Equal("Not Found", root.GetProperty("title").GetString());
        // The detail names the offending path so client logs are actionable.
        Assert.Contains("/api/nonexistent", root.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }
}
