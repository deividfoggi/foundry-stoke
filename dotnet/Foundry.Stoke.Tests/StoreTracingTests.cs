using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Foundry.Stoke.Errors;
using Foundry.Stoke.Store;

namespace Foundry.Stoke.Tests;

[Collection("StoreTracing")]
public sealed class StoreTracingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "stoke-tracing-" + Guid.NewGuid());

    [Theory]
    [InlineData("in_memory")]
    [InlineData("file_system")]
    public async Task PublicCrudAndQuery_EmitOneSafeSpanEach(string provider)
    {
        using var capture = new Capture(provider);
        var store = CreateStore(provider);
        var record = Record();
        var created = await store.CreateAsync(record);
        Assert.Equal(created.Etag, (await store.ReadAsync(record.Id, record.PartitionKey)).Etag);
        Assert.Single(await store.QueryByPartitionAsync(record.PartitionKey));
        var updated = await store.UpsertAsync(record, created.Etag);
        Assert.NotEqual(created.Etag, updated.Etag);
        await store.DeleteAsync(record.Id, record.PartitionKey, updated.Etag);
        Assert.Empty(await store.QueryByPartitionAsync(record.PartitionKey));
        Assert.Same(capture.Parent, Activity.Current);
        Assert.Equal(new[]
        {
            "stoke.store.write", "stoke.store.read", "stoke.store.read",
            "stoke.store.write", "stoke.store.write", "stoke.store.read",
        }, capture.Stopped.Select(span => span.OperationName));
        Assert.Equal(6, capture.Started.Count);
        Assert.All(capture.Stopped, span =>
        {
            Assert.Equal(capture.Parent.TraceId, span.TraceId);
            Assert.Equal(capture.Parent.SpanId, span.ParentSpanId);
            Assert.True(span.IsStopped);
            Assert.True(span.Duration >= TimeSpan.Zero);
            Assert.Equal(ActivityStatusCode.Ok, span.Status);
            AssertSafe(span, provider);
        });
    }

    [Theory]
    [InlineData("in_memory")]
    [InlineData("file_system")]
    public async Task TypedFailures_PreserveContractAndEmitSafeErrors(string provider)
    {
        using var capture = new Capture(provider);
        var store = CreateStore(provider);
        var record = Record();
        record.Id = "AccountKey=private-key; token=private-token";
        var created = await store.CreateAsync(record);
        capture.Stopped.Clear();
        await Assert.ThrowsAsync<AlreadyExistsException>(() => store.CreateAsync(record));
        await Assert.ThrowsAsync<NotFoundException>(() => store.ReadAsync("missing-" + record.Id, record.PartitionKey));
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => store.UpsertAsync(record, "private-etag"));
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => store.DeleteAsync(record.Id, record.PartitionKey, "private-etag"));
        await Assert.ThrowsAsync<NotFoundException>(() => store.DeleteAsync("missing-" + record.Id, record.PartitionKey));
        Assert.Same(capture.Parent, Activity.Current);
        Assert.Equal(new[]
        {
            "stoke.store.write", "stoke.store.read", "stoke.store.write",
            "stoke.store.write", "stoke.store.write",
        }, capture.Stopped.Select(span => span.OperationName));
        Assert.All(capture.Stopped, span =>
        {
            Assert.Equal(ActivityStatusCode.Error, span.Status);
            Assert.Equal(capture.Parent.SpanId, span.ParentSpanId);
            AssertSafe(span, provider);
        });
        Assert.Equal(created.Etag, (await store.ReadAsync(record.Id, record.PartitionKey)).Etag);
    }

    [Theory]
    [InlineData("in_memory")]
    [InlineData("file_system")]
    public async Task Cancellation_PreservesTokensAndCanceledTasksForAllOperations(string provider)
    {
        using var capture = new Capture(provider);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var store = CreateStore(provider);
        var record = Record();
        var operations = new Func<Task>[]
        {
            () => store.CreateAsync(record, cancellation.Token),
            () => store.ReadAsync(record.Id, record.PartitionKey, cancellation.Token),
            () => store.UpsertAsync(record, null, cancellation.Token),
            () => store.DeleteAsync(record.Id, record.PartitionKey, cancellationToken: cancellation.Token),
            () => store.QueryByPartitionAsync(record.PartitionKey, cancellationToken: cancellation.Token),
        };
        foreach (var operation in operations)
        {
            var task = operation();
            var caught = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.Equal(cancellation.Token, caught.CancellationToken);
            Assert.True(task.IsCanceled);
            Assert.Same(capture.Parent, Activity.Current);
        }
        Assert.Equal(5, capture.Stopped.Count);
        Assert.All(capture.Stopped, span =>
        {
            Assert.Equal(ActivityStatusCode.Error, span.Status);
            AssertSafe(span, provider);
        });
        Assert.Empty(await store.QueryByPartitionAsync(record.PartitionKey));
    }

    [Fact]
    public async Task CorruptedFile_ReadAndQueryEmitSafeErrors()
    {
        using var capture = new Capture("file_system");
        var store = CreateStore("file_system");
        var record = Record();
        await store.CreateAsync(record);
        foreach (var filePath in Directory.EnumerateFiles(_directory, "*.json", SearchOption.AllDirectories))
        {
            await File.WriteAllTextAsync(filePath, "AccountKey=private-key broken JSON");
        }
        capture.Stopped.Clear();
        await Assert.ThrowsAsync<CorruptedRecordException>(() => store.ReadAsync(record.Id, record.PartitionKey));
        await Assert.ThrowsAsync<CorruptedRecordException>(() => store.QueryByPartitionAsync(record.PartitionKey));
        Assert.Equal(2, capture.Stopped.Count);
        Assert.All(capture.Stopped, span =>
        {
            Assert.Equal("stoke.store.read", span.OperationName);
            Assert.Equal(ActivityStatusCode.Error, span.Status);
            AssertSafe(span, "file_system");
        });
        Assert.Same(capture.Parent, Activity.Current);
    }

    [Theory]
    [InlineData("in_memory", false)]
    [InlineData("file_system", false)]
    [InlineData("in_memory", true)]
    [InlineData("file_system", true)]
    public async Task SpanCoversRealOperation_AndPreservesOriginalException(string provider, bool fail)
    {
        using var capture = new Capture(provider);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<Activity?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("/private/path AccountKey=private-key; token=private-token");
        var options = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        options.Converters.Add(new GatedValueConverter(() =>
        {
            entered.TrySetResult(Activity.Current);
            if (!release.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("test gate was not released");
            }
            if (fail)
            {
                throw failure;
            }
        }));
        options.MakeReadOnly();
        var record = Record();
        record.Payload["gated"] = JsonValue.Create(new GatedValue(),
            (JsonTypeInfo<GatedValue>)options.GetTypeInfo(typeof(GatedValue)));
        var store = CreateStore(provider);
        Activity? restored = null;
        var task = Task.Run(async () =>
        {
            try
            {
                return await store.CreateAsync(record);
            }
            finally
            {
                restored = Activity.Current;
            }
        });
        try
        {
            var active = await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.NotNull(active);
            Assert.NotSame(capture.Parent, active);
            Assert.Equal("stoke.store.write", active.OperationName);
            Assert.False(active.IsStopped);
            Assert.False(task.IsCompleted);
            Assert.Empty(capture.Stopped);
        }
        finally
        {
            release.Set();
            if (fail)
            {
                Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => task));
            }
            else
            {
                Assert.Equal(record.Id, (await task).Id);
            }
        }
        Assert.Same(capture.Parent, Activity.Current);
        Assert.Same(capture.Parent, restored);
        var span = Assert.Single(capture.Stopped);
        Assert.Equal(capture.Parent.SpanId, span.ParentSpanId);
        Assert.True(span.IsStopped);
        Assert.Equal(fail ? ActivityStatusCode.Error : ActivityStatusCode.Ok, span.Status);
        AssertSafe(span, provider);
    }

    [Theory]
    [InlineData("in_memory")]
    [InlineData("file_system")]
    public async Task NoListener_PreservesStoreContractAndParent(string provider)
    {
        using var parent = new Activity("parent").Start();
        var store = CreateStore(provider);
        var record = Record();
        var created = await store.CreateAsync(record);
        Assert.Equal(created.Etag, (await store.ReadAsync(record.Id, record.PartitionKey)).Etag);
        Assert.Single(await store.QueryByPartitionAsync(record.PartitionKey));
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => store.UpsertAsync(record, "wrong-etag"));
        var updated = await store.UpsertAsync(record, created.Etag);
        await store.DeleteAsync(record.Id, record.PartitionKey, updated.Etag);
        Assert.Empty(await store.QueryByPartitionAsync(record.PartitionKey));
        await Assert.ThrowsAsync<NotFoundException>(() => store.ReadAsync(record.Id, record.PartitionKey));
        Assert.Same(parent, Activity.Current);
    }

    private sealed class GatedValue;

    private sealed class GatedValueConverter(Action onWrite) : JsonConverter<GatedValue>
    {
        public override GatedValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, GatedValue value, JsonSerializerOptions options)
        {
            onWrite();
            writer.WriteStringValue("value");
        }
    }

    private IDurableStoreProvider CreateStore(string provider) =>
        provider == "in_memory" ? new InMemoryStore() : new FileSystemStore(_directory);

    private static StoreRecord Record() => new("private-id", "private-partition", "tracked-session",
        new JsonObject { ["secret"] = "AccountKey=private-key; token=private-token" });

    private static void AssertSafe(Activity span, string provider)
    {
        Assert.Equal("Foundry.Stoke", span.Source.Name);
        Assert.Empty(span.Events);
        Assert.Null(span.StatusDescription);
        Assert.Equal(new KeyValuePair<string, object?>("stoke.store.provider", provider),
            Assert.Single(span.TagObjects));
    }

    private sealed class Capture : IDisposable
    {
        public Activity Parent { get; } = new Activity("parent").SetIdFormat(ActivityIdFormat.W3C).Start();
        public List<Activity> Started { get; } = new();
        public List<Activity> Stopped { get; } = new();
        private readonly ActivityListener _listener;

        public Capture(string provider)
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
                    Assert.Equal(new KeyValuePair<string, object?>("stoke.store.provider", provider),
                        Assert.Single(options.Tags!));
                    return ActivitySamplingResult.AllDataAndRecorded;
                },
                ActivityStarted = span =>
                {
                    AssertSafe(span, provider);
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

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}

[CollectionDefinition("StoreTracing", DisableParallelization = true)]
public sealed class StoreTracingCollection;
