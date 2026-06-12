using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using VueApp1.Server.Agent.Skills;

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

    /// <summary>Upload caps for <c>POST /api/agent/attachments</c> (docs/AGENT.md "Attachments").</summary>
    public AgentAttachmentOptions Attachments { get; init; } = new();

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

/// <summary>
/// Attachment limits, enforced twice on purpose: at the upload endpoint
/// (typed ProblemDetails the composer mirrors client-side) and again inside
/// <see cref="Attachments.InMemoryAttachmentStore"/> (defense in depth — a
/// future caller cannot route around the endpoint checks).
/// </summary>
public sealed class AgentAttachmentOptions
{
    /// <summary>Per-attachment byte cap (default 5 MiB) — 413 above it.</summary>
    public long MaxBytes { get; init; } = 5 * 1024 * 1024;

    /// <summary>Max attachment references on one user message — 400 above it.</summary>
    public int MaxPerMessage { get; init; } = 4;

    /// <summary>
    /// Media-type allowlist — 415 outside it. Images and PDFs hydrate as
    /// <c>DataContent</c>; <c>text/*</c> inlines under the boundary-nonce
    /// frame (see <see cref="AgentMessageBuilder"/>). NOTE: the binder MERGES
    /// configured entries with these code defaults (the classic collection
    /// gotcha) — harmless here because matching dedupes through a set.
    /// </summary>
    public IReadOnlyList<string> AllowedContentTypes { get; init; } =
        ["image/png", "image/jpeg", "image/webp", "image/gif", "application/pdf", "text/plain", "text/markdown"];

    private HashSet<string>? _allowedSet;

    public bool IsAllowedContentType(string? contentType)
    {
        // Benign race: concurrent first calls build identical sets.
        var allowed = _allowedSet ??= [.. AllowedContentTypes
            .Select(NormalizeMediaType)
            .OfType<string>()];
        return NormalizeMediaType(contentType) is { } normalized && allowed.Contains(normalized);
    }

    /// <summary>
    /// Canonical media type: parameters stripped (<c>text/plain; charset=utf-8</c>
    /// → <c>text/plain</c>), trimmed, lowercased. Null when unusable.
    /// </summary>
    public static string? NormalizeMediaType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        var separatorIndex = contentType.IndexOf(';', StringComparison.Ordinal);
        var value = (separatorIndex >= 0 ? contentType[..separatorIndex] : contentType).Trim();
        return value.Length == 0 || !value.Contains('/', StringComparison.Ordinal)
            ? null
            : value.ToLowerInvariant();
    }
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

        if (options.Attachments.MaxBytes < 1)
        {
            failures.Add("Agent:Attachments:MaxBytes must be at least 1.");
        }

        if (options.Attachments.MaxPerMessage < 1)
        {
            failures.Add("Agent:Attachments:MaxPerMessage must be at least 1.");
        }

        foreach (var contentType in options.Attachments.AllowedContentTypes)
        {
            if (AgentAttachmentOptions.NormalizeMediaType(contentType) is null)
            {
                failures.Add(
                    $"Agent:Attachments:AllowedContentTypes entry '{contentType}' is not a media type "
                    + "(expected type/subtype, e.g. image/png).");
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
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // AgentChatClientFactory's fail-fast (no API key for the
                // selected provider, blanked model name) surfaces during the
                // resolve above as InvalidOperationException — but a cloner's
                // pre-registered IChatClient may throw anything from its own
                // factory. Every resolution failure here is a boot-blocking
                // wiring problem, so the catch is deliberately wide: report
                // it through the aggregated ValidateOptionsResult (keeping
                // any other queued failures visible) instead of aborting
                // validation with a raw exception.
                failures.Add(exception.Message);
            }

            try
            {
                // Shipped skills validate at BOOT, not at first use: resolving
                // the catalog parses every Agent/Skills/*/SKILL.md, and a
                // malformed file dies here with its path and reason instead
                // of 500ing someone's first turn. Resolved only when the
                // module is enabled — flag-off stays zero-cost.
                _ = serviceProvider.GetService<FileSystemSkillCatalog>();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                failures.Add(exception.Message);
            }
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
