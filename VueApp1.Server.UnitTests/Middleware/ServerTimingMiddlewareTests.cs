using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using VueApp1.Server.Middleware;
using VueApp1.Server.UnitTests.Infrastructure;
using Xunit;

namespace VueApp1.Server.UnitTests.Middleware;

public class ServerTimingMiddlewareTests
{
    /// <summary>
    /// DefaultHttpContext's response feature silently ignores OnStarting
    /// callbacks; this feature captures them so the test can fire them the
    /// way a real server does just before the response starts.
    /// </summary>
    private sealed class StartableResponseFeature : HttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> _onStarting = [];

        public override void OnStarting(Func<object, Task> callback, object state) =>
            _onStarting.Add((callback, state));

        public async Task FireOnStartingAsync()
        {
            for (var i = _onStarting.Count - 1; i >= 0; i--)
            {
                await _onStarting[i].Callback(_onStarting[i].State);
            }
        }
    }

    private static (DefaultHttpContext Context, StartableResponseFeature Response) CreateContext(
        string path,
        IServerTimingMetrics? metrics = null)
    {
        var services = new ServiceCollection();
        if (metrics is not null)
        {
            services.AddSingleton(metrics);
        }

        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
        };
        var responseFeature = new StartableResponseFeature();
        context.Features.Set<IHttpResponseFeature>(responseFeature);
        context.Request.Path = path;
        return (context, responseFeature);
    }

    private static async Task RunPipelineAsync(
        HttpContext context,
        StartableResponseFeature responseFeature)
    {
        var middleware = new ServerTimingMiddleware(_ => Task.CompletedTask);
        await middleware.InvokeAsync(context);
        await responseFeature.FireOnStartingAsync();
    }

    [Fact]
    public async Task ApiRequest_GetsServerTimingHeader()
    {
        var (context, response) = CreateContext("/api/weatherforecast");

        await RunPipelineAsync(context, response);

        var header = context.Response.Headers["Server-Timing"].ToString();
        Assert.StartsWith("total;dur=", header, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonApiRequest_GetsNoHeader()
    {
        var (context, response) = CreateContext("/weather");

        await RunPipelineAsync(context, response);

        Assert.False(context.Response.Headers.ContainsKey("Server-Timing"));
    }

    [Fact]
    public async Task Duration_UsesInvariantDecimalPoint_OnCommaDecimalLocales()
    {
        // Server-Timing requires a decimal point; sv-SE formats 0.9 as "0,9".
        // The runner already pins sv-SE suite-wide (xunit.runner.json); the
        // explicit switch keeps this regression pin self-contained if that
        // default ever changes.
        using var _ = new CultureSwitcher("sv-SE");

        var (context, response) = CreateContext("/api/weatherforecast");

        await RunPipelineAsync(context, response);

        var header = context.Response.Headers["Server-Timing"].ToString();
        Assert.DoesNotContain(",", header.Split(';')[1], StringComparison.Ordinal);
        Assert.Matches(@"total;dur=\d+\.\d", header);
    }

    [Fact]
    public async Task MetricsEntries_AreAppended()
    {
        var (context, response) = CreateContext("/api/weatherforecast", new FakeMetrics());

        await RunPipelineAsync(context, response);

        var header = context.Response.Headers["Server-Timing"].ToString();
        Assert.Contains("db;dur=12.3", header, StringComparison.Ordinal);
    }

    private sealed class FakeMetrics : IServerTimingMetrics
    {
        public IEnumerable<string> GetEntries() => ["db;dur=12.3"];
    }
}
