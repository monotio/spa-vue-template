using System.Diagnostics.Metrics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace VueApp1.Server.Agent;

/// <summary>Exactly one of these per provider call — the 1:1 reconciliation unit.</summary>
public sealed record AgentUsageEntry(
    DateTimeOffset TimestampUtc,
    string Provider,
    string Model,
    string ConversationId,
    Guid TurnId,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long ReasoningTokens,
    decimal EstimatedUsd,
    string Outcome);

public sealed record AgentUsageSummary(
    decimal TotalUsd,
    decimal TodayUsd,
    decimal DailyUsdBudget,
    DateTimeOffset ResetAtUtc,
    long TotalInputTokens,
    long TotalOutputTokens,
    int EntryCount,
    IReadOnlyList<AgentUsageEntry> RecentEntries);

/// <summary>
/// The visible-spend ledger, distilled from a real invisible-spend incident:
/// a production system whose streaming paths bypassed accounting recorded
/// only a small fraction of actual LLM spend — the gap surfaced on the
/// vendor BILL, months and five figures later. The rules that incident
/// bought, all enforced here and pinned by tests:
/// <list type="bullet">
/// <item>exactly ONE entry per provider call, written in the loop's
/// <c>finally</c> — a client abort still bills, so it still ledgers;</item>
/// <item>null usage is tolerated and still counted (a missing-usage call is
/// an entry with zero tokens, not a missing entry);</item>
/// <item>budgets gate BEFORE turns, never mid-stream — "if it started, let
/// it finish" (partial output is worse than one bounded overrun);</item>
/// <item>spend is visible within minutes of first clone:
/// <c>GET /api/agent/usage</c>, the terminal <c>usage</c> part, and OTel
/// metrics (prompt text never recorded).</item>
/// </list>
/// In-memory and process-local (honest banner): entries vanish on restart.
/// Durable upgrade: one DB row per entry, reconciled against the vendor bill.
/// </summary>
public sealed class AgentUsageLedger : IDisposable
{
    // Instance-owned meter (this is a singleton): rides the existing
    // "VueApp1.*" wildcard in SetupTelemetry. GenAI semconv attribute names
    // are still experimental upstream — keep them, but expect renames.
    private readonly Meter _meter = new("VueApp1.Agent");
    private readonly Counter<long> _inputTokens;
    private readonly Counter<long> _outputTokens;
    private readonly Histogram<double> _callCostUsd;

    private readonly object _gate = new();
    private readonly List<AgentUsageEntry> _entries = [];
    private readonly AgentOptions _options;
    private readonly TimeProvider _timeProvider;

    public AgentUsageLedger(IOptions<AgentOptions> options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _timeProvider = timeProvider;
        _inputTokens = _meter.CreateCounter<long>("vueapp1.agent.input_tokens");
        _outputTokens = _meter.CreateCounter<long>("vueapp1.agent.output_tokens");
        _callCostUsd = _meter.CreateHistogram<double>("vueapp1.agent.call_cost_usd");
    }

    public AgentUsageEntry Record(
        string provider,
        string model,
        string conversationId,
        Guid turnId,
        UsageDetails? usage,
        string outcome)
    {
        var entry = new AgentUsageEntry(
            TimestampUtc: _timeProvider.GetUtcNow(),
            Provider: provider,
            Model: model,
            ConversationId: conversationId,
            TurnId: turnId,
            InputTokens: usage?.InputTokenCount ?? 0,
            CachedInputTokens: usage?.CachedInputTokenCount ?? 0,
            OutputTokens: usage?.OutputTokenCount ?? 0,
            ReasoningTokens: usage?.ReasoningTokenCount ?? 0,
            EstimatedUsd: Estimate(model, usage),
            Outcome: outcome);

        lock (_gate)
        {
            _entries.Add(entry);
        }

        KeyValuePair<string, object?>[] tags =
        [
            new("gen_ai.provider.name", provider),
            new("gen_ai.request.model", model),
        ];
        _inputTokens.Add(entry.InputTokens, tags);
        _outputTokens.Add(entry.OutputTokens, tags);
        _callCostUsd.Record((double)entry.EstimatedUsd, tags);

        return entry;
    }

    /// <summary>Soft daily preflight: true once today's recorded spend reaches the budget.</summary>
    public bool IsDailyBudgetExhausted(out DateTimeOffset resetAtUtc)
    {
        var now = _timeProvider.GetUtcNow();
        var todayStartUtc = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        resetAtUtc = todayStartUtc.AddDays(1);
        return SpentSince(todayStartUtc) >= _options.DailyUsdBudget;
    }

    public AgentUsageSummary GetSummary()
    {
        var now = _timeProvider.GetUtcNow();
        var todayStartUtc = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        lock (_gate)
        {
            return new AgentUsageSummary(
                TotalUsd: _entries.Sum(entry => entry.EstimatedUsd),
                TodayUsd: _entries.Where(entry => entry.TimestampUtc >= todayStartUtc).Sum(entry => entry.EstimatedUsd),
                DailyUsdBudget: _options.DailyUsdBudget,
                ResetAtUtc: todayStartUtc.AddDays(1),
                TotalInputTokens: _entries.Sum(entry => entry.InputTokens),
                TotalOutputTokens: _entries.Sum(entry => entry.OutputTokens),
                EntryCount: _entries.Count,
                RecentEntries: [.. _entries.TakeLast(100)]);
        }
    }

    public IReadOnlyList<AgentUsageEntry> GetEntries()
    {
        lock (_gate)
        {
            return [.. _entries];
        }
    }

    public void Dispose() => _meter.Dispose();

    private decimal SpentSince(DateTimeOffset thresholdUtc)
    {
        lock (_gate)
        {
            return _entries
                .Where(entry => entry.TimestampUtc >= thresholdUtc)
                .Sum(entry => entry.EstimatedUsd);
        }
    }

    private decimal Estimate(string model, UsageDetails? usage)
    {
        if (usage is null || !_options.Pricing.TryGetValue(model, out var pricing))
        {
            return 0m;
        }

        // Cached input is the cheaper subset of input; reasoning tokens are
        // already included in OutputTokenCount by the adapters, so output is
        // billed once at the output rate (no double count).
        var input = usage.InputTokenCount ?? 0;
        var cached = Math.Min(usage.CachedInputTokenCount ?? 0, input);
        var output = usage.OutputTokenCount ?? 0;
        return ((input - cached) * pricing.InputPerMTokUsd
            + cached * pricing.CachedInputPerMTokUsd
            + output * pricing.OutputPerMTokUsd) / 1_000_000m;
    }
}
