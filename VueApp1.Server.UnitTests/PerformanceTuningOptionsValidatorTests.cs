using Xunit;

namespace VueApp1.Server.UnitTests;

public class PerformanceTuningOptionsValidatorTests
{
    [Fact]
    public void Validate_PassesCommittedDefaults_AndListsEveryCrossFieldViolation()
    {
        var validator = new PerformanceTuningOptionsValidator();

        // The committed defaults must stay boot-valid: the template's design
        // goal is that placeholder config boots the backend with zero secrets.
        Assert.True(validator.Validate(name: null, new PerformanceTuningOptions()).Succeeded);

        var hostile = new PerformanceTuningOptions
        {
            // Cross-field: a single cached body larger than the whole cache.
            OutputCache = new OutputCachingSettings { MaximumBodySizeBytes = 8, SizeLimitBytes = 4 },
            // Range: a rate-limit window of zero seconds.
            RateLimiting = new RateLimitingSettings { WindowSeconds = 0 },
            // Cross-field: the "long-running" policy shorter than the default.
            RequestTimeout = new RequestTimeoutSettings
            {
                Enabled = true,
                DefaultTimeoutSeconds = 30,
                LongRunningTimeoutSeconds = 5,
            },
        };

        var result = validator.Validate(name: null, hostile);

        Assert.True(result.Failed);
        Assert.NotNull(result.Failures);
        // All violations are reported in one pass (no short-circuit), each
        // naming the configuration key to fix.
        Assert.Contains(result.Failures, f =>
            f.Contains("Performance:OutputCache:MaximumBodySizeBytes", StringComparison.Ordinal));
        Assert.Contains(result.Failures, f =>
            f.Contains("Performance:RateLimiting:WindowSeconds", StringComparison.Ordinal));
        Assert.Contains(result.Failures, f =>
            f.Contains("Performance:RequestTimeout:LongRunningTimeoutSeconds", StringComparison.Ordinal));
    }
}
