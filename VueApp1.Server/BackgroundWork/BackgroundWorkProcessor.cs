using System.Diagnostics;
using System.Globalization;

namespace VueApp1.Server.BackgroundWork;

/// <summary>
/// Drains <see cref="BackgroundWorkQueue"/> one item at a time, restoring the
/// captured envelope around each run:
/// <list type="bullet">
/// <item>a FRESH DI scope per item — request-scoped services must never leak
/// past the response, and one item's scope must never bleed into the next;</item>
/// <item>the enqueuer's culture, so formatting/localization behave as they
/// did at the call site;</item>
/// <item>a new root activity LINKED to the originating trace — linked, not
/// parented: the request span has long since ended, and parenting late work
/// under it would stretch request traces to nonsense durations while a link
/// still lets the trace UI navigate both ways;</item>
/// <item>per-item exception isolation — one failing item is logged and must
/// never take down the drain loop (or, via the host's
/// <c>BackgroundServiceExceptionBehavior.StopHost</c> default, the process).</item>
/// </list>
/// </summary>
public sealed partial class BackgroundWorkProcessor(
    BackgroundWorkQueue queue,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<BackgroundWorkProcessor> logger) : BackgroundService
{
    // Collected by the "VueApp1.*" wildcard in the telemetry setup.
    private static readonly ActivitySource _activitySource = new("VueApp1.BackgroundWork");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Cancellation here is host shutdown: anything still in the channel
        // is lost (in-process queue, best-effort by contract).
        await foreach (var item in queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            await RunItemAsync(item, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RunItemAsync(BackgroundWorkItem item, CancellationToken stoppingToken)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentCulture = item.Culture;
        CultureInfo.CurrentUICulture = item.UiCulture;
        try
        {
            using var activity = _activitySource.StartActivity(
                item.WorkName,
                ActivityKind.Internal,
                parentContext: default,
                links: item.TraceContext != default ? [new ActivityLink(item.TraceContext)] : null);
            activity?.SetTag("work.initiator", item.Initiator);

            var started = timeProvider.GetTimestamp();
            await using var scope = scopeFactory.CreateAsyncScope();
            try
            {
                await item.Work(scope.ServiceProvider, stoppingToken).ConfigureAwait(false);
                var elapsedMilliseconds = timeProvider.GetElapsedTime(started).TotalMilliseconds;
                LogWorkCompleted(logger, item.WorkName, item.Initiator, elapsedMilliseconds);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Host shutdown mid-item, not a work failure — stop draining.
                throw;
            }
            catch (Exception exception)
            {
                activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
                LogWorkFailed(logger, exception, item.WorkName, item.Initiator);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Background work '{WorkName}' (initiator: {Initiator}) completed in {ElapsedMilliseconds}ms")]
    private static partial void LogWorkCompleted(
        ILogger logger, string workName, string initiator, double elapsedMilliseconds);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Background work '{WorkName}' (initiator: {Initiator}) failed; continuing with the next item")]
    private static partial void LogWorkFailed(
        ILogger logger, Exception exception, string workName, string initiator);
}
