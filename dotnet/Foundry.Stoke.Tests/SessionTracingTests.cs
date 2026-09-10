using System.Diagnostics;
using Foundry.Stoke.Errors;
using Foundry.Stoke.Observability;
using Foundry.Stoke.Session;

namespace Foundry.Stoke.Tests;

/// <summary>T025: real activities across the asynchronous session lifecycle.</summary>
public sealed class SessionTracingTests
{
    private const string Handle = "private-session-handle";
    private const string Secret = "AccountKey=private-key; token=private-token Bearer private-bearer";

    [Theory]
    [InlineData("create", "success")]
    [InlineData("get", "success")]
    [InlineData("stop", "success")]
    [InlineData("delete", "success")]
    [InlineData("create", "error")]
    [InlineData("get", "error")]
    [InlineData("stop", "error")]
    [InlineData("delete", "error")]
    [InlineData("create", "cancel")]
    [InlineData("get", "cancel")]
    [InlineData("stop", "cancel")]
    [InlineData("delete", "cancel")]
    public async Task OperationSpan_CoversAwait_OutcomeAndRedaction(string operation, string outcome)
    {
        using var capture = new Capture();
        using var cancellation = new CancellationTokenSource();
        var failure = new InvalidOperationException($"{Handle} {Secret}");
        var operations = new Operations { Failure = outcome == "error" ? failure : null };
        var controller = new SessionController(operations);
        var task = Invoke(controller, operation, cancellation.Token);
        await operations.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            Assert.Same(capture.Parent, Activity.Current);
            Assert.NotSame(capture.Parent, operations.BeforeAwait);
            Assert.NotNull(operations.BeforeAwait);
            Assert.False(operations.BeforeAwait.IsStopped);
            Assert.Empty(capture.Stopped);
            var started = Assert.Single(capture.Started);
            AssertSafe(started);
        }
        finally
        {
            if (outcome == "cancel")
            {
                cancellation.Cancel();
            }
            operations.Release.TrySetResult();
            if (outcome == "success")
            {
                await task;
            }
            else if (outcome == "error")
            {
                Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => task));
            }
            else
            {
                var caught = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
                Assert.Equal(cancellation.Token, caught.CancellationToken);
                Assert.True(task.IsCanceled);
            }
        }

        Assert.Same(capture.Parent, Activity.Current);
        var span = Assert.Single(capture.Stopped);
        Assert.Equal($"stoke.session.{operation}", span.OperationName);
        Assert.Equal("Foundry.Stoke", span.Source.Name);
        Assert.Equal(capture.Parent.TraceId, span.TraceId);
        Assert.Equal(capture.Parent.SpanId, span.ParentSpanId);
        Assert.True(span.IsStopped);
        Assert.True(span.Duration >= TimeSpan.Zero);
        Assert.Equal(outcome == "success" ? ActivityStatusCode.Ok : ActivityStatusCode.Error, span.Status);
        AssertSafe(span);
        if (operation == "create")
        {
            Assert.Empty(span.TagObjects);
        }
        else
        {
            Assert.Equal(Redaction.HashSessionId(Handle), Assert.Single(span.TagObjects).Value);
        }
        if (outcome != "cancel")
        {
            Assert.Same(operations.BeforeAwait, operations.AfterAwait);
        }

        if (operation == "delete" && outcome != "success")
        {
            operations.Failure = null;
            Assert.Equal(Handle, (await controller.GetSessionAsync(Secret, Handle)).AgentSessionId);
        }
    }

    [Fact]
    public async Task ValidationAndClosedSessionFailures_AreTraced()
    {
        using var capture = new Capture();
        var operations = new Operations();
        operations.Release.SetResult();
        var controller = new SessionController(operations);
        await Assert.ThrowsAsync<InvalidIdleTimeoutException>(() => controller.CreateSessionAsync(Secret, 60));
        await controller.DeleteSessionAsync(Secret, Handle);
        foreach (var operation in new[] { "get", "stop", "delete" })
        {
            await Assert.ThrowsAsync<SessionClosedException>(() => Invoke(controller, operation));
            Assert.Same(capture.Parent, Activity.Current);
        }
        Assert.Equal(new[]
        {
            ActivityStatusCode.Error, ActivityStatusCode.Ok, ActivityStatusCode.Error,
            ActivityStatusCode.Error, ActivityStatusCode.Error,
        }, capture.Stopped.Select(span => span.Status));
        Assert.All(capture.Stopped, AssertSafe);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("get")]
    public async Task TranslationFailure_IsInsideSpan(string operation)
    {
        using var capture = new Capture();
        var operations = new Operations();
        operations.Release.SetResult();
        var failure = new InvalidOperationException($"{Handle} {Secret}");
        var controller = new SessionController(operations, _ =>
        {
            Assert.Equal($"stoke.session.{operation}", Activity.Current?.OperationName);
            throw failure;
        });
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(controller, operation)));
        Assert.Same(capture.Parent, Activity.Current);
        var span = Assert.Single(capture.Stopped);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        AssertSafe(span);
    }

    [Fact]
    public async Task List_RemainsUninstrumented()
    {
        using var capture = new Capture();
        Assert.Empty(await new SessionController(new Operations()).ListSessionsAsync("agent"));
        Assert.Empty(capture.Started);
        Assert.Empty(capture.Stopped);
    }

    [Fact]
    public async Task NoListener_PreservesLifecycleAndCallback()
    {
        using var parent = new Activity("parent").Start();
        var operations = new Operations();
        operations.Release.SetResult();
        var controller = new SessionController(operations);
        foreach (var operation in new[] { "create", "get", "stop", "delete" })
        {
            await Invoke(controller, operation);
            Assert.Same(parent, operations.BeforeAwait);
            Assert.Same(parent, Activity.Current);
        }
        await Assert.ThrowsAsync<SessionClosedException>(() => controller.GetSessionAsync(Secret, Handle));
        var events = new List<TelemetryEvent>();
        var telemetry = new Telemetry(events.Add);
        telemetry.Emit("callback", new Dictionary<string, object?>
        {
            ["stoke.agent_session_id"] = Handle,
        });
        var result = Assert.Single(events);
        Assert.Equal(Redaction.HashSessionId(Handle), result.Attributes["stoke.agent_session_id"]);
    }

    private static Task Invoke(SessionController controller, string operation, CancellationToken token = default) =>
        operation switch
        {
            "create" => controller.CreateSessionAsync(Secret, cancellationToken: token),
            "get" => controller.GetSessionAsync(Secret, Handle, cancellationToken: token),
            "stop" => controller.StopSessionAsync(Secret, Handle, token),
            "delete" => controller.DeleteSessionAsync(Secret, Handle, token),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    private static void AssertSafe(Activity span)
    {
        Assert.Empty(span.Events);
        Assert.Null(span.StatusDescription);
        var emitted = string.Join(" ", span.TagObjects.Select(tag => $"{tag.Key}={tag.Value}"));
        foreach (var sensitive in new[] { Handle, "private-key", "private-token", "private-bearer" })
        {
            Assert.DoesNotContain(sensitive, emitted);
        }
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
                    options.Parent.TraceId == Parent.TraceId
                        ? ActivitySamplingResult.AllDataAndRecorded : ActivitySamplingResult.None,
                ActivityStarted = Started.Add,
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

    private sealed class Operations : ISessionOperations
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? Failure { get; set; }
        public Activity? BeforeAwait { get; private set; }
        public Activity? AfterAwait { get; private set; }

        private async Task Wait(CancellationToken token)
        {
            BeforeAwait = Activity.Current;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(token);
            AfterAwait = Activity.Current;
            if (Failure is not null)
            {
                throw Failure;
            }
        }

        public async Task<RawSession> CreateSessionAsync(string agentDefinitionId, int idleTimeoutSeconds, CancellationToken cancellationToken = default)
        {
            await Wait(cancellationToken);
            return new RawSession(Handle, "active");
        }

        public async Task<RawSession> GetSessionAsync(string agentDefinitionId, string agentSessionId, CancellationToken cancellationToken = default)
        {
            await Wait(cancellationToken);
            return new RawSession(Handle, "active");
        }

        public Task<IReadOnlyList<RawSession>> ListSessionsAsync(string agentDefinitionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RawSession>>(Array.Empty<RawSession>());

        public Task StopSessionAsync(string agentDefinitionId, string agentSessionId, CancellationToken cancellationToken = default) =>
            Wait(cancellationToken);

        public Task DeleteSessionAsync(string agentDefinitionId, string agentSessionId, CancellationToken cancellationToken = default) =>
            Wait(cancellationToken);
    }
}
