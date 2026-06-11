using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

namespace VueApp1.Server.Idempotency;

public enum IdempotencyOutcome
{
    /// <summary>First sight of this key: execute, then <see cref="IdempotencyReservation.CommitAsync"/> the response.</summary>
    Reserved,

    /// <summary>The operation already completed: replay <see cref="IdempotencyReservation.Record"/> verbatim.</summary>
    CachedResponse,

    /// <summary>The key was reused with a DIFFERENT payload — a client bug; reject with 422, never run or replay.</summary>
    PayloadConflict,

    /// <summary>The original request is still executing — reject with 409; the client retries after it finishes.</summary>
    InProgress,
}

/// <summary>
/// The single atomic unit the seam stores. Payload hash and response live in
/// ONE record on purpose: stored separately there would be a window where a
/// retry sees one but not the other and misclassifies (replays a response it
/// can't hash-check, or conflicts on a hash with no response to replay).
/// </summary>
public sealed record IdempotencyRecord(string PayloadHash, int StatusCode, string ContentType, string Body);

/// <summary>
/// The classified result of <see cref="IIdempotencyService.BeginAsync"/>.
/// A <see cref="IdempotencyOutcome.Reserved"/> reservation holds the
/// per-key lock until disposed; disposing WITHOUT committing stores nothing
/// — that is the don't-poison guarantee: a request that fails validation or
/// throws must leave the key fresh for a corrected retry.
/// </summary>
public sealed class IdempotencyReservation : IAsyncDisposable
{
    private readonly IAsyncDisposable? _lockHandle;
    private readonly string? _payloadHash;
    private readonly Func<IdempotencyRecord, Task>? _commit;
    private bool _disposed;

    internal IdempotencyReservation(
        IdempotencyOutcome outcome,
        IdempotencyRecord? record = null,
        IAsyncDisposable? lockHandle = null,
        string? payloadHash = null,
        Func<IdempotencyRecord, Task>? commit = null)
    {
        Outcome = outcome;
        Record = record;
        _lockHandle = lockHandle;
        _payloadHash = payloadHash;
        _commit = commit;
    }

    public IdempotencyOutcome Outcome { get; }

    /// <summary>The stored response to replay; non-null only for <see cref="IdempotencyOutcome.CachedResponse"/>.</summary>
    public IdempotencyRecord? Record { get; }

    /// <summary>
    /// Stores the response against the key. Deliberately takes NO
    /// CancellationToken: by commit time the mutation has already happened,
    /// and a client disconnect (RequestAborted) that cancelled this write
    /// would let the client's automatic retry find no record and execute the
    /// mutation a second time — the exact duplicate this seam prevents.
    /// </summary>
    public Task CommitAsync(int statusCode, string contentType, string body)
    {
        if (Outcome != IdempotencyOutcome.Reserved || _commit is null)
        {
            throw new InvalidOperationException(
                $"Only a {nameof(IdempotencyOutcome.Reserved)} reservation can commit a response (outcome: {Outcome}).");
        }

        return _commit(new IdempotencyRecord(_payloadHash!, statusCode, contentType, body));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_lockHandle is not null)
        {
            // Released AFTER any commit: a competitor acquiring the lock must
            // observe the committed record in its double-check read.
            await _lockHandle.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Execution seam for the <c>Idempotency-Key</c> convention (retrying
/// clients: mobile/PWA networks, agent tool callers). See
/// <see cref="IdempotencyKeyFilter"/> for the HTTP wiring and
/// docs/PATTERNS.md for the correctness notes and cross-process upgrades.
/// </summary>
public interface IIdempotencyService
{
    /// <summary>
    /// Classifies <paramref name="key"/>: fast-path read first (most retries
    /// arrive after the original finished), then a non-blocking per-key lock
    /// with a second read under it — the original may commit between the
    /// fast-path read and the lock acquisition.
    /// </summary>
    ValueTask<IdempotencyReservation> BeginAsync(
        string key, string payloadHash, CancellationToken cancellationToken = default);
}

public sealed class IdempotencyService(
    IDistributedCache cache,
    IIdempotencyLock keyLock,
    TimeSpan retention) : IIdempotencyService
{
    public async ValueTask<IdempotencyReservation> BeginAsync(
        string key, string payloadHash, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadHash);

        var existing = await ReadAsync(key, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return Classify(existing, payloadHash);
        }

        var lockHandle = await keyLock.TryAcquireAsync(key, cancellationToken).ConfigureAwait(false);
        if (lockHandle is null)
        {
            return new IdempotencyReservation(IdempotencyOutcome.InProgress);
        }

        try
        {
            existing = await ReadAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await lockHandle.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        if (existing is not null)
        {
            // Double-check hit: the original committed while we acquired.
            await lockHandle.DisposeAsync().ConfigureAwait(false);
            return Classify(existing, payloadHash);
        }

        return new IdempotencyReservation(
            IdempotencyOutcome.Reserved,
            lockHandle: lockHandle,
            payloadHash: payloadHash,
            commit: record => CommitAsync(key, record));
    }

    private Task CommitAsync(string key, IdempotencyRecord record) =>
        // CancellationToken.None: see IdempotencyReservation.CommitAsync.
        cache.SetAsync(
            key,
            JsonSerializer.SerializeToUtf8Bytes(record),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = retention },
            CancellationToken.None);

    private async Task<IdempotencyRecord?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        var bytes = await cache.GetAsync(key, cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : JsonSerializer.Deserialize<IdempotencyRecord>(bytes);
    }

    private static IdempotencyReservation Classify(IdempotencyRecord record, string payloadHash) =>
        string.Equals(record.PayloadHash, payloadHash, StringComparison.Ordinal)
            ? new IdempotencyReservation(IdempotencyOutcome.CachedResponse, record)
            : new IdempotencyReservation(IdempotencyOutcome.PayloadConflict);
}

public static class IdempotencyServiceCollectionExtensions
{
    public static IServiceCollection AddIdempotency(
        this IServiceCollection services, TimeSpan? retention = null)
    {
        // IDistributedCache, not the already-registered HybridCache: this
        // seam needs a plain read-without-create plus one atomic write, and
        // HybridCache's GetOrCreateAsync shape cannot express "is there a
        // record — and if not, DON'T make one". The in-memory default keeps
        // the template database-free (single-node guarantee); registering a
        // real IDistributedCache (e.g. AddStackExchangeRedisCache) upgrades
        // storage to cross-process without code changes — pair it with a
        // cross-process IIdempotencyLock (sketches in docs/PATTERNS.md).
        // This registration does NOT change HybridCache's topology:
        // HybridCache adopts a registered IDistributedCache as its L2, but
        // deliberately ignores MemoryDistributedCache (verified: its backend
        // stays null with this default). A REAL distributed cache registered
        // later upgrades both consumers at once — by design.
        services.AddDistributedMemoryCache();
        services.AddSingleton<IIdempotencyLock, InMemoryIdempotencyLock>();
        // 24h default retention: long enough for client retry windows,
        // short enough that the response cache stays bounded.
        services.AddSingleton<IIdempotencyService>(provider => new IdempotencyService(
            provider.GetRequiredService<IDistributedCache>(),
            provider.GetRequiredService<IIdempotencyLock>(),
            retention ?? TimeSpan.FromHours(24)));
        services.AddTransient<IdempotencyKeyFilter>();
        return services;
    }
}
