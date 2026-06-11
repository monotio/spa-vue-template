using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using VueApp1.Server.Idempotency;
using Xunit;

namespace VueApp1.Server.UnitTests.Idempotency;

public class IdempotencyServiceTests
{
    private const string Key = "idempotency:POST:/api/sample:key-1";
    private const string Hash = "HASH-A";

    private readonly IDistributedCache _cache =
        new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

    private IdempotencyService CreateService(IIdempotencyLock? keyLock = null) =>
        new(_cache, keyLock ?? new InMemoryIdempotencyLock(), TimeSpan.FromHours(1));

    [Fact]
    public async Task FirstUseOfAKey_IsReserved()
    {
        var service = CreateService();

        await using var reservation = await service.BeginAsync(
            Key, Hash, TestContext.Current.CancellationToken);

        Assert.Equal(IdempotencyOutcome.Reserved, reservation.Outcome);
        Assert.Null(reservation.Record);
    }

    [Fact]
    public async Task CommittedResponse_IsReplayedForTheSamePayload()
    {
        var service = CreateService();
        await using (var first = await service.BeginAsync(Key, Hash, TestContext.Current.CancellationToken))
        {
            await first.CommitAsync(201, "application/json; charset=utf-8", """{"id":"abc"}""");
        }

        await using var retry = await service.BeginAsync(Key, Hash, TestContext.Current.CancellationToken);

        Assert.Equal(IdempotencyOutcome.CachedResponse, retry.Outcome);
        Assert.NotNull(retry.Record);
        Assert.Equal(201, retry.Record.StatusCode);
        Assert.Equal("""{"id":"abc"}""", retry.Record.Body);
        Assert.Equal("application/json; charset=utf-8", retry.Record.ContentType);
    }

    [Fact]
    public async Task CommittedKey_WithADifferentPayload_IsPayloadConflict()
    {
        var service = CreateService();
        await using (var first = await service.BeginAsync(Key, Hash, TestContext.Current.CancellationToken))
        {
            await first.CommitAsync(201, "application/json; charset=utf-8", "{}");
        }

        await using var conflicting = await service.BeginAsync(
            Key, "HASH-B", TestContext.Current.CancellationToken);

        Assert.Equal(IdempotencyOutcome.PayloadConflict, conflicting.Outcome);
        // The stored response must NOT be exposed on a conflict — it belongs
        // to a different payload.
        Assert.Null(conflicting.Record);
    }

    [Fact]
    public async Task KeyHeldByAnInFlightRequest_IsInProgress()
    {
        var service = CreateService();
        await using var inFlight = await service.BeginAsync(Key, Hash, TestContext.Current.CancellationToken);

        await using var concurrent = await service.BeginAsync(Key, Hash, TestContext.Current.CancellationToken);

        Assert.Equal(IdempotencyOutcome.Reserved, inFlight.Outcome);
        Assert.Equal(IdempotencyOutcome.InProgress, concurrent.Outcome);
    }

    [Fact]
    public async Task DisposeWithoutCommit_StoresNothing_AndReleasesTheLock()
    {
        var service = CreateService();
        await using (var failed = await service.BeginAsync(Key, Hash, TestContext.Current.CancellationToken))
        {
            Assert.Equal(IdempotencyOutcome.Reserved, failed.Outcome);
            // No commit: the request failed validation / threw.
        }

        await using var retry = await service.BeginAsync(Key, Hash, TestContext.Current.CancellationToken);

        // Reserved again — not CachedResponse (nothing stored: the key is
        // not poisoned by the failure) and not InProgress (lock released).
        Assert.Equal(IdempotencyOutcome.Reserved, retry.Outcome);
    }

    [Fact]
    public async Task Commit_WritesWithAnUncancellableToken()
    {
        var recordingCache = new TokenRecordingCache(_cache);
        var service = new IdempotencyService(
            recordingCache, new InMemoryIdempotencyLock(), TimeSpan.FromHours(1));
        using var requestAborted = new CancellationTokenSource();

        await using var reservation = await service.BeginAsync(Key, Hash, requestAborted.Token);
        // The client disconnects after the mutation succeeded — the commit
        // must still land, or the client's retry would duplicate the mutation.
        await requestAborted.CancelAsync();
        await reservation.CommitAsync(201, "application/json; charset=utf-8", "{}");

        Assert.False(recordingCache.LastSetToken!.Value.CanBeCanceled);

        await using var retry = await service.BeginAsync(Key, Hash, TestContext.Current.CancellationToken);
        Assert.Equal(IdempotencyOutcome.CachedResponse, retry.Outcome);
    }

    [Fact]
    public async Task Commit_OnANonReservedReservation_Throws()
    {
        var service = CreateService();
        await using (var first = await service.BeginAsync(Key, Hash, TestContext.Current.CancellationToken))
        {
            await first.CommitAsync(201, "application/json; charset=utf-8", "{}");
        }

        await using var replay = await service.BeginAsync(Key, Hash, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => replay.CommitAsync(200, "application/json; charset=utf-8", "{}"));
    }

    [Fact]
    public async Task DoubleCheckUnderTheLock_SeesARecordCommittedDuringAcquisition()
    {
        // Simulates the cross-process race the double-check exists for: the
        // original request commits between this request's fast-path read and
        // its lock acquisition. The lock stub performs that commit inside
        // TryAcquireAsync, deterministically inside the race window.
        var committingLock = new CommitDuringAcquireLock(_cache);
        var service = CreateService(committingLock);

        await using var reservation = await service.BeginAsync(
            Key, Hash, TestContext.Current.CancellationToken);

        Assert.Equal(IdempotencyOutcome.CachedResponse, reservation.Outcome);
        Assert.Equal("""{"id":"raced"}""", reservation.Record!.Body);
        // The uselessly acquired lock must be released on the early return.
        Assert.True(committingLock.HandleDisposed);
    }

    private sealed class CommitDuringAcquireLock(IDistributedCache cache) : IIdempotencyLock
    {
        public bool HandleDisposed { get; private set; }

        public async ValueTask<IAsyncDisposable?> TryAcquireAsync(
            string key, CancellationToken cancellationToken = default)
        {
            var competitor = new IdempotencyService(
                cache, new InMemoryIdempotencyLock(), TimeSpan.FromHours(1));
            await using (var winning = await competitor.BeginAsync(key, Hash, cancellationToken))
            {
                await winning.CommitAsync(201, "application/json; charset=utf-8", """{"id":"raced"}""");
            }

            return new Handle(this);
        }

        private sealed class Handle(CommitDuringAcquireLock owner) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                owner.HandleDisposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class TokenRecordingCache(IDistributedCache inner) : IDistributedCache
    {
        public CancellationToken? LastSetToken { get; private set; }

        public Task SetAsync(
            string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            LastSetToken = token;
            return inner.SetAsync(key, value, options, token);
        }

        public byte[]? Get(string key) => inner.Get(key);

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) =>
            inner.GetAsync(key, token);

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) =>
            inner.Set(key, value, options);

        public void Refresh(string key) => inner.Refresh(key);

        public Task RefreshAsync(string key, CancellationToken token = default) =>
            inner.RefreshAsync(key, token);

        public void Remove(string key) => inner.Remove(key);

        public Task RemoveAsync(string key, CancellationToken token = default) =>
            inner.RemoveAsync(key, token);
    }
}
