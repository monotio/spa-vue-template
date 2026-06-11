using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using VueApp1.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace VueApp1.Server.IntegrationTests;

/// <summary>
/// Pins the fail-fast boot guarantee from docs/CONFIG.md: an unknown key
/// under a validated section (e.g. a typo'd rate-limit setting) must kill
/// host startup with a message naming the offending key, not silently fall
/// back to defaults. This guards the <c>ErrorOnUnknownConfiguration</c>
/// binder flag in Program.cs — the one fail-fast promise the validator unit
/// tests cannot cover, and one a refactor back to a bare <c>Get&lt;T&gt;()</c>
/// would silently drop.
/// </summary>
public class ConfigurationFailFastTests(IntegrationTestWebApplicationFactory factory)
    : IClassFixture<IntegrationTestWebApplicationFactory>
{
    [Fact]
    public void UnknownPerformanceKey_FailsBoot_NamingTheOffendingKey()
    {
        var brokenFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["Performance:Bogus"] = "1" })));

        var exception = Assert.ThrowsAny<Exception>(() => brokenFactory.CreateClient());

        Assert.Contains("Bogus", FlattenMessages(exception), StringComparison.Ordinal);
    }

    private static string FlattenMessages(Exception exception)
    {
        var messages = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        return string.Join(" | ", messages);
    }
}
