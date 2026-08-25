using System.Text;
using System.Text.Json.Nodes;
using Foundry.Stoke.Errors;
using Foundry.Stoke.Store;

namespace Foundry.Stoke.Tests.Conformance;

/// <summary>
/// FileSystem-provider tests that go beyond the shared conformance fixtures:
/// cross-restart persistence and the SEC-001/002/006 security controls
/// (mirrors python/tests/test_file_system_security.py). Each test uses an
/// isolated temporary directory and removes it afterwards.
/// </summary>
[Trait("Category", "Store")]
public sealed class FileSystemStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("stoke-fs-store-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static StoreRecord Record(
        string id = "s1", string partition = "agent-a", string type = "tracked-session")
    {
        return new StoreRecord(id, partition, type, new JsonObject { ["n"] = 1 });
    }

    // --- persistence across restarts ---

    [Fact]
    public async Task RecordSurvivesAcrossStoreInstances()
    {
        var writer = new FileSystemStore(_dir);
        await writer.CreateAsync(Record());

        var reader = new FileSystemStore(_dir);
        var read = await reader.ReadAsync("s1", "agent-a");

        Assert.True(JsonNode.DeepEquals(read.Payload, new JsonObject { ["n"] = 1 }));
        Assert.False(string.IsNullOrEmpty(read.Etag));
    }

    // --- SEC-001 path sanitization ---

    [Fact]
    public async Task EmptyKeyRejected()
    {
        var store = new FileSystemStore(_dir);
        await Assert.ThrowsAsync<InvalidRecordKeyException>(() => store.CreateAsync(Record(id: "")));
        await Assert.ThrowsAsync<InvalidRecordKeyException>(() => store.CreateAsync(Record(partition: "")));
    }

    [Fact]
    public async Task OversizedKeyRejected()
    {
        var store = new FileSystemStore(_dir);
        var longId = new string('x', FileSystemStore.MaxKeyLength + 1);
        await Assert.ThrowsAsync<InvalidRecordKeyException>(() => store.CreateAsync(Record(id: longId)));
    }

    [Fact]
    public async Task PathTraversalKeysAreConfined()
    {
        var store = new FileSystemStore(_dir);
        await store.CreateAsync(Record(id: "../../evil", partition: "../../secret"));

        var read = await store.ReadAsync("../../evil", "../../secret");
        Assert.True(JsonNode.DeepEquals(read.Payload, new JsonObject { ["n"] = 1 }));

        // The traversal-looking keys are hashed, so the record file lands under
        // the base directory and nothing escapes it.
        var recordPath = store.RecordPath("../../evil", "../../secret");
        var basePrefix = _dir + Path.DirectorySeparatorChar;
        Assert.StartsWith(basePrefix, recordPath, StringComparison.Ordinal);
        Assert.True(File.Exists(recordPath));

        var allFiles = Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories);
        Assert.All(allFiles, path =>
            Assert.StartsWith(basePrefix, path, StringComparison.Ordinal));
    }

    // --- SEC-002 schema-safe deserialization ---

    [Fact]
    public async Task CorruptedFileRaisesTypedError()
    {
        var store = new FileSystemStore(_dir);
        await store.CreateAsync(Record());

        var recordPath = store.RecordPath("s1", "agent-a");
        await File.WriteAllBytesAsync(recordPath, Encoding.UTF8.GetBytes("{ this is not valid json"));

        await Assert.ThrowsAsync<CorruptedRecordException>(() => store.ReadAsync("s1", "agent-a"));
    }

    [Fact]
    public async Task PartialRecordRaisesTypedError()
    {
        var store = new FileSystemStore(_dir);
        var recordPath = store.RecordPath("s1", "agent-a");
        Directory.CreateDirectory(Path.GetDirectoryName(recordPath)!);

        // Valid JSON, allowed type, but missing required fields.
        var partial = new JsonObject { ["type"] = "tracked-session", ["id"] = "s1" };
        await File.WriteAllTextAsync(recordPath, partial.ToJsonString());

        await Assert.ThrowsAsync<CorruptedRecordException>(() => store.ReadAsync("s1", "agent-a"));
    }

    [Fact]
    public async Task UnknownTypeOnWriteRejected()
    {
        var store = new FileSystemStore(_dir);
        await Assert.ThrowsAsync<UnknownRecordTypeException>(
            () => store.CreateAsync(Record(type: "malicious-type")));
    }

    [Fact]
    public async Task UnknownTypeOnReadRejected()
    {
        var store = new FileSystemStore(_dir);
        var recordPath = store.RecordPath("s1", "agent-a");
        Directory.CreateDirectory(Path.GetDirectoryName(recordPath)!);

        var forged = new JsonObject
        {
            ["id"] = "s1",
            ["partition_key"] = "agent-a",
            ["type"] = "malicious-type",
            ["payload"] = new JsonObject(),
            ["etag"] = "e1",
            ["created_at"] = "2026-08-21T00:00:00.0000000+00:00",
            ["updated_at"] = "2026-08-21T00:00:00.0000000+00:00",
        };
        await File.WriteAllTextAsync(recordPath, forged.ToJsonString());

        await Assert.ThrowsAsync<UnknownRecordTypeException>(() => store.ReadAsync("s1", "agent-a"));
    }

    [Fact]
    public async Task OversizedFileRejected()
    {
        var store = new FileSystemStore(_dir, maxFileBytes: 64);
        var big = new StoreRecord(
            "s1", "agent-a", "tracked-session", new JsonObject { ["blob"] = new string('x', 500) });
        await Assert.ThrowsAsync<CorruptedRecordException>(() => store.CreateAsync(big));
    }

    // --- SEC-006 cross-process lock with acquisition timeout ---

    [Fact]
    public async Task LockAcquisitionTimeout()
    {
        var store = new FileSystemStore(_dir, lockTimeout: TimeSpan.FromMilliseconds(200));
        var recordPath = store.RecordPath("s1", "agent-a");
        Directory.CreateDirectory(Path.GetDirectoryName(recordPath)!);
        var lockPath = recordPath + ".lock";

        using var holder = new FileStream(
            lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        await Assert.ThrowsAsync<LockTimeoutException>(() => store.CreateAsync(Record()));
    }
}
