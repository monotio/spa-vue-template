using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using VueApp1.Server.Agent;
using Xunit;

namespace VueApp1.Server.UnitTests.Agent;

/// <summary>
/// Pins the incident-bought ledger rules: 1:1 entries, null-usage tolerance,
/// the pricing math, and the TimeProvider-driven UTC-midnight budget
/// boundary (assertions are timezone-safe — the test process runs pinned to
/// Etc/GMT-5, and the math must not care).
/// </summary>
public class AgentUsageLedgerTests
{
    private static readonly Guid _turnId = Guid.NewGuid();

    [Fact]
    public void Record_WritesExactlyOneEntryPerCall()
    {
        var (ledger, _) = CreateLedger();

        ledger.Record("anthropic", "m1", "c1", _turnId, Usage(10, 5), "completed");
        ledger.Record("anthropic", "m1", "c1", _turnId, Usage(20, 5), "completed");

        Assert.Equal(2, ledger.GetEntries().Count);
    }

    [Fact]
    public void Record_NullUsage_IsToleratedAndStillCounted()
    {
        // A provider call that never surfaced usage still happened and may
        // still have billed — the entry exists with zero tokens, it is not
        // silently dropped.
        var (ledger, _) = CreateLedger();

        var entry = ledger.Record("anthropic", "m1", "c1", _turnId, usage: null, "error");

        var stored = Assert.Single(ledger.GetEntries());
        Assert.Equal(entry, stored);
        Assert.Equal(0, stored.InputTokens);
        Assert.Equal(0m, stored.EstimatedUsd);
        Assert.Equal("error", stored.Outcome);
    }

    [Fact]
    public void Estimate_PricesCachedInputAtTheCheaperRate_AndOutputOnce()
    {
        var (ledger, _) = CreateLedger();

        // 1M input of which 200k cached, 100k output of which 50k reasoning:
        // (800k * 3.0 + 200k * 0.3 + 100k * 15.0) / 1M = 2.4 + 0.06 + 1.5.
        // Reasoning is a SUBSET of output in the adapters' accounting, so it
        // is not billed a second time.
        var entry = ledger.Record(
            "anthropic", "priced-model", "c1", _turnId,
            Usage(1_000_000, 100_000, cached: 200_000, reasoning: 50_000), "completed");

        Assert.Equal(3.96m, entry.EstimatedUsd);
    }

    [Fact]
    public void Estimate_UnknownModel_CostsZeroButTokensAreRecorded()
    {
        var (ledger, _) = CreateLedger();

        var entry = ledger.Record("anthropic", "unpriced-model", "c1", _turnId, Usage(1_000, 50), "completed");

        Assert.Equal(0m, entry.EstimatedUsd);
        Assert.Equal(1_000, entry.InputTokens);
        Assert.Equal(50, entry.OutputTokens);
    }

    [Fact]
    public void DailyBudget_UsesUtcMidnightBoundary_FromTimeProvider()
    {
        // Local wall clock 03:30 at UTC+5 — still the PREVIOUS day in UTC.
        // The boundary must come from UtcDateTime, never the local date.
        var start = new DateTimeOffset(2026, 6, 12, 3, 30, 0, TimeSpan.FromHours(5));
        var (ledger, clock) = CreateLedger(dailyBudget: 5m);

        clock.Now = start;
        ledger.Record(
            "anthropic", "priced-model", "c1", _turnId, Usage(2_000_000, 0), "completed"); // 6 USD

        Assert.True(ledger.IsDailyBudgetExhausted(out var resetAtUtc));
        Assert.Equal(new DateTimeOffset(2026, 6, 12, 0, 0, 0, TimeSpan.Zero), resetAtUtc);

        // Cross UTC midnight: yesterday's spend no longer counts.
        clock.Now = new DateTimeOffset(2026, 6, 12, 5, 30, 0, TimeSpan.FromHours(5)); // 00:30 UTC
        Assert.False(ledger.IsDailyBudgetExhausted(out resetAtUtc));
        Assert.Equal(new DateTimeOffset(2026, 6, 13, 0, 0, 0, TimeSpan.Zero), resetAtUtc);
    }

    [Fact]
    public void Summary_SplitsTodayFromTotal()
    {
        var (ledger, clock) = CreateLedger();

        clock.Now = new DateTimeOffset(2026, 6, 10, 12, 0, 0, TimeSpan.Zero);
        ledger.Record("anthropic", "priced-model", "c1", _turnId, Usage(1_000_000, 0), "completed"); // 3 USD
        clock.Now = new DateTimeOffset(2026, 6, 12, 12, 0, 0, TimeSpan.Zero);
        ledger.Record("anthropic", "priced-model", "c2", _turnId, Usage(1_000_000, 0), "completed"); // 3 USD

        var summary = ledger.GetSummary();

        Assert.Equal(6m, summary.TotalUsd);
        Assert.Equal(3m, summary.TodayUsd);
        Assert.Equal(2, summary.EntryCount);
        Assert.Equal(25m, summary.DailyUsdBudget);
    }

    // -----------------------------------------------------------------------

    private static UsageDetails Usage(long input, long output, long cached = 0, long reasoning = 0) => new()
    {
        InputTokenCount = input,
        OutputTokenCount = output,
        CachedInputTokenCount = cached,
        ReasoningTokenCount = reasoning,
    };

    private static (AgentUsageLedger Ledger, MutableTimeProvider Clock) CreateLedger(decimal dailyBudget = 25m)
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 6, 12, 12, 0, 0, TimeSpan.Zero));
        var options = new AgentOptions
        {
            DailyUsdBudget = dailyBudget,
            Pricing = new Dictionary<string, AgentModelPricing>
            {
                ["priced-model"] = new()
                {
                    InputPerMTokUsd = 3.0m,
                    CachedInputPerMTokUsd = 0.3m,
                    OutputPerMTokUsd = 15.0m,
                },
            },
        };
        return (new AgentUsageLedger(Options.Create(options), clock), clock);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
