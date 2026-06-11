using Microsoft.Extensions.Options;

namespace VueApp1.Server;

/// <summary>
/// Config-driven performance tuning (section <c>Performance</c>): Kestrel
/// request limits, output caching, rate limiting and request timeouts.
/// Program.cs binds this eagerly (the values configure the host itself) and
/// also registers it with <c>ValidateOnStart()</c>, so invalid config kills
/// boot with a precise message instead of silently running defaults.
/// </summary>
public sealed class PerformanceTuningOptions
{
    public const string SectionName = "Performance";
    public OutputCachingSettings OutputCache { get; init; } = new();
    public RateLimitingSettings RateLimiting { get; init; } = new();
    public RequestTimeoutSettings RequestTimeout { get; init; } = new();
    public RequestLimitSettings RequestLimits { get; init; } = new();
}

public sealed class RequestLimitSettings
{
    public long MaxRequestBodySizeBytes { get; init; } = 10 * 1024 * 1024;
    public double MinBodyDataRateBytesPerSecond { get; init; } = 100;
    public int MinBodyDataRateGraceSeconds { get; init; } = 10;
}

public sealed class OutputCachingSettings
{
    public int DefaultExpirationSeconds { get; init; } = 30;
    public int ReadExpirationSeconds { get; init; } = 30;
    public long SizeLimitBytes { get; init; } = 64 * 1024 * 1024;
    public long MaximumBodySizeBytes { get; init; } = 4 * 1024 * 1024;
}

public sealed class RateLimitingSettings
{
    public int PermitLimit { get; init; } = 5000;
    public int QueueLimit { get; init; } = 100;
    public int WindowSeconds { get; init; } = 60;
}

public sealed class RequestTimeoutSettings
{
    public bool Enabled { get; init; }
    public int DefaultTimeoutSeconds { get; init; } = 10;
    public int LongRunningTimeoutSeconds { get; init; } = 30;
}

/// <summary>
/// Range and cross-field invariants the binder cannot express. Failures are
/// collected (not short-circuited) so one boot error lists every problem,
/// each message naming the exact configuration key to fix.
/// </summary>
public sealed class PerformanceTuningOptionsValidator : IValidateOptions<PerformanceTuningOptions>
{
    public ValidateOptionsResult Validate(string? name, PerformanceTuningOptions options)
    {
        List<string> failures = [];

        var limits = options.RequestLimits;
        if (limits.MaxRequestBodySizeBytes <= 0)
        {
            failures.Add($"Performance:RequestLimits:MaxRequestBodySizeBytes must be > 0 (was {limits.MaxRequestBodySizeBytes}).");
        }
        if (limits.MinBodyDataRateBytesPerSecond <= 0)
        {
            failures.Add($"Performance:RequestLimits:MinBodyDataRateBytesPerSecond must be > 0 (was {limits.MinBodyDataRateBytesPerSecond}).");
        }
        if (limits.MinBodyDataRateGraceSeconds <= 0)
        {
            failures.Add($"Performance:RequestLimits:MinBodyDataRateGraceSeconds must be > 0 (was {limits.MinBodyDataRateGraceSeconds}).");
        }

        var cache = options.OutputCache;
        if (cache.DefaultExpirationSeconds <= 0)
        {
            failures.Add($"Performance:OutputCache:DefaultExpirationSeconds must be > 0 (was {cache.DefaultExpirationSeconds}).");
        }
        if (cache.ReadExpirationSeconds <= 0)
        {
            failures.Add($"Performance:OutputCache:ReadExpirationSeconds must be > 0 (was {cache.ReadExpirationSeconds}).");
        }
        if (cache.SizeLimitBytes <= 0)
        {
            failures.Add($"Performance:OutputCache:SizeLimitBytes must be > 0 (was {cache.SizeLimitBytes}).");
        }
        if (cache.MaximumBodySizeBytes <= 0)
        {
            failures.Add($"Performance:OutputCache:MaximumBodySizeBytes must be > 0 (was {cache.MaximumBodySizeBytes}).");
        }
        if (cache.MaximumBodySizeBytes > cache.SizeLimitBytes)
        {
            failures.Add(
                $"Performance:OutputCache:MaximumBodySizeBytes ({cache.MaximumBodySizeBytes}) cannot exceed "
                + $"SizeLimitBytes ({cache.SizeLimitBytes}) — a response that large would never be cached.");
        }

        var rateLimiting = options.RateLimiting;
        if (rateLimiting.PermitLimit <= 0)
        {
            failures.Add($"Performance:RateLimiting:PermitLimit must be > 0 (was {rateLimiting.PermitLimit}).");
        }
        if (rateLimiting.QueueLimit < 0)
        {
            failures.Add($"Performance:RateLimiting:QueueLimit must be >= 0 (was {rateLimiting.QueueLimit}).");
        }
        if (rateLimiting.WindowSeconds <= 0)
        {
            failures.Add($"Performance:RateLimiting:WindowSeconds must be > 0 (was {rateLimiting.WindowSeconds}).");
        }

        var timeout = options.RequestTimeout;
        if (timeout.Enabled)
        {
            if (timeout.DefaultTimeoutSeconds <= 0)
            {
                failures.Add($"Performance:RequestTimeout:DefaultTimeoutSeconds must be > 0 (was {timeout.DefaultTimeoutSeconds}).");
            }
            if (timeout.LongRunningTimeoutSeconds < timeout.DefaultTimeoutSeconds)
            {
                failures.Add(
                    $"Performance:RequestTimeout:LongRunningTimeoutSeconds ({timeout.LongRunningTimeoutSeconds}) must be >= "
                    + $"DefaultTimeoutSeconds ({timeout.DefaultTimeoutSeconds}) — the \"long-running\" policy would otherwise be the shorter one.");
            }
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }
}
