using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using VueApp1.Server.Agent;
using Xunit;

namespace VueApp1.Server.UnitTests.Agent;

/// <summary>
/// Pins the validator's IChatClient resolution hook: ANY construction
/// failure of the registered client is a boot-blocking wiring problem and
/// must surface through the aggregated <c>ValidateOptionsResult</c> — never
/// as a raw exception that aborts validation and masks the other queued
/// failures. (The boot-level matrix lives in AgentEndpointTests; this pins
/// the aggregation contract itself.)
/// </summary>
public class AgentOptionsValidatorTests
{
    private static AgentOptionsValidator BuildValidator(
        ServiceCollection services, out ServiceProvider provider)
    {
        provider = services.BuildServiceProvider();
        return new AgentOptionsValidator(provider);
    }

    [Fact]
    public void Enabled_ClientFactoryThrowingNonInvalidOperation_ReportsAggregatedFailures()
    {
        // A cloner's pre-registered IChatClient whose own factory throws
        // something other than the AgentChatClientFactory's
        // InvalidOperationException (here: ArgumentException).
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(_ =>
            throw new ArgumentException("pre-registered client exploded in its own factory"));
        var validator = BuildValidator(services, out var provider);
        using (provider)
        {
            var options = new AgentOptions { Enabled = true, MaxTurnsPerRequest = 0 };

            var result = validator.Validate(name: null, options);

            Assert.True(result.Failed);
            Assert.Contains(
                result.Failures!,
                failure => failure.Contains("exploded", StringComparison.Ordinal));
            // The aggregation contract: the resolve failure must not mask the
            // other queued validation failures.
            Assert.Contains(
                result.Failures!,
                failure => failure.Contains("MaxTurnsPerRequest", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Enabled_NoClientRegistered_FailsNamingTheWiring()
    {
        var validator = BuildValidator([], out var provider);
        using (provider)
        {
            var result = validator.Validate(name: null, new AgentOptions { Enabled = true });

            Assert.True(result.Failed);
            Assert.Contains(
                result.Failures!,
                failure => failure.Contains("IChatClient", StringComparison.Ordinal));
        }
    }
}
