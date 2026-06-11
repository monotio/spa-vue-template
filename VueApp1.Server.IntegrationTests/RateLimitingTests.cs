using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using VueApp1.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace VueApp1.Server.IntegrationTests;

/// <summary>
/// Pins the wire shape of the global rate limiter's rejection. The 429 is the
/// one error produced entirely outside MVC and the exception handler, so it
/// is the easiest place for the "RFC 9457 on every error" promise to silently
/// break — historically it was written with WriteAsJsonAsync and went out
/// mislabeled as plain application/json.
/// </summary>
public class RateLimitingTests(IntegrationTestWebApplicationFactory factory)
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
    public async Task RateLimitedRequest_EmitsProblemJsonWithRetryAfter()
    {
        // A single-permit window makes the second request deterministically
        // the rejected one; the derived host keeps the partition state (and
        // the tightened limits) isolated from every other test.
        using var client = factory
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Performance:RateLimiting:PermitLimit", "1");
                builder.UseSetting("Performance:RateLimiting:QueueLimit", "0");
                builder.UseSetting("Performance:RateLimiting:WindowSeconds", "60");
            })
            .CreateClient();

        using var cts = CreateRequestCts();
        var accepted = await client.GetAsync("/api/weatherforecast", cts.Token);
        accepted.EnsureSuccessStatusCode();

        var rejected = await client.GetAsync("/api/weatherforecast", cts.Token);

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        // The contract promise: every error is application/problem+json —
        // including this one, which never touches MVC.
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);

        // Retry-After must reflect the lease's actual reset time (capped by
        // the configured window), as delta-seconds per RFC 9110 §10.2.3.
        var retryAfter = rejected.Headers.RetryAfter?.Delta;
        Assert.NotNull(retryAfter);
        Assert.InRange(retryAfter.Value, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60));

        var payload = await rejected.Content.ReadAsStringAsync(cts.Token);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        Assert.Equal(429, root.GetProperty("status").GetInt32());
        Assert.Equal("Too many requests", root.GetProperty("title").GetString());
    }
}
