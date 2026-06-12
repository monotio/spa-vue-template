using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using VueApp1.Server.Agent;
using VueApp1.Server.Agent.Skills;
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

    [Fact]
    public void Enabled_McpServerToolNamedLoadSkill_FailsAsLoopReserved()
    {
        // The loop dispatches load_skill ITSELF, before consulting
        // AgentToolPolicy — an McpServerTool registered under that name would
        // be silently shadowed for the agent (annotations ignored, handler
        // never invoked) while still being served verbatim to external /mcp
        // clients. A split-brain tool is a boot failure, not a surprise.
        var services = new ServiceCollection();
        services.AddSingleton(McpServerTool.Create(
            () => "ok",
            new McpServerToolCreateOptions
            {
                Name = FileSystemSkillCatalog.LoadSkillToolName,
                Description = "imposter tool — registered ONLY in this test",
                ReadOnly = true,
                Destructive = false,
            }));
        var validator = BuildValidator(services, out var provider);
        using (provider)
        {
            var result = validator.Validate(name: null, new AgentOptions { Enabled = true });

            Assert.True(result.Failed);
            Assert.Contains(
                result.Failures!,
                failure => failure.Contains("load_skill", StringComparison.Ordinal)
                    && failure.Contains("reserved", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Disabled_McpServerToolNamedLoadSkill_PassesValidation()
    {
        // Flag off there is no loop to shadow: a deployment running ONLY the
        // external MCP server may name its tools whatever it likes.
        var services = new ServiceCollection();
        services.AddSingleton(McpServerTool.Create(
            () => "ok",
            new McpServerToolCreateOptions
            {
                Name = FileSystemSkillCatalog.LoadSkillToolName,
                Description = "imposter tool — registered ONLY in this test",
                ReadOnly = true,
                Destructive = false,
            }));
        var validator = BuildValidator(services, out var provider);
        using (provider)
        {
            var options = new AgentOptions
            {
                Enabled = false,
                Attachments = new AgentAttachmentOptions { AllowedContentTypes = ["image/png"] },
            };

            var result = validator.Validate(name: null, options);

            Assert.True(result.Succeeded);
        }
    }

    [Fact]
    public void AttachmentLimits_InvalidValues_FailWithTheConfigKeys()
    {
        var validator = BuildValidator([], out var provider);
        using (provider)
        {
            var options = new AgentOptions
            {
                Attachments = new AgentAttachmentOptions
                {
                    MaxBytes = 0,
                    MaxPerMessage = 0,
                    AllowedContentTypes = ["image/png", "not-a-media-type"],
                },
            };

            var result = validator.Validate(name: null, options);

            Assert.True(result.Failed);
            Assert.Contains(
                result.Failures!,
                failure => failure.Contains("Agent:Attachments:MaxBytes", StringComparison.Ordinal));
            Assert.Contains(
                result.Failures!,
                failure => failure.Contains("Agent:Attachments:MaxPerMessage", StringComparison.Ordinal));
            Assert.Contains(
                result.Failures!,
                failure => failure.Contains("'not-a-media-type'", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Attachments_EmptyAllowlist_FailsNamingTheKey()
    {
        // The code default is deliberately EMPTY (the binder merges into
        // non-empty defaults, making the allowlist impossible to narrow);
        // the shipped list lives in appsettings.json. Deleting it must be a
        // boot-time message, not an upload endpoint that accepts nothing.
        var validator = BuildValidator([], out var provider);
        using (provider)
        {
            var options = new AgentOptions
            {
                Attachments = new AgentAttachmentOptions { AllowedContentTypes = [] },
            };

            var result = validator.Validate(name: null, options);

            Assert.True(result.Failed);
            Assert.Contains(
                result.Failures!,
                failure => failure.Contains("Agent:Attachments:AllowedContentTypes", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Attachments_MaxBytesAboveIntMaxValue_FailsNamingTheKey()
    {
        // Attachments buffer in memory as byte arrays; a MaxBytes past
        // int.MaxValue would overflow the upload buffer's capacity cast at
        // runtime instead of failing boot with a precise message.
        var validator = BuildValidator([], out var provider);
        using (provider)
        {
            var options = new AgentOptions
            {
                Attachments = new AgentAttachmentOptions
                {
                    MaxBytes = (long)int.MaxValue + 1,
                    AllowedContentTypes = ["image/png"],
                },
            };

            var result = validator.Validate(name: null, options);

            Assert.True(result.Failed);
            Assert.Contains(
                result.Failures!,
                failure => failure.Contains("Agent:Attachments:MaxBytes", StringComparison.Ordinal));
        }
    }
}
