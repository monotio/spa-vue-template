using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace VueApp1.Server.Agent;

/// <summary>
/// Config for the opt-in agent module (section <c>Agent</c>). Bound with
/// <c>ErrorOnUnknownConfiguration</c> + <c>ValidateOnStart()</c> like every
/// other options class here: a typo'd key or an absurd guard value kills boot
/// with a precise message instead of silently running an expensive loop on
/// defaults. The guard knobs mirror the names of MEAI's
/// <c>FunctionInvokingChatClient</c> (turn cap, consecutive-error cap) so
/// collapsing onto the middleware later is a rename, not a redesign.
/// </summary>
public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    public bool Enabled { get; init; }

    /// <summary>"anthropic" or "openai" — pinned per process, never switched mid-request.</summary>
    public string Provider { get; init; } = "anthropic";

    public AgentProviderModelOptions Anthropic { get; init; } = new() { Model = "claude-sonnet-4-5" };

    public AgentProviderModelOptions OpenAI { get; init; } = new() { Model = "gpt-5.2" };

    /// <summary>
    /// Per-model pricing used for the ledger's <c>EstimatedUsd</c>. Models
    /// missing here cost 0 in the estimate (the token counts are still
    /// recorded) — reconcile against the vendor bill, not against this map.
    /// </summary>
    public Dictionary<string, AgentModelPricing> Pricing { get; init; } = [];

    /// <summary>Max provider calls per HTTP request; the final one is forced to answer in text.</summary>
    public int MaxTurnsPerRequest { get; init; } = 10;

    /// <summary>Consecutive failed tool calls before the loop stops feeding errors back.</summary>
    public int MaxConsecutiveToolErrors { get; init; } = 3;

    /// <summary>Per-request spend ceiling, checked BETWEEN turns (a started call always finishes).</summary>
    public decimal MaxRequestUsd { get; init; } = 0.50m;

    /// <summary>Soft daily budget: preflight gate before turn 0, never a mid-flight kill.</summary>
    public decimal DailyUsdBudget { get; init; } = 25m;

    /// <summary>
    /// Safe-by-default: non-destructive write tools require human approval.
    /// Loosen knowingly; destructive and unannotated tools ALWAYS require it.
    /// </summary>
    public bool RequireApprovalForWrites { get; init; } = true;

    /// <summary>The model name for the configured provider (ledger attribution).</summary>
    public string SelectedModel =>
        string.Equals(Provider, "openai", StringComparison.OrdinalIgnoreCase)
            ? OpenAI.Model
            : Anthropic.Model;
}

public sealed class AgentProviderModelOptions
{
    public string Model { get; init; } = string.Empty;
}

public sealed class AgentModelPricing
{
    public decimal InputPerMTokUsd { get; init; }
    public decimal CachedInputPerMTokUsd { get; init; }
    public decimal OutputPerMTokUsd { get; init; }
}

/// <summary>
/// Value sanity plus the zero-secrets boot contract: when the module is
/// enabled, resolving <see cref="IChatClient"/> must SUCCEED at validation
/// time. That resolution hits either a pre-registered client (exactly how
/// the integration tests inject their scripted <c>FakeChatClient</c> via
/// <c>ConfigureServices</c> — last registration wins) or the
/// <see cref="AgentChatClientFactory"/> registration from
/// <c>SetupAgent</c>, whose own fail-fast (enabled + no key) is converted
/// into a validation failure here. Either way the failure is a boot-time
/// message naming the fix, not a 500 on the first turn.
/// </summary>
public sealed class AgentOptionsValidator(IServiceProvider serviceProvider) : IValidateOptions<AgentOptions>
{
    private static readonly string[] _knownProviders = ["anthropic", "openai"];

    public ValidateOptionsResult Validate(string? name, AgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> failures = [];

        if (!_knownProviders.Contains(options.Provider, StringComparer.OrdinalIgnoreCase))
        {
            failures.Add($"Agent:Provider must be one of: {string.Join(", ", _knownProviders)} (got '{options.Provider}').");
        }

        if (options.MaxTurnsPerRequest < 1)
        {
            failures.Add("Agent:MaxTurnsPerRequest must be at least 1.");
        }

        if (options.MaxConsecutiveToolErrors < 1)
        {
            failures.Add("Agent:MaxConsecutiveToolErrors must be at least 1.");
        }

        if (options.MaxRequestUsd <= 0)
        {
            failures.Add("Agent:MaxRequestUsd must be positive — it is the per-request spend ceiling.");
        }

        if (options.DailyUsdBudget <= 0)
        {
            failures.Add("Agent:DailyUsdBudget must be positive — it is the daily preflight gate.");
        }

        foreach (var (model, pricing) in options.Pricing)
        {
            if (pricing.InputPerMTokUsd < 0 || pricing.CachedInputPerMTokUsd < 0 || pricing.OutputPerMTokUsd < 0)
            {
                failures.Add($"Agent:Pricing:{model} rates must be non-negative.");
            }
        }

        if (options.Enabled)
        {
            try
            {
                if (serviceProvider.GetService<IChatClient>() is null)
                {
                    failures.Add(
                        "Agent:Enabled is true but no IChatClient is registered. SetupAgent normally "
                        + "registers the AgentChatClientFactory-backed client; either re-register one "
                        + "in DI (tests pre-register a scripted client) or restore that wiring. "
                        + "See docs/AGENT.md.");
                }
            }
            catch (InvalidOperationException exception)
            {
                // AgentChatClientFactory's fail-fast (no API key for the
                // selected provider, blanked model name) surfaces during the
                // resolve above; report it as the validation failure so boot
                // dies with the factory's precise, actionable message.
                failures.Add(exception.Message);
            }
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
