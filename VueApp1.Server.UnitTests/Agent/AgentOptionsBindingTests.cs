using Microsoft.Extensions.Configuration;
using VueApp1.Server.Agent;
using Xunit;

namespace VueApp1.Server.UnitTests.Agent;

/// <summary>
/// Pins that the attachment allowlist can be NARROWED via config. The
/// configuration binder MERGES bound collection entries into a pre-populated
/// code default (it can add, never remove), so a non-empty code default would
/// make tightening impossible: an operator who deletes application/pdf from
/// appsettings.json would silently keep accepting PDFs. The contract is
/// therefore: empty code default, appsettings.json owns the shipped list,
/// config is authoritative, and the validator rejects an empty result.
/// </summary>
public class AgentOptionsBindingTests
{
    [Fact]
    public void AllowedContentTypes_BoundFromConfig_ReplacesRatherThanMerges()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Agent:Attachments:AllowedContentTypes:0"] = "image/png",
            })
            .Build();

        var options = configuration.GetSection(AgentOptions.SectionName).Get<AgentOptions>(
            binder => binder.ErrorOnUnknownConfiguration = true)!;

        // Exactly the configured entry — no code-default leftovers merged in.
        var allowed = Assert.Single(options.Attachments.AllowedContentTypes);
        Assert.Equal("image/png", allowed);
        Assert.True(options.Attachments.IsAllowedContentType("image/png"));
        // The hardening case a binder merge would silently undo:
        Assert.False(options.Attachments.IsAllowedContentType("application/pdf"));
    }
}
