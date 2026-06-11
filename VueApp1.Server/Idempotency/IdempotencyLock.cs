using System.Collections.Concurrent;

namespace VueApp1.Server.Idempotency;

/// <summary>
/// Mutual exclusion between two in-flight requests carrying the same
/// Idempotency-Key. Abstracted because the default below is process-local:
/// correct on a single node, and that is exactly how far the guarantee goes.
/// Behind a load balancer, swap in a cross-process lock — SQL advisory lock
/// or Redis SET NX (sketches in docs/PATTERNS.md) — without touching
/// <see cref="IdempotencyService"/>.
/// </summary>
public interface IIdempotencyLock
{
    /// <summary>
    /// Non-blocking: returns null when another holder owns the key (the
    /// caller maps that to 409 InProgress instead of queueing requests).
    /// Dispose the handle to release.
    /// </summary>
    ValueTask<IAsyncDisposable?> TryAcquireAsync(string key, CancellationToken cancellationToken = default);
}

public sealed class InMemoryIdempotencyLock : IIdempotencyLock
{
    private readonly ConcurrentDictionary<string, byte> _held = new();

    public ValueTask<IAsyncDisposable?> TryAcquireAsync(
        string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IAsyncDisposable?>(
            _held.TryAdd(key, 0) ? new Releaser(this, key) : null);
    }

    private sealed class Releaser(InMemoryIdempotencyLock owner, string key) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            // Idempotent release: the reservation's DisposeAsync guards too,
            // but a lock that can be double-released must not exist at all.
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner._held.TryRemove(key, out _);
            }

            return ValueTask.CompletedTask;
        }
    }
}
