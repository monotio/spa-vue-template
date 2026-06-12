using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using VueApp1.Server;
using VueApp1.Server.Idempotency;
using Xunit;

namespace VueApp1.Server.UnitTests.Idempotency;

/// <summary>
/// Branch pins for the minimal-API twin of <see cref="IdempotencyKeyFilter"/>.
/// The streamed-response behaviors (in-flight 409 over the wire, the
/// never-commits posture, release-on-abort) are pinned end to end in
/// AgentEndpointTests; THIS suite pins the outcome→result mapping —
/// including the two branches a never-committing endpoint cannot reach over
/// HTTP (payload mismatch and cached replay both require a committed record,
/// which only exists when the key scope is shared with a committing
/// endpoint; the filter handles them "for correctness" and that handling
/// deserves a pin too).
/// </summary>
public class IdempotencyEndpointFilterTests
{
    private const string Path = "/api/agent/conversations/c1/turns";

    private sealed record SamplePayload(string Message);

    private readonly IdempotencyService _service = new(
        new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
        new InMemoryIdempotencyLock(),
        TimeSpan.FromHours(1));

    private readonly IOptions<JsonOptions> _jsonOptions = Options.Create(new JsonOptions());

    [Fact]
    public async Task WithoutAKeyHeader_PassesThrough()
    {
        var filter = CreateFilter();
        var expected = Results.Ok();
        var nextCalled = false;

        var result = await filter.InvokeAsync(CreateContext(key: null, new SamplePayload("hi")), _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(expected);
        });

        Assert.True(nextCalled);
        Assert.Same(expected, result);
    }

    [Fact]
    public async Task FreshKey_IsReserved_AndTheHandlerRuns()
    {
        var filter = CreateFilter();
        var expected = Results.Ok();
        var nextCalled = false;

        var result = await filter.InvokeAsync(CreateContext("key-1", new SamplePayload("hi")), _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(expected);
        });

        Assert.True(nextCalled);
        Assert.Same(expected, result);
    }

    [Fact]
    public async Task KeyHeldByAnInFlightRequest_Returns409InProgress_WithoutRunningTheHandler()
    {
        var payload = new SamplePayload("hi");
        await using var original = await _service.BeginAsync(
            KeyFor("key-1"), HashOf(payload), TestContext.Current.CancellationToken);
        Assert.Equal(IdempotencyOutcome.Reserved, original.Outcome);

        var filter = CreateFilter();
        var nextCalled = false;
        var result = await filter.InvokeAsync(CreateContext("key-1", payload), _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        });

        Assert.False(nextCalled);
        var problem = Assert.IsType<ProblemHttpResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, problem.StatusCode);
        Assert.Equal(ProblemDetailTypes.IdempotencyInProgress, problem.ProblemDetails.Type);
    }

    [Fact]
    public async Task CommittedRecordWithDifferentPayload_Returns422Mismatch_WithoutRunningTheHandler()
    {
        await CommitRecordAsync("key-1", new SamplePayload("original"));

        var filter = CreateFilter();
        var nextCalled = false;
        var result = await filter.InvokeAsync(CreateContext("key-1", new SamplePayload("DIFFERENT")), _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        });

        Assert.False(nextCalled);
        var problem = Assert.IsType<ProblemHttpResult>(result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, problem.StatusCode);
        Assert.Equal(ProblemDetailTypes.IdempotencyPayloadMismatch, problem.ProblemDetails.Type);
    }

    [Fact]
    public async Task CommittedRecordWithSamePayload_ReplaysIt_WithoutRunningTheHandler()
    {
        await CommitRecordAsync("key-1", new SamplePayload("original"));

        var filter = CreateFilter();
        var context = CreateContext("key-1", new SamplePayload("original"));
        var nextCalled = false;
        var result = await filter.InvokeAsync(context, _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        });

        Assert.False(nextCalled);
        var content = Assert.IsType<ContentHttpResult>(result);
        Assert.Equal(StatusCodes.Status201Created, content.StatusCode);
        Assert.Equal("""{"id":"first"}""", content.ResponseContent);
        Assert.Equal(
            "true",
            context.HttpContext.Response.Headers[IdempotencyKeyFilter.ReplayedHeaderName]);
    }

    // -----------------------------------------------------------------------

    private IdempotencyEndpointFilter<SamplePayload> CreateFilter() => new(_service, _jsonOptions);

    /// <summary>Same scoping rule the filter applies: method + lowercased path + key.</summary>
    private static string KeyFor(string headerValue) => $"idempotency:POST:{Path}:{headerValue}";

    private string HashOf(SamplePayload payload) =>
        Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOptions.Value.SerializerOptions)));

    /// <summary>
    /// Simulates the key scope being shared with a COMMITTING endpoint: the
    /// endpoint filter itself never commits, so these branches are
    /// unreachable through the agent turn POST alone.
    /// </summary>
    private async Task CommitRecordAsync(string headerValue, SamplePayload payload)
    {
        await using var reservation = await _service.BeginAsync(
            KeyFor(headerValue), HashOf(payload), TestContext.Current.CancellationToken);
        Assert.Equal(IdempotencyOutcome.Reserved, reservation.Outcome);
        await reservation.CommitAsync(
            StatusCodes.Status201Created, "application/json", """{"id":"first"}""");
    }

    private static DefaultEndpointFilterInvocationContext CreateContext(string? key, SamplePayload payload)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.Path = Path;
        if (key is not null)
        {
            httpContext.Request.Headers[IdempotencyKeyFilter.HeaderName] = key;
        }

        return new DefaultEndpointFilterInvocationContext(httpContext, payload);
    }
}
