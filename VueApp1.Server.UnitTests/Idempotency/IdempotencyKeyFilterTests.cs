using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using VueApp1.Server.Idempotency;
using Xunit;

namespace VueApp1.Server.UnitTests.Idempotency;

public class IdempotencyKeyFilterTests
{
    private sealed record SamplePayload(string Message);

    private readonly IdempotencyService _service = new(
        new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
        new InMemoryIdempotencyLock(),
        TimeSpan.FromHours(1));

    private IdempotencyKeyFilter CreateFilter() =>
        new(_service, Options.Create(new JsonOptions()));

    [Fact]
    public async Task WithoutAKeyHeader_PassesThrough()
    {
        var filter = CreateFilter();
        var context = CreateContext(key: null, new SamplePayload("hello"));
        var nextCalled = false;

        await filter.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(Executed(context, new CreatedResult((string?)null, null)));
        });

        Assert.True(nextCalled);
        Assert.Null(context.Result);
    }

    [Fact]
    public async Task SecondRequest_SameKeySamePayload_ReplaysTheFirstResponse()
    {
        var filter = CreateFilter();
        var receipt = new { id = "first" };

        var first = CreateContext("key-1", new SamplePayload("hello"));
        await filter.OnActionExecutionAsync(first, () =>
            Task.FromResult(Executed(first, new CreatedResult((string?)null, receipt))));
        Assert.Null(first.Result); // first request executed normally

        var second = CreateContext("key-1", new SamplePayload("hello"));
        var nextCalled = false;
        await filter.OnActionExecutionAsync(second, () =>
        {
            nextCalled = true;
            return Task.FromResult(Executed(second, new CreatedResult((string?)null, new { id = "second" })));
        });

        // The action must NOT run again; the stored response is replayed.
        Assert.False(nextCalled);
        var content = Assert.IsType<ContentResult>(second.Result);
        Assert.Equal(StatusCodes.Status201Created, content.StatusCode);
        Assert.Contains("first", content.Content, StringComparison.Ordinal);
        Assert.Equal("application/json; charset=utf-8", content.ContentType);
        Assert.Equal("true", second.HttpContext.Response.Headers[IdempotencyKeyFilter.ReplayedHeaderName]);
    }

    [Fact]
    public async Task SameKey_DifferentPayload_Returns422WithStableType()
    {
        var filter = CreateFilter();

        var first = CreateContext("key-1", new SamplePayload("hello"));
        await filter.OnActionExecutionAsync(first, () =>
            Task.FromResult(Executed(first, new CreatedResult((string?)null, new { id = "first" }))));

        var conflicting = CreateContext("key-1", new SamplePayload("DIFFERENT"));
        await filter.OnActionExecutionAsync(conflicting, () =>
            throw new InvalidOperationException("the action must not run on a payload conflict"));

        var result = Assert.IsType<ObjectResult>(conflicting.Result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, result.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(result.Value);
        Assert.Equal(ProblemDetailTypes.IdempotencyPayloadMismatch, problem.Type);
        Assert.Contains("application/problem+json", result.ContentTypes);
    }

    [Fact]
    public async Task ConcurrentRequest_SameKey_Returns409WhileTheFirstIsInFlight()
    {
        var filter = CreateFilter();
        var firstEnteredAction = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = CreateContext("key-1", new SamplePayload("hello"));
        var firstInvocation = filter.OnActionExecutionAsync(first, async () =>
        {
            firstEnteredAction.SetResult();
            await releaseFirst.Task.WaitAsync(TestContext.Current.CancellationToken);
            return Executed(first, new CreatedResult((string?)null, new { id = "first" }));
        });

        // Deterministic interleaving: the concurrent request starts only once
        // the first one provably holds the reservation (it reached its action).
        await firstEnteredAction.Task.WaitAsync(TestContext.Current.CancellationToken);

        var concurrent = CreateContext("key-1", new SamplePayload("hello"));
        await filter.OnActionExecutionAsync(concurrent, () =>
            throw new InvalidOperationException("the action must not run while the key is in flight"));

        var result = Assert.IsType<ObjectResult>(concurrent.Result);
        Assert.Equal(StatusCodes.Status409Conflict, result.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(result.Value);
        Assert.Equal(ProblemDetailTypes.IdempotencyInProgress, problem.Type);

        releaseFirst.SetResult();
        await firstInvocation.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ErrorResult_IsNotCommitted_SoTheKeyStaysUsable()
    {
        var filter = CreateFilter();

        var failed = CreateContext("key-1", new SamplePayload("hello"));
        await filter.OnActionExecutionAsync(failed, () =>
            Task.FromResult(Executed(failed, new ObjectResult(new ProblemDetails())
            {
                StatusCode = StatusCodes.Status422UnprocessableEntity,
            })));

        // Nothing stored and the lock released: a corrected retry (any
        // payload) reserves instead of hitting a poisoned key or a held lock.
        await using var retry = await _service.BeginAsync(
            "idempotency:POST:/api/sample:key-1",
            "HASH-OF-THE-CORRECTED-PAYLOAD",
            TestContext.Current.CancellationToken);
        Assert.Equal(IdempotencyOutcome.Reserved, retry.Outcome);
    }

    [Fact]
    public async Task ExceptionFromTheAction_IsNotCommitted_SoTheKeyStaysUsable()
    {
        var filter = CreateFilter();

        var throwing = CreateContext("key-1", new SamplePayload("hello"));
        await filter.OnActionExecutionAsync(throwing, () =>
        {
            var executed = Executed(throwing, result: null);
            executed.Exception = new InvalidOperationException("deliberate test failure");
            return Task.FromResult(executed);
        });

        var retryContext = CreateContext("key-1", new SamplePayload("hello"));
        var nextCalled = false;
        await filter.OnActionExecutionAsync(retryContext, () =>
        {
            nextCalled = true;
            return Task.FromResult(Executed(retryContext, new CreatedResult((string?)null, new { id = "retry" })));
        });

        // The retry re-executes (no poisoned key, no stale replay).
        Assert.True(nextCalled);
        Assert.Null(retryContext.Result);
    }

    private static ActionExecutingContext CreateContext(string? key, object payload)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.Path = "/api/sample";
        if (key is not null)
        {
            httpContext.Request.Headers[IdempotencyKeyFilter.HeaderName] = key;
        }

        var descriptor = new ControllerActionDescriptor
        {
            Parameters =
            [
                new ParameterDescriptor
                {
                    Name = "request",
                    ParameterType = payload.GetType(),
                    BindingInfo = new BindingInfo { BindingSource = BindingSource.Body },
                },
            ],
        };
        var actionContext = new ActionContext(httpContext, new RouteData(), descriptor);
        return new ActionExecutingContext(
            actionContext,
            [],
            new Dictionary<string, object?> { ["request"] = payload },
            controller: new object());
    }

    private static ActionExecutedContext Executed(ActionExecutingContext context, IActionResult? result) =>
        new(context, [], context.Controller) { Result = result };
}
