using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using VueApp1.Server;
using VueApp1.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace VueApp1.Server.IntegrationTests;

/// <summary>
/// Wire-level pins for the Idempotency-Key teaching endpoint: replay
/// (same id, marker header), payload-conflict 422, missing-key 400, and the
/// don't-poison rule after a domain rejection. Each test uses a fresh
/// GUID key — the class fixture shares one server (and one cache).
/// </summary>
public class FeedbackEndpointTests(IntegrationTestWebApplicationFactory factory)
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

    private Task<HttpResponseMessage> PostFeedbackAsync(
        object payload, string? idempotencyKey, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/feedback")
        {
            Content = JsonContent.Create(payload),
        };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return _client.SendAsync(request, cancellationToken);
    }

    [Fact]
    public async Task Post_Returns201WithReceipt()
    {
        using var cts = CreateRequestCts();
        var response = await PostFeedbackAsync(
            new { message = "The weather page is delightful." }, NewKey(), cts.Token);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<FeedbackReceipt>(cts.Token);
        Assert.NotNull(receipt);
        Assert.NotEqual(Guid.Empty, receipt.Id);
        Assert.Equal("The weather page is delightful.", receipt.Message);
    }

    [Fact]
    public async Task Post_RetryWithSameKeyAndBody_ReplaysTheStoredResponse()
    {
        var key = NewKey();
        var payload = new { message = "Retried over a flaky connection." };
        using var cts = CreateRequestCts();

        var first = await PostFeedbackAsync(payload, key, cts.Token);
        var firstReceipt = await first.Content.ReadFromJsonAsync<FeedbackReceipt>(cts.Token);

        var retry = await PostFeedbackAsync(payload, key, cts.Token);
        var retryReceipt = await retry.Content.ReadFromJsonAsync<FeedbackReceipt>(cts.Token);

        // Same server-minted id = the stored response was replayed; a re-run
        // would have minted a new one. The marker header makes it explicit.
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(firstReceipt!.Id, retryReceipt!.Id);
        Assert.Equal("true", Assert.Single(retry.Headers.GetValues("Idempotency-Replayed")));
        Assert.False(first.Headers.Contains("Idempotency-Replayed"));
    }

    [Fact]
    public async Task Post_SameKeyDifferentBody_Returns422WithStableProblemType()
    {
        var key = NewKey();
        using var cts = CreateRequestCts();

        await PostFeedbackAsync(new { message = "Original payload." }, key, cts.Token);
        var conflicting = await PostFeedbackAsync(new { message = "Tampered payload." }, key, cts.Token);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, conflicting.StatusCode);
        Assert.Equal(
            "application/problem+json", conflicting.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await conflicting.Content.ReadAsStringAsync(cts.Token));
        Assert.Equal(
            "/problems/idempotency-payload-mismatch",
            problem.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Post_WithoutIdempotencyKey_Returns400ValidationProblem()
    {
        using var cts = CreateRequestCts();
        var response = await PostFeedbackAsync(
            new { message = "No key on an endpoint that requires one." }, idempotencyKey: null, cts.Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
        Assert.True(problem.RootElement.TryGetProperty("errors", out _));
    }

    [Fact]
    public async Task Post_InvalidBody_Returns400ValidationProblem()
    {
        using var cts = CreateRequestCts();
        var response = await PostFeedbackAsync(new { message = "ab" }, NewKey(), cts.Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Post_DomainRejection_DoesNotPoisonTheKey()
    {
        var key = NewKey();
        using var cts = CreateRequestCts();

        // Whitespace passes DataAnnotations but fails the domain rule: a 422
        // AFTER the reservation was taken — the don't-poison case that the
        // missing-key 400 (rejected before the filter runs) can't exercise.
        var rejected = await PostFeedbackAsync(new { message = "   " }, key, cts.Token);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        using (var problem = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync(cts.Token)))
        {
            Assert.Equal(
                "/problems/validation-failed",
                problem.RootElement.GetProperty("type").GetString());
        }

        // The corrected retry with the SAME key must execute (201), not
        // replay the 422 and not collide with a stale lock.
        var corrected = await PostFeedbackAsync(new { message = "Corrected message." }, key, cts.Token);
        Assert.Equal(HttpStatusCode.Created, corrected.StatusCode);
        Assert.False(corrected.Headers.Contains("Idempotency-Replayed"));
    }

    private static string NewKey() => Guid.NewGuid().ToString("N");
}
