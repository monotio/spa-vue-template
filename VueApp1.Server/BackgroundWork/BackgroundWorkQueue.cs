using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;

namespace VueApp1.Server.BackgroundWork;

/// <summary>
/// A unit of background work plus the ambient context captured at the
/// enqueue site. Ambient context (current <see cref="Activity"/>, culture,
/// the request's DI scope) does NOT flow across an enqueue boundary by
/// itself — by the time the item runs, the request that produced it is gone.
/// Capturing here and restoring in <see cref="BackgroundWorkProcessor"/> is
/// what keeps background work traceable and correctly localized.
/// </summary>
public sealed record BackgroundWorkItem
{
    /// <summary>Short operation name, used as the trace span name and in logs.</summary>
    public required string WorkName { get; init; }

    /// <summary>
    /// Who or what caused this work (a user id once auth exists, otherwise
    /// the feature/system name making the call). Always explicit — see the
    /// fail-closed rule on <see cref="IBackgroundWorkQueue.EnqueueAsync"/>.
    /// </summary>
    public required string Initiator { get; init; }

    /// <summary>
    /// The work itself. Receives a fresh scoped <see cref="IServiceProvider"/>
    /// (never the request's — that scope is disposed when the response ends)
    /// and the processor's stopping token.
    /// </summary>
    public required Func<IServiceProvider, CancellationToken, Task> Work { get; init; }

    /// <summary>Trace context of the enqueuing operation, for an <see cref="ActivityLink"/>.</summary>
    public ActivityContext TraceContext { get; init; }

    public required CultureInfo Culture { get; init; }
    public required CultureInfo UiCulture { get; init; }
}

// CA1711 reserves the Queue suffix for System.Collections-style queues; this
// IS a queue in the only sense that matters here (bounded producer/consumer
// over Channel<T>), and "background work queue" is the established name for
// the pattern — renaming to dodge the analyzer would obscure intent.
#pragma warning disable CA1711

/// <summary>
/// Run-after-the-response work without blocking the request and without
/// fire-and-forget <c>Task.Run</c> (which loses errors, scopes and trace
/// context). In-process and best-effort by design: items still queued at
/// shutdown are LOST — when that stops being acceptable, swap in a durable
/// scheduler (docs/BACKGROUND.md); only the enqueue sites stay the same shape.
/// </summary>
public interface IBackgroundWorkQueue
{
    /// <summary>
    /// Captures ambient context (trace context, culture) and enqueues.
    /// Fails closed: <paramref name="initiator"/> is required and never
    /// guessed — work that cannot say who caused it should not run, because
    /// a default "system" stamp silently misattributes user-initiated work
    /// in logs, traces and audit trails.
    /// Applies backpressure: when the bounded queue is full, this waits
    /// instead of dropping work or growing without bound.
    /// </summary>
    ValueTask EnqueueAsync(
        string workName,
        string initiator,
        Func<IServiceProvider, CancellationToken, Task> work,
        CancellationToken cancellationToken = default);
}

public sealed class BackgroundWorkQueue(int capacity) : IBackgroundWorkQueue
{
    // Bounded on purpose: an unbounded channel turns a stuck consumer into
    // unobserved memory growth. FullMode.Wait surfaces the problem as
    // producer backpressure instead (callers may pass a CancellationToken
    // to bail out).
    private readonly Channel<BackgroundWorkItem> _channel =
        Channel.CreateBounded<BackgroundWorkItem>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

    internal ChannelReader<BackgroundWorkItem> Reader => _channel.Reader;

    public ValueTask EnqueueAsync(
        string workName,
        string initiator,
        Func<IServiceProvider, CancellationToken, Task> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workName);
        ArgumentException.ThrowIfNullOrWhiteSpace(initiator);
        ArgumentNullException.ThrowIfNull(work);

        return _channel.Writer.WriteAsync(new BackgroundWorkItem
        {
            WorkName = workName,
            Initiator = initiator,
            Work = work,
            TraceContext = Activity.Current?.Context ?? default,
            Culture = CultureInfo.CurrentCulture,
            UiCulture = CultureInfo.CurrentUICulture,
        }, cancellationToken);
    }
}

#pragma warning restore CA1711

public static class BackgroundWorkServiceCollectionExtensions
{
    /// <summary>
    /// Registers the queue and its draining hosted service. Dormant until
    /// called — see the commented seam in Program.cs.
    /// </summary>
    public static IServiceCollection AddBackgroundWorkQueue(
        this IServiceCollection services, int capacity = 100)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        services.AddSingleton(new BackgroundWorkQueue(capacity));
        services.AddSingleton<IBackgroundWorkQueue>(static sp => sp.GetRequiredService<BackgroundWorkQueue>());
        services.AddHostedService<BackgroundWorkProcessor>();
        return services;
    }
}
