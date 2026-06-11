using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;
using VueApp1.Server.Mcp;
using Xunit;

namespace VueApp1.Server.UnitTests.Mcp;

public class ValidateOriginFilterTests
{
    // Host-matching semantics deliberately mirror ASP.NET Core host
    // filtering: wildcard entries match subdomains but never the apex, and
    // comparison ignores scheme, port and case.
    [Theory]
    [InlineData("https://anything.example", "*", true)]
    [InlineData("https://anything.example", null, true)]
    [InlineData("https://anything.example", "   ", true)]
    [InlineData("https://app.example.com", "example.com;app.example.com", true)]
    [InlineData("https://evil.test", "example.com", false)]
    [InlineData("https://api.example.com", "*.example.com", true)]
    [InlineData("https://example.com", "*.example.com", false)]
    [InlineData("https://api.example.com", ".example.com", true)]
    [InlineData("https://EXAMPLE.com", "example.com", true)]
    [InlineData("https://example.com:5173", "example.com", true)]
    [InlineData("http://example.com", "example.com", true)]
    [InlineData("https://example.com.evil.test", "example.com", false)]
    [InlineData("https://b.example", "a.example; b.example", true)]
    [InlineData("null", "example.com", false)]
    [InlineData("not a url", "example.com", false)]
    public void IsOriginAllowed_MatchesHostFilteringSemantics(
        string origin, string? allowedHosts, bool expected)
    {
        Assert.Equal(expected, ValidateOriginFilter.IsOriginAllowed(origin, allowedHosts));
    }

    [Fact]
    public async Task RequestWithoutOrigin_PassesThrough()
    {
        // Non-browser clients (MCP CLIs, agents) send no Origin header — the
        // filter must never block them, even with a restrictive allowlist.
        var filter = CreateFilter(allowedHosts: "example.com");
        var context = CreateContext(origin: null);
        var nextCalled = false;

        var result = await filter.InvokeAsync(context, _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>("handled");
        });

        Assert.True(nextCalled);
        Assert.Equal("handled", result);
    }

    [Fact]
    public async Task DisallowedOrigin_ShortCircuitsWithProblem403()
    {
        var filter = CreateFilter(allowedHosts: "example.com");
        var context = CreateContext(origin: "https://attacker.test");
        var nextCalled = false;

        var result = await filter.InvokeAsync(context, _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>("handled");
        });

        Assert.False(nextCalled);
        var problem = Assert.IsType<ProblemHttpResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, problem.StatusCode);
        Assert.Equal("Origin not allowed.", problem.ProblemDetails.Title);
    }

    private static ValidateOriginFilter CreateFilter(string allowedHosts)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AllowedHosts"] = allowedHosts })
            .Build();
        return new ValidateOriginFilter(configuration);
    }

    private static DefaultEndpointFilterInvocationContext CreateContext(string? origin)
    {
        var httpContext = new DefaultHttpContext();
        if (origin is not null)
        {
            httpContext.Request.Headers.Origin = origin;
        }

        return new DefaultEndpointFilterInvocationContext(httpContext);
    }
}
