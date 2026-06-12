using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using VueApp1.Server.Agent;
using Xunit;

namespace VueApp1.Server.UnitTests.Agent;

/// <summary>
/// Pins the provider routing matrix and the fail-fast matrix with ZERO
/// network: every test constructs the adapter and inspects its
/// <see cref="ChatClientMetadata"/> — provider construction is offline by
/// design, and a call attempt against these fake keys would fail fast on
/// auth, not hang, but no test here ever issues one. Keys come exclusively
/// from in-memory <see cref="IConfiguration"/>, so a developer machine's
/// real ANTHROPIC_API_KEY / OPENAI_API_KEY can never leak in (or flake the
/// missing-key cases).
/// </summary>
public class AgentChatClientFactoryTests
{
    private static IConfiguration EmptyConfiguration => BuildConfiguration([]);

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static IConfiguration WithAnthropicKey =>
        BuildConfiguration(new() { [AgentChatClientFactory.AnthropicApiKeyName] = "test-key-not-real" });

    private static IConfiguration WithOpenAIKey =>
        BuildConfiguration(new() { [AgentChatClientFactory.OpenAIApiKeyName] = "test-key-not-real" });

    // -- Routing matrix -------------------------------------------------------

    [Fact]
    public void AnthropicProvider_BuildsAnthropicAdapter_WithConfiguredModel()
    {
        using var client = AgentChatClientFactory.Create(new AgentOptions(), WithAnthropicKey);

        var metadata = client.GetService<ChatClientMetadata>();
        Assert.NotNull(metadata);
        Assert.Equal("anthropic", metadata.ProviderName);
        // Default model per appsettings/AgentOptions — config-driven, not hardcoded downstream.
        Assert.Equal("claude-sonnet-4-5", metadata.DefaultModelId);
    }

    [Fact]
    public void OpenAIProvider_BuildsOpenAIAdapter_WithConfiguredModel()
    {
        var options = new AgentOptions { Provider = "openai" };

        using var client = AgentChatClientFactory.Create(options, WithOpenAIKey);

        var metadata = client.GetService<ChatClientMetadata>();
        Assert.NotNull(metadata);
        Assert.Equal("openai", metadata.ProviderName);
        Assert.Equal("gpt-5.2", metadata.DefaultModelId);
    }

    [Theory]
    [InlineData("anthropic", "claude-haiku-4-5")]
    [InlineData("openai", "gpt-5.2-mini")]
    public void ConfiguredModelName_FlowsIntoTheAdapter(string provider, string model)
    {
        var options = new AgentOptions
        {
            Provider = provider,
            Anthropic = new AgentProviderModelOptions { Model = model },
            OpenAI = new AgentProviderModelOptions { Model = model },
        };
        var configuration = provider == "anthropic" ? WithAnthropicKey : WithOpenAIKey;

        using var client = AgentChatClientFactory.Create(options, configuration);

        Assert.Equal(model, client.GetService<ChatClientMetadata>()?.DefaultModelId);
    }

    [Theory]
    [InlineData("Anthropic", "anthropic")]
    [InlineData("OPENAI", "openai")]
    public void ProviderName_MatchesCaseInsensitively_LikeTheOptionsValidator(string configured, string expected)
    {
        var options = new AgentOptions { Provider = configured };
        var configuration = BuildConfiguration(new()
        {
            [AgentChatClientFactory.AnthropicApiKeyName] = "test-key-not-real",
            [AgentChatClientFactory.OpenAIApiKeyName] = "test-key-not-real",
        });

        using var client = AgentChatClientFactory.Create(options, configuration);

        Assert.Equal(expected, client.GetService<ChatClientMetadata>()?.ProviderName);
    }

    // -- Endpoint overrides ----------------------------------------------------

    [Theory]
    [InlineData(
        "anthropic",
        AgentChatClientFactory.AnthropicApiKeyName,
        AgentChatClientFactory.AnthropicBaseUrlName)]
    [InlineData(
        "openai",
        AgentChatClientFactory.OpenAIApiKeyName,
        AgentChatClientFactory.OpenAIBaseUrlName)]
    public void BaseUrlOverride_FlowsIntoTheAdapter(string provider, string keyName, string baseUrlName)
    {
        // The seam the integration tests' provider-boot factories rely on for
        // their structural no-network guard (and what a gateway/proxy user
        // sets in production): the standard *_BASE_URL name repoints the SDK.
        var configuration = BuildConfiguration(new()
        {
            [keyName] = "test-key-not-real",
            [baseUrlName] = "https://127.0.0.1:9/gateway",
        });

        using var client = AgentChatClientFactory.Create(
            new AgentOptions { Provider = provider }, configuration);

        var metadata = client.GetService<ChatClientMetadata>();
        Assert.NotNull(metadata);
        Assert.Contains(
            "127.0.0.1",
            metadata.ProviderUri?.ToString() ?? string.Empty,
            StringComparison.Ordinal);
    }

    // -- Fail-fast matrix ------------------------------------------------------

    [Fact]
    public void MissingAnthropicKey_FailsFast_NamingTheEnvVar()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => AgentChatClientFactory.Create(new AgentOptions(), EmptyConfiguration));

        Assert.Contains(AgentChatClientFactory.AnthropicApiKeyName, exception.Message, StringComparison.Ordinal);
        Assert.Contains("docs/AGENT.md", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingOpenAIKey_FailsFast_NamingTheEnvVar()
    {
        var options = new AgentOptions { Provider = "openai" };

        var exception = Assert.Throws<InvalidOperationException>(
            () => AgentChatClientFactory.Create(options, EmptyConfiguration));

        Assert.Contains(AgentChatClientFactory.OpenAIApiKeyName, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WrongProvidersKey_DoesNotSatisfyTheSelectedProvider()
    {
        // An OPENAI_API_KEY must not let the anthropic provider boot.
        var exception = Assert.Throws<InvalidOperationException>(
            () => AgentChatClientFactory.Create(new AgentOptions(), WithOpenAIKey));

        Assert.Contains(AgentChatClientFactory.AnthropicApiKeyName, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void WhitespaceKey_FailsFast_SameAsMissing(string blank)
    {
        var configuration = BuildConfiguration(new() { [AgentChatClientFactory.AnthropicApiKeyName] = blank });

        Assert.Throws<InvalidOperationException>(
            () => AgentChatClientFactory.Create(new AgentOptions(), configuration));
    }

    [Fact]
    public void UnknownProvider_FailsFast_ListingTheValidValues()
    {
        var options = new AgentOptions { Provider = "azure" };

        var exception = Assert.Throws<InvalidOperationException>(
            () => AgentChatClientFactory.Create(options, EmptyConfiguration));

        Assert.Contains("anthropic", exception.Message, StringComparison.Ordinal);
        Assert.Contains("openai", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitlyBlankedModelName_FailsFast_NamingTheConfigPath()
    {
        var options = new AgentOptions { Anthropic = new AgentProviderModelOptions { Model = " " } };

        var exception = Assert.Throws<InvalidOperationException>(
            () => AgentChatClientFactory.Create(options, WithAnthropicKey));

        Assert.Contains("Agent:Anthropic:Model", exception.Message, StringComparison.Ordinal);
    }
}
