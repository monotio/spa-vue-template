using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VueApp1.Server.BackgroundWork;
using VueApp1.Server.UnitTests.Infrastructure;
using Xunit;

namespace VueApp1.Server.UnitTests.BackgroundWork;

/// <summary>
/// Deterministic by construction (docs/TESTING.md): every wait is a
/// TaskCompletionSource the work item itself completes — no real delays,
/// no polling. A regression hangs the test (and the runner's
/// longRunningTestSeconds flags it) instead of flaking.
/// </summary>
public sealed class BackgroundWorkProcessorTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly BackgroundWorkQueue _queue;
    private readonly BackgroundWorkProcessor _processor;

    public BackgroundWorkProcessorTests()
    {
        _provider = new ServiceCollection()
            .AddScoped<ScopedProbe>()
            .BuildServiceProvider();
        _queue = new BackgroundWorkQueue(capacity: 16);
        _processor = new BackgroundWorkProcessor(
            _queue,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<BackgroundWorkProcessor>.Instance);
    }

    public void Dispose()
    {
        _processor.Dispose();
        _provider.Dispose();
    }

    [Fact]
    public async Task RunsEachItem_InAFreshDisposedDiScope()
    {
        var probes = new List<ScopedProbe>();
        var secondDone = CreateCompletionSource();
        await _queue.EnqueueAsync("first", "tests", (sp, _) =>
        {
            probes.Add(sp.GetRequiredService<ScopedProbe>());
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);
        await _queue.EnqueueAsync("second", "tests", (sp, _) =>
        {
            probes.Add(sp.GetRequiredService<ScopedProbe>());
            secondDone.SetResult();
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        await _processor.StartAsync(TestContext.Current.CancellationToken);
        await secondDone.Task.WaitAsync(TestContext.Current.CancellationToken);
        await _processor.StopAsync(TestContext.Current.CancellationToken);

        // Different instances of a scoped service = a fresh scope per item;
        // both disposed = no scope outlives its item.
        Assert.Equal(2, probes.Count);
        Assert.NotEqual(probes[0].Id, probes[1].Id);
        Assert.All(probes, probe => Assert.True(probe.Disposed));
    }

    [Fact]
    public async Task FailingItem_IsIsolated_AndDrainingContinues()
    {
        var secondDone = CreateCompletionSource();
        await _queue.EnqueueAsync("failing", "tests",
            (_, _) => throw new InvalidOperationException("deliberate test failure"),
            TestContext.Current.CancellationToken);
        await _queue.EnqueueAsync("after-failure", "tests", (_, _) =>
        {
            secondDone.SetResult();
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        await _processor.StartAsync(TestContext.Current.CancellationToken);

        // Reaching here at all proves the drain loop survived the failure.
        await secondDone.Task.WaitAsync(TestContext.Current.CancellationToken);
        await _processor.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RestoresTheEnqueueSiteCulture_InsideTheItem()
    {
        CultureInfo? observedCulture = null;
        CultureInfo? observedUiCulture = null;
        var done = CreateCompletionSource();

        using (new CultureSwitcher("sv-SE"))
        {
            await _queue.EnqueueAsync("culture-probe", "tests", (_, _) =>
            {
                observedCulture = CultureInfo.CurrentCulture;
                observedUiCulture = CultureInfo.CurrentUICulture;
                done.SetResult();
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);
        }

        // Started AFTER the switcher reverted: any sv-SE seen inside the item
        // can only have travelled in the envelope.
        await _processor.StartAsync(TestContext.Current.CancellationToken);
        await done.Task.WaitAsync(TestContext.Current.CancellationToken);
        await _processor.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal("sv-SE", observedCulture?.Name);
        Assert.Equal("sv-SE", observedUiCulture?.Name);
    }

    [Fact]
    public async Task WorkActivity_IsANewRoot_LinkedToTheEnqueueTrace()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "VueApp1.BackgroundWork",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        Activity? workActivity = null;
        var done = CreateCompletionSource();

        // Start the processor BEFORE the enqueue-site activity exists, so the
        // drain loop's own execution context carries no ambient activity and
        // anything observed inside the item came from the envelope.
        await _processor.StartAsync(TestContext.Current.CancellationToken);

        using var enqueueActivity = new Activity("enqueue-site").Start();
        await _queue.EnqueueAsync("traced", "tests", (_, _) =>
        {
            workActivity = Activity.Current;
            done.SetResult();
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);
        enqueueActivity.Stop();

        await done.Task.WaitAsync(TestContext.Current.CancellationToken);
        await _processor.StopAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(workActivity);
        var link = Assert.Single(workActivity.Links);
        Assert.Equal(enqueueActivity.TraceId, link.Context.TraceId);
        // Linked, not parented: late work must not stretch the request trace.
        Assert.NotEqual(enqueueActivity.TraceId, workActivity.TraceId);
        Assert.Equal("tests", workActivity.GetTagItem("work.initiator"));
    }

    [Fact]
    public async Task WorkActivity_IsRoot_EvenWithAnAmbientActivityAtProcessorStart()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "VueApp1.BackgroundWork",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        Activity? workActivity = null;
        var done = CreateCompletionSource();

        // An ambient activity in the execution context captured at host start
        // (instrumented startup, a harness running the factory under a span)
        // must NOT become the silent parent of every drained item — the root
        // promise has to hold regardless of what surrounds StartAsync.
        using var ambient = new Activity("ambient-at-start").Start();
        await _processor.StartAsync(TestContext.Current.CancellationToken);

        using var enqueueActivity = new Activity("enqueue-site").Start();
        await _queue.EnqueueAsync("traced", "tests", (_, _) =>
        {
            workActivity = Activity.Current;
            done.SetResult();
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);
        enqueueActivity.Stop();

        await done.Task.WaitAsync(TestContext.Current.CancellationToken);
        await _processor.StopAsync(TestContext.Current.CancellationToken);
        ambient.Stop();

        Assert.NotNull(workActivity);
        Assert.Null(workActivity.ParentId);
        Assert.NotEqual(ambient.TraceId, workActivity.TraceId);
        // The deliberate envelope link still points at the enqueue trace.
        var link = Assert.Single(workActivity.Links);
        Assert.Equal(enqueueActivity.TraceId, link.Context.TraceId);
    }

    [Fact]
    public async Task BoundedQueue_AppliesBackpressure_WhenFull()
    {
        var queue = new BackgroundWorkQueue(capacity: 1);
        await queue.EnqueueAsync("fills-the-queue", "tests",
            (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

        // No reader is running and capacity is 1: the second write cannot
        // complete — FullMode.Wait turns overflow into producer backpressure
        // instead of dropped work or unbounded growth.
        var pending = queue.EnqueueAsync("waits-for-capacity", "tests",
            (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);
        Assert.False(pending.IsCompleted);

        using var processor = new BackgroundWorkProcessor(
            queue,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<BackgroundWorkProcessor>.Instance);
        await processor.StartAsync(TestContext.Current.CancellationToken);
        await pending.AsTask().WaitAsync(TestContext.Current.CancellationToken);
        await processor.StopAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EnqueueAsync_FailsClosed_WithoutAnInitiatorStamp(string initiator)
    {
        // Never guess who caused background work: an implicit "system" stamp
        // would silently misattribute user-initiated work.
        await Assert.ThrowsAsync<ArgumentException>(() => _queue.EnqueueAsync(
            "work", initiator, (_, _) => Task.CompletedTask,
            TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task EnqueueAsync_Rejects_MissingWorkNameOrDelegate()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _queue.EnqueueAsync(
            " ", "tests", (_, _) => Task.CompletedTask,
            TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<ArgumentNullException>(() => _queue.EnqueueAsync(
            "work", "tests", null!, TestContext.Current.CancellationToken).AsTask());
    }

    private static TaskCompletionSource CreateCompletionSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ScopedProbe : IDisposable
    {
        public Guid Id { get; } = Guid.NewGuid();
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
