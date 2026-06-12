using System.ClientModel;
using Anthropic;
using Microsoft.Extensions.AI;
using OpenAI;

namespace VueApp1.Server.Agent;

/// <summary>
/// The ONLY file that touches a provider SDK. <c>Agent:Provider</c> selects
/// the <see cref="IChatClient"/> once at startup — pinned per process, never
/// switched mid-request — and everything past this seam (the loop, the
/// policy, the ledger, the tests) codes against the abstraction.
///
/// Key resolution is "clone + one env var = working agent": the standard
/// <c>ANTHROPIC_API_KEY</c> / <c>OPENAI_API_KEY</c> environment variables,
/// read through <see cref="IConfiguration"/> so user-secrets (docs/CONFIG.md)
/// and test overrides resolve through the same lookup. No key when the module
/// is enabled (and no pre-registered <see cref="IChatClient"/> overriding
/// this factory) fails BOOT with a precise message — never a 500 on the
/// first turn. The check is ours on purpose: a keyless
/// <c>AnthropicClient</c> constructs fine and only fails on the first call
/// (lazy env auto-resolution), which is exactly the late failure mode the
/// fail-fast posture forbids.
///
/// Per-provider divergences behind the shared interface (doc notes, not a
/// capabilities abstraction — nothing to branch on with two adapters; see
/// docs/AGENT.md "Provider notes"):
///  - Reasoning round-trip (P4): the Anthropic adapter carries thinking
///    signatures and the OpenAI adapter encrypted reasoning, both inside
///    <c>TextReasoningContent.ProtectedData</c> — opaque here, by rule.
///  - Prompt caching: OpenAI caches long stable prefixes implicitly;
///    Anthropic wants explicit cache_control breakpoints, which its adapter
///    reads from <c>AdditionalProperties["anthropic:cache_control"]</c>
///    (set via the SDK's <c>WithCacheControl</c> extension).
///  - PDF input: both adapters map <c>DataContent</c> with
///    <c>application/pdf</c> natively (Anthropic document blocks, OpenAI
///    file inputs) — relevant once attachments land.
/// </summary>
public static class AgentChatClientFactory
{
    public const string AnthropicApiKeyName = "ANTHROPIC_API_KEY";
    public const string OpenAIApiKeyName = "OPENAI_API_KEY";

    /// <summary>
    /// Optional endpoint overrides under the SDKs' standard env-var names,
    /// resolved through the same <see cref="IConfiguration"/> lookup as the
    /// keys. Point them at a gateway/proxy — or, as the integration tests'
    /// provider-boot factories do, at an unroutable loopback address so any
    /// accidental provider call fails instantly instead of leaving the
    /// machine. Unset means each SDK's production endpoint.
    /// </summary>
    public const string AnthropicBaseUrlName = "ANTHROPIC_BASE_URL";
    public const string OpenAIBaseUrlName = "OPENAI_BASE_URL";

    /// <summary>
    /// Builds the provider-backed <see cref="IChatClient"/>. Construction is
    /// offline — no network until the loop's first
    /// <c>GetStreamingResponseAsync</c> — so this is safe to run (and the
    /// validator deliberately DOES run it) during startup validation.
    /// </summary>
    public static IChatClient Create(AgentOptions options, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);

        return options.Provider.ToUpperInvariant() switch
        {
            "ANTHROPIC" => CreateAnthropic(options, configuration),
            "OPENAI" => CreateOpenAI(options, configuration),
            // The options validator rejects unknown providers before this
            // runs; this guard keeps the factory honest standalone.
            _ => throw new InvalidOperationException(
                $"Agent:Provider must be 'anthropic' or 'openai' (got '{options.Provider}')."),
        };
    }

    private static IChatClient CreateAnthropic(AgentOptions options, IConfiguration configuration)
    {
        var apiKey = RequireApiKey(configuration, AnthropicApiKeyName, "anthropic");
        var model = RequireModel(options.Anthropic.Model, "Agent:Anthropic:Model");
        var baseUrl = configuration[AnthropicBaseUrlName];

        // The official Anthropic-authored SDK's native MEAI adapter. The key
        // is passed explicitly (not left to the SDK's lazy env resolution) so
        // a missing key fails above, at boot, with our message.
        var client = string.IsNullOrWhiteSpace(baseUrl)
            ? new AnthropicClient { ApiKey = apiKey }
            : new AnthropicClient { ApiKey = apiKey, BaseUrl = baseUrl };
        return client.AsIChatClient(model);
    }

    private static IChatClient CreateOpenAI(AgentOptions options, IConfiguration configuration)
    {
        var apiKey = RequireApiKey(configuration, OpenAIApiKeyName, "openai");
        var model = RequireModel(options.OpenAI.Model, "Agent:OpenAI:Model");
        var baseUrl = configuration[OpenAIBaseUrlName];

        // Official OpenAI SDK → Chat Completions client for the configured
        // model → Microsoft.Extensions.AI.OpenAI adapter over it.
        var client = string.IsNullOrWhiteSpace(baseUrl)
            ? new OpenAIClient(apiKey)
            : new OpenAIClient(
                new ApiKeyCredential(apiKey),
                new OpenAIClientOptions { Endpoint = new Uri(baseUrl) });
        return client.GetChatClient(model).AsIChatClient();
    }

    /// <summary>
    /// Env vars surface through the default host configuration under their
    /// verbatim names, so one <see cref="IConfiguration"/> lookup covers the
    /// environment, user-secrets, and test in-memory overrides alike.
    /// </summary>
    private static string RequireApiKey(IConfiguration configuration, string keyName, string provider)
    {
        var apiKey = configuration[keyName];
        return string.IsNullOrWhiteSpace(apiKey)
            ? throw new InvalidOperationException(
                $"Agent:Enabled is true and Agent:Provider is '{provider}', but no API key was found. "
                + $"Set the {keyName} environment variable (or store it via user-secrets — docs/CONFIG.md), "
                + "or pre-register an IChatClient in DI (how the tests inject their scripted client). "
                + "See docs/AGENT.md.")
            : apiKey;
    }

    private static string RequireModel(string model, string configPath) =>
        string.IsNullOrWhiteSpace(model)
            ? throw new InvalidOperationException(
                $"{configPath} must be a non-empty model name — the built-in default was explicitly "
                + "blanked in configuration. See docs/AGENT.md for the defaults.")
            : model;
}
