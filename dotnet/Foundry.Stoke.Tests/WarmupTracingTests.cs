using System.Diagnostics;
using Foundry.Stoke.Observability;
using Foundry.Stoke.Scheduling;
using Foundry.Stoke.Session;
using Foundry.Stoke.Store;
using Foundry.Stoke.Warmup;

namespace Foundry.Stoke.Tests;

[Collection("WarmupTracing")]
public sealed class WarmupTracingTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProbeSpans_PreserveResultsAndCallbacks(bool ok)
    {
        using var capture = new Capture();
        var calls = new List<(string, string)>();
        var events = new List<TelemetryEvent>();
        var strategy = new KeepaliveStrategy(new CallableProbe((agent, session) =>
        {
            calls.Add((agent, session));
            return Task.FromResult(new ProbeResult(ok, 0.1));
        }), new VirtualClock(), 300, "private-agent", new[] { "private-handle", "second-handle" },
            new Telemetry(events.Add));

        var report = await strategy.ReconcileAsync();

        Assert.Same(capture.Parent, Activity.Current);
        Assert.Equal(2, report.Probed);
        Assert.Equal(ok ? 0 : 2, report.Failures);
        Assert.Equal(new[] { ("private-agent", "private-handle"), ("private-agent", "second-handle") }, calls);
        Assert.Equal(2, events.Count);
        Assert.All(events, item => Assert.Equal(ok, item.Attributes["stoke.probe.ok"]));
        Assert.Equal(2, capture.Stopped.Count);
        Assert.All(capture.Stopped, span =>
        {
            Assert.Equal("stoke.warmup.probe", span.OperationName);
            Assert.Equal(capture.Parent.SpanId, span.ParentSpanId);
            Assert.Equal(ok ? ActivityStatusCode.Ok : ActivityStatusCode.Error, span.Status);
            Assert.True(span.IsStopped);
            AssertSafe(span, "keepalive");
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task RefillSpan_ContainsSessionAndStoreOperations(int targetSize)
    {
        using var capture = new Capture();
        var events = new List<TelemetryEvent>();
        var pool = new PreProvisionPoolStrategy(new SessionController(new SessionOperations()),
            new InMemoryStore(), "private-agent", targetSize, new VirtualClock(autoAdvance: true),
            telemetry: new Telemetry(events.Add));

        foreach (var expectedCreated in new[] { targetSize, 0 })
        {
            capture.Stopped.Clear();
            var report = await pool.ReconcileAsync();
            Assert.Equal(expectedCreated, report.Created);
            Assert.Equal(targetSize, report.Ready);
            Assert.Equal(0, report.Failures);
            Assert.Same(capture.Parent, Activity.Current);
            var span = Assert.Single(capture.Stopped, item => item.OperationName == "stoke.warmup.refill");
            Assert.Equal(capture.Parent.SpanId, span.ParentSpanId);
            Assert.Equal(ActivityStatusCode.Ok, span.Status);
            AssertSafe(span, "pre-provision-pool");
            var children = capture.Stopped.Where(item => item != span).ToList();
            Assert.Equal(2 + targetSize, children.Count);
            Assert.All(children, child =>
            {
                Assert.Equal(span.SpanId, child.ParentSpanId);
                Assert.Equal(span.TraceId, child.TraceId);
                Assert.True(child.IsStopped);
                Assert.True(span.StartTimeUtc <= child.StartTimeUtc);
                Assert.True(child.StartTimeUtc + child.Duration <= span.StartTimeUtc + span.Duration);
            });
        }
        Assert.Equal(2, events.Count);
        Assert.All(events, item => Assert.Equal("stoke.warmup.refill", item.Name));
    }

    [Theory]
    [InlineData("probe", "success")]
    [InlineData("probe", "error")]
    [InlineData("probe", "cancel")]
    [InlineData("create", "success")]
    [InlineData("create", "error")]
    [InlineData("create", "cancel")]
    [InlineData("query", "success")]
    [InlineData("query", "error")]
    [InlineData("query", "cancel")]
    [InlineData("read", "success")]
    [InlineData("read", "error")]
    [InlineData("read", "cancel")]
    [InlineData("write", "success")]
    [InlineData("write", "error")]
    [InlineData("write", "cancel")]
    [InlineData("backoff", "success")]
    [InlineData("backoff", "error")]
    [InlineData("backoff", "cancel")]
    public async Task AsyncLifetimes_PreserveContextFailuresAndCancellation(string stage, string outcome)
    {
        using var capture = new Capture();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource<Activity?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("/private/path AccountKey=private-key token=private-token");
        var events = new List<TelemetryEvent>();

        async Task Wait()
        {
            var current = Activity.Current;
            entered.TrySetResult(current);
            await release.Task;
            Assert.Same(current, Activity.Current);
        }

        var operations = new SessionOperations();
        var store = new Store();
        var clock = new Clock();
        var controller = new SessionController(operations);
        var pool = new PreProvisionPoolStrategy(controller, store, "AccountKey=private-key", 1,
            clock, maxRetries: stage == "backoff" ? 1 : 0, telemetry: new Telemetry(events.Add));
        if (stage == "query")
        {
            await pool.ReconcileAsync();
            operations.BeforeGet = Wait;
        }
        if (stage == "create")
        {
            operations.BeforeCreate = Wait;
        }
        if (stage == "read")
        {
            store.BeforeRead = Wait;
        }
        if (stage == "write")
        {
            store.BeforeWrite = Wait;
        }
        if (stage == "backoff")
        {
            var attempts = 0;
            operations.BeforeCreate = () => ++attempts == 1 ? Task.FromException(failure) : Task.CompletedTask;
            clock.BeforeDelay = Wait;
        }
        IWarmupStrategy strategy = stage == "probe"
            ? new KeepaliveStrategy(new CallableProbe(async (agent, session) =>
            {
                await Wait();
                await controller.GetSessionAsync(agent, session);
                return new ProbeResult(true, 0);
            }), clock, 300, "AccountKey=private-key", new[] { "private-handle" }, new Telemetry(events.Add))
            : pool;
        capture.Started.Clear();
        capture.Stopped.Clear();
        events.Clear();
        Activity? restored = null;
        async Task<WarmupReport> Invoke()
        {
            try
            {
                return await strategy.ReconcileAsync();
            }
            finally
            {
                restored = Activity.Current;
            }
        }

        var task = Invoke();
        var handled = stage is "probe" or "create" or "query";
        try
        {
            var observed = await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.NotNull(observed);
            Assert.NotSame(capture.Parent, observed);
            Assert.False(observed.IsStopped);
            Assert.False(task.IsCompleted);
            var active = Assert.Single(capture.Started, span => span.OperationName.StartsWith("stoke.warmup.", StringComparison.Ordinal));
            Assert.False(active.IsStopped);
            Assert.DoesNotContain(capture.Stopped, span => span == active);
        }
        finally
        {
            if (outcome == "cancel")
            {
                cancellation.Cancel();
                release.TrySetCanceled(cancellation.Token);
            }
            else if (outcome == "error")
            {
                release.TrySetException(failure);
            }
            else
            {
                release.TrySetResult();
            }

            if (outcome == "cancel" && !handled)
            {
                var caught = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
                Assert.Equal(cancellation.Token, caught.CancellationToken);
                Assert.True(task.IsCanceled);
            }
            else if (outcome == "error" && !handled)
            {
                Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => task));
            }
            else
            {
                var report = await task;
                var expectedFailures = stage == "backoff" || (outcome != "success" && stage != "query") ? 1 : 0;
                Assert.Equal(expectedFailures, report.Failures);
                Assert.Equal(stage == "query" && outcome != "success" ? 1 : 0, report.Evicted);
                Assert.True(task.IsCompletedSuccessfully);
                Assert.Equal(stage == "probe" ? 1 : report.Failures + 1, events.Count);
            }
        }

        Assert.Same(capture.Parent, Activity.Current);
        Assert.Same(capture.Parent, restored);
        var span = Assert.Single(capture.Stopped, item => item.OperationName.StartsWith("stoke.warmup.", StringComparison.Ordinal));
        Assert.Equal(stage == "probe" ? "stoke.warmup.probe" : "stoke.warmup.refill", span.OperationName);
        Assert.Equal(capture.Parent.SpanId, span.ParentSpanId);
        Assert.Equal(outcome == "success" && stage != "backoff" ? ActivityStatusCode.Ok : ActivityStatusCode.Error, span.Status);
        Assert.True(span.IsStopped);
        AssertSafe(span, stage == "probe" ? "keepalive" : "pre-provision-pool");
        Assert.All(capture.Stopped.Where(item => item != span), child =>
        {
            Assert.Equal(span.SpanId, child.ParentSpanId);
            Assert.True(child.IsStopped);
            Assert.True(child.StartTimeUtc + child.Duration <= span.StartTimeUtc + span.Duration);
            Assert.Empty(child.Events);
            Assert.Null(child.StatusDescription);
            Assert.DoesNotContain(child.TagObjects, tag => (tag.Value?.ToString() ?? "").Contains("private-key", StringComparison.Ordinal));
            Assert.DoesNotContain(child.TagObjects, tag => (tag.Value?.ToString() ?? "").Contains("private-handle", StringComparison.Ordinal));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefillFailures_PreserveRetryCeilingJitterAndReports(bool recover)
    {
        using var capture = new Capture();
        var failure = new InvalidOperationException("AccountKey=private-key");
        var attempts = 0;
        var operations = new SessionOperations
        {
            BeforeCreate = () => ++attempts == 1 || !recover ? Task.FromException(failure) : Task.CompletedTask,
        };
        var clock = new VirtualClock(autoAdvance: true);
        var events = new List<TelemetryEvent>();
        var pool = new PreProvisionPoolStrategy(new SessionController(operations), new InMemoryStore(),
            "private-agent", 1, clock, maxRetries: 1, rng: new Random(1234), telemetry: new Telemetry(events.Add));
        var report = await pool.ReconcileAsync();
        Assert.Equal(2, attempts);
        Assert.Equal(recover ? 1 : 0, report.Ready);
        Assert.Equal(recover ? 1 : 2, report.Failures);
        Assert.Equal(new Random(1234).NextDouble(), clock.Now());
        Assert.Equal(report.Failures + 1, events.Count);
        var span = Assert.Single(capture.Stopped, item => item.OperationName == "stoke.warmup.refill");
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        AssertSafe(span, "pre-provision-pool");
    }

    [Fact]
    public async Task EmptyKeepalive_EmitsNoProbeSpan()
    {
        using var capture = new Capture();
        var strategy = new KeepaliveStrategy(new CallableProbe((_, _) => throw new InvalidOperationException()),
            new VirtualClock(), 300, "private-agent", Array.Empty<string>());
        var report = await strategy.ReconcileAsync();
        Assert.Equal(0, report.Probed);
        Assert.Equal(0, report.Failures);
        Assert.Empty(capture.Started);
        Assert.Empty(capture.Stopped);
    }

    [Fact]
    public async Task NoListener_PreservesParentAndWarmupContracts()
    {
        using var parent = new Activity("parent").Start();
        var probe = new CallableProbe((_, _) =>
        {
            Assert.Same(parent, Activity.Current);
            return Task.FromResult(new ProbeResult(false, 0));
        });
        var strategy = new KeepaliveStrategy(probe, new VirtualClock(), 300,
            "private-agent", new[] { "private-handle" });
        var report = await strategy.ReconcileAsync();
        Assert.Equal(1, report.Probed);
        Assert.Equal(1, report.Failures);
        var operations = new SessionOperations
        {
            BeforeCreate = () =>
            {
                Assert.Same(parent, Activity.Current);
                return Task.CompletedTask;
            },
        };
        var pool = new PreProvisionPoolStrategy(new SessionController(operations), new InMemoryStore(),
            "private-agent", 1, new VirtualClock(autoAdvance: true));
        Assert.Equal(1, (await pool.ReconcileAsync()).Created);
        Assert.Equal(0, (await pool.ReconcileAsync()).Created);
        Assert.Equal("private-handle-1", await pool.AcquireAsync());
        Assert.Same(parent, Activity.Current);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CallbackExceptions_RemainUnmodified(bool probe)
    {
        using var capture = new Capture();
        var failure = new InvalidOperationException("AccountKey=private-key");
        var events = new List<TelemetryEvent>();
        var telemetry = new Telemetry(item =>
        {
            events.Add(item);
            throw failure;
        });
        IWarmupStrategy strategy = probe
            ? new KeepaliveStrategy(new CallableProbe((_, _) => Task.FromResult(new ProbeResult(true, 0))),
                new VirtualClock(), 300, "private-agent", new[] { "private-handle" }, telemetry)
            : new PreProvisionPoolStrategy(new SessionController(new SessionOperations()),
                new InMemoryStore(), "private-agent", 0, new VirtualClock(), telemetry: telemetry);

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => strategy.ReconcileAsync()));
        Assert.Same(capture.Parent, Activity.Current);
        Assert.Single(events);
        var span = Assert.Single(capture.Stopped, item => item.OperationName.StartsWith("stoke.warmup.", StringComparison.Ordinal));
        Assert.Equal(probe ? ActivityStatusCode.Ok : ActivityStatusCode.Error, span.Status);
        AssertSafe(span, probe ? "keepalive" : "pre-provision-pool");
    }

    private sealed class Clock : IClock
    {
        private readonly VirtualClock _inner = new(autoAdvance: true);
        public Func<Task>? BeforeDelay { get; set; }
        public double Now() => _inner.Now();
        public async Task DelayAsync(double seconds)
        {
            if (BeforeDelay is not null)
            {
                await BeforeDelay();
            }
            await _inner.DelayAsync(seconds);
        }
    }

    private sealed class Store : IDurableStoreProvider
    {
        private readonly InMemoryStore _inner = new();
        public Func<Task>? BeforeRead { get; set; }
        public Func<Task>? BeforeWrite { get; set; }

        public async Task<StoreRecord> ReadAsync(string id, string partitionKey, CancellationToken cancellationToken = default)
        {
            if (BeforeRead is not null)
            {
                await BeforeRead();
            }
            return await _inner.ReadAsync(id, partitionKey, cancellationToken);
        }

        public async Task<StoreRecord> CreateAsync(StoreRecord record, CancellationToken cancellationToken = default)
        {
            if (BeforeWrite is not null)
            {
                await BeforeWrite();
            }
            return await _inner.CreateAsync(record, cancellationToken);
        }

        public Task<StoreRecord> UpsertAsync(StoreRecord record, string? expectedEtag, CancellationToken cancellationToken = default) =>
            _inner.UpsertAsync(record, expectedEtag, cancellationToken);
        public Task DeleteAsync(string id, string partitionKey, string? expectedEtag = null, CancellationToken cancellationToken = default) =>
            _inner.DeleteAsync(id, partitionKey, expectedEtag, cancellationToken);
        public Task<IReadOnlyList<StoreRecord>> QueryByPartitionAsync(string partitionKey, string? typeFilter = null, CancellationToken cancellationToken = default) =>
            _inner.QueryByPartitionAsync(partitionKey, typeFilter, cancellationToken);
    }

    private sealed class SessionOperations : ISessionOperations
    {
        public Func<Task>? BeforeCreate { get; set; }
        public Func<Task>? BeforeGet { get; set; }
        public int Created { get; private set; }

        public async Task<RawSession> CreateSessionAsync(
            string agentDefinitionId, int idleTimeoutSeconds, CancellationToken cancellationToken = default)
        {
            if (BeforeCreate is not null)
            {
                await BeforeCreate();
            }
            Created++;
            return new RawSession($"private-handle-{Created}", "active");
        }

        public async Task<RawSession> GetSessionAsync(
            string agentDefinitionId, string agentSessionId, CancellationToken cancellationToken = default)
        {
            if (BeforeGet is not null)
            {
                await BeforeGet();
            }
            return new RawSession(agentSessionId, "active");
        }

        public Task<IReadOnlyList<RawSession>> ListSessionsAsync(
            string agentDefinitionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RawSession>>(Array.Empty<RawSession>());

        public Task StopSessionAsync(
            string agentDefinitionId, string agentSessionId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteSessionAsync(
            string agentDefinitionId, string agentSessionId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private static void AssertSafe(Activity span, string strategy)
    {
        Assert.Equal("Foundry.Stoke", span.Source.Name);
        Assert.Empty(span.Events);
        Assert.Null(span.StatusDescription);
        Assert.Equal(new KeyValuePair<string, object?>("stoke.warmup.strategy", strategy),
            Assert.Single(span.TagObjects));
    }

    private sealed class Capture : IDisposable
    {
        public Activity Parent { get; } = new Activity("parent").SetIdFormat(ActivityIdFormat.W3C).Start();
        public List<Activity> Started { get; } = new();
        public List<Activity> Stopped { get; } = new();
        private readonly ActivityListener _listener;

        public Capture()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "Foundry.Stoke",
                Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                {
                    if (options.Parent.TraceId != Parent.TraceId)
                    {
                        return ActivitySamplingResult.None;
                    }
                    if (options.Name.StartsWith("stoke.warmup.", StringComparison.Ordinal))
                    {
                        var strategy = options.Name == "stoke.warmup.probe" ? "keepalive" : "pre-provision-pool";
                        Assert.Equal(new KeyValuePair<string, object?>("stoke.warmup.strategy", strategy),
                            Assert.Single(options.Tags!));
                    }
                    return ActivitySamplingResult.AllDataAndRecorded;
                },
                ActivityStarted = span =>
                {
                    if (span.OperationName.StartsWith("stoke.warmup.", StringComparison.Ordinal))
                    {
                        AssertSafe(span, span.OperationName == "stoke.warmup.probe" ? "keepalive" : "pre-provision-pool");
                    }
                    Started.Add(span);
                },
                ActivityStopped = Stopped.Add,
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public void Dispose()
        {
            _listener.Dispose();
            Parent.Dispose();
        }
    }
}

[CollectionDefinition("WarmupTracing", DisableParallelization = true)]
public sealed class WarmupTracingCollection;
