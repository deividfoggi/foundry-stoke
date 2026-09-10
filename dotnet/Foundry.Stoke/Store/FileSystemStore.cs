using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Foundry.Stoke.Errors;

namespace Foundry.Stoke.Store;

/// <summary>
/// FileSystem (JSON) durable store provider (US2, T035). Persists each
/// <see cref="StoreRecord"/> as a JSON file on disk, one directory per
/// partition. Intended for local development, not production: the cross-process
/// advisory lock is not guaranteed on network filesystems (ADR 0001). Mirrors
/// the Python <c>FileSystemStore</c>, including its security controls.
///
/// Security controls (security-review-architecture.md):
/// <list type="bullet">
/// <item>SEC-001 path sanitization: file and directory names are SHA-256 hex
/// digests of the id/partition key, confined to the base directory via
/// canonical-path validation; empty or oversized keys are rejected.</item>
/// <item>SEC-002 schema-safe deserialization: JSON only (never a polymorphic
/// deserializer); the record type must be in an allowlist; corrupted, partial,
/// or oversized files raise a typed error.</item>
/// <item>SEC-006 concurrency: the full read-check-etag-write cycle runs under a
/// cross-process file lock (FileShare.None) with an acquisition timeout.</item>
/// </list>
///
/// All blocking file I/O runs on a worker thread so the async surface stays
/// non-blocking (mirrors the Python asyncio.to_thread offload).
/// </summary>
public sealed class FileSystemStore : IDurableStoreProvider
{
    /// <summary>Maximum accepted length of an id/partition key (SEC-001).</summary>
    public const int MaxKeyLength = 512;

    /// <summary>Default per-record file size ceiling: 1 MiB (SEC-002).</summary>
    public const long DefaultMaxFileBytes = 1 * 1024 * 1024;

    private static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LockPollInterval = TimeSpan.FromMilliseconds(50);

    private readonly string _base;
    private readonly FrozenSet<string> _allowedTypes;
    private readonly TimeSpan _lockTimeout;
    private readonly long _maxFileBytes;

    public FileSystemStore(
        string baseDir,
        FrozenSet<string>? allowedTypes = null,
        TimeSpan? lockTimeout = null,
        long maxFileBytes = DefaultMaxFileBytes)
    {
        _base = Path.GetFullPath(baseDir);
        Directory.CreateDirectory(_base);
        _allowedTypes = allowedTypes ?? RecordTypes.Known;
        _lockTimeout = lockTimeout ?? DefaultLockTimeout;
        _maxFileBytes = maxFileBytes;
    }

    // --- async surface (non-blocking) ---

    public Task<StoreRecord> CreateAsync(StoreRecord record, CancellationToken cancellationToken = default)
        => Task.Run(() => CreateSync(record), cancellationToken);

    public Task<StoreRecord> ReadAsync(string id, string partitionKey, CancellationToken cancellationToken = default)
        => Task.Run(() => ReadFile(RecordPath(id, partitionKey)), cancellationToken);

    public Task<StoreRecord> UpsertAsync(StoreRecord record, string? expectedEtag, CancellationToken cancellationToken = default)
        => Task.Run(() => UpsertSync(record, expectedEtag), cancellationToken);

    public Task DeleteAsync(string id, string partitionKey, string? expectedEtag = null, CancellationToken cancellationToken = default)
        => Task.Run(() => DeleteSync(id, partitionKey, expectedEtag), cancellationToken);

    public Task<IReadOnlyList<StoreRecord>> QueryByPartitionAsync(string partitionKey, string? typeFilter = null, CancellationToken cancellationToken = default)
        => Task.Run(() => QuerySync(partitionKey, typeFilter), cancellationToken);

    // --- path sanitization (SEC-001) ---

    private static void ValidateKey(string value, string name)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidRecordKeyException($"{name} must be a non-empty string");
        }

        if (value.Length > MaxKeyLength)
        {
            throw new InvalidRecordKeyException(
                $"{name} exceeds the maximum length of {MaxKeyLength} characters");
        }
    }

    private static string Hash(string value)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    internal string RecordPath(string id, string partitionKey)
    {
        ValidateKey(partitionKey, "partition_key");
        ValidateKey(id, "id");
        var partitionDir = Path.Combine(_base, Hash(partitionKey));
        var recordPath = Path.GetFullPath(Path.Combine(partitionDir, Hash(id) + ".json"));

        // Defense in depth: the hashed names are hex-only so traversal is
        // already impossible, but confirm the resolved path stays under the base.
        var basePrefix = _base.EndsWith(Path.DirectorySeparatorChar)
            ? _base
            : _base + Path.DirectorySeparatorChar;
        if (!recordPath.StartsWith(basePrefix, StringComparison.Ordinal))
        {
            throw new InvalidRecordKeyException("resolved record path escapes the base directory");
        }

        return recordPath;
    }

    // --- lock (SEC-006) ---

    private FileStream AcquireLock(string recordPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(recordPath)!);
        var lockPath = recordPath + ".lock";
        var deadline = DateTime.UtcNow + _lockTimeout;
        while (true)
        {
            try
            {
                // FileShare.None takes an exclusive OS-level lock on the file
                // handle, blocking other processes for the read-check-write cycle.
                return new FileStream(
                    lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exc)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    throw new LockTimeoutException(
                        $"could not acquire lock for {Path.GetFileName(recordPath)} "
                        + $"within {_lockTimeout.TotalSeconds}s",
                        exc);
                }

                Thread.Sleep(LockPollInterval);
            }
        }
    }

    // --- safe (de)serialization (SEC-002) ---

    private StoreRecord ReadFile(string recordPath)
    {
        byte[] raw;
        try
        {
            raw = File.ReadAllBytes(recordPath);
        }
        catch (FileNotFoundException exc)
        {
            throw new NotFoundException($"no record file at {Path.GetFileName(recordPath)}", exc);
        }
        catch (DirectoryNotFoundException exc)
        {
            throw new NotFoundException($"no record file at {Path.GetFileName(recordPath)}", exc);
        }

        if (raw.Length > _maxFileBytes)
        {
            throw new CorruptedRecordException(
                $"record file {Path.GetFileName(recordPath)} exceeds size limit");
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(raw);
        }
        catch (JsonException exc)
        {
            throw new CorruptedRecordException(
                $"record file {Path.GetFileName(recordPath)} is not valid JSON", exc);
        }

        if (node is not JsonObject data)
        {
            throw new CorruptedRecordException(
                $"record file {Path.GetFileName(recordPath)} is not a JSON object");
        }

        var recordType = (string?)data["type"];
        if (recordType is null || !_allowedTypes.Contains(recordType))
        {
            throw new UnknownRecordTypeException(
                $"record type '{recordType}' is not in the allowlist");
        }

        try
        {
            return StoreRecord.FromJsonObject(data);
        }
        catch (Exception exc) when (
            exc is FormatException
                or ArgumentException
                or InvalidOperationException
                or InvalidCastException
                or OverflowException
                or KeyNotFoundException)
        {
            throw new CorruptedRecordException(
                $"record file {Path.GetFileName(recordPath)} has missing or malformed fields", exc);
        }
    }

    private void WriteFile(string recordPath, StoreRecord record)
    {
        if (!_allowedTypes.Contains(record.Type))
        {
            throw new UnknownRecordTypeException(
                $"record type '{record.Type}' is not in the allowlist");
        }

        var payload = Encoding.UTF8.GetBytes(record.ToJsonObject().ToJsonString());
        if (payload.Length > _maxFileBytes)
        {
            throw new CorruptedRecordException("serialized record exceeds size limit");
        }

        var tmpPath = recordPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(tmpPath, payload);
        File.Move(tmpPath, recordPath, overwrite: true); // atomic rename within the same dir
    }

    // --- sync CRUD under lock ---

    private StoreRecord CreateSync(StoreRecord record)
    {
        var recordPath = RecordPath(record.Id, record.PartitionKey);
        using var _ = AcquireLock(recordPath);
        if (File.Exists(recordPath))
        {
            throw new AlreadyExistsException($"record already exists for id='{record.Id}'");
        }

        var now = DateTimeOffset.UtcNow;
        var stored = record.Clone();
        stored.Etag = NewEtag();
        stored.CreatedAt = now;
        stored.UpdatedAt = now;
        WriteFile(recordPath, stored);
        return stored;
    }

    private StoreRecord UpsertSync(StoreRecord record, string? expectedEtag)
    {
        var recordPath = RecordPath(record.Id, record.PartitionKey);
        using var _ = AcquireLock(recordPath);
        StoreRecord? existing = null;
        try
        {
            existing = ReadFile(recordPath);
        }
        catch (NotFoundException)
        {
            existing = null;
        }

        if (existing is not null && expectedEtag != existing.Etag)
        {
            throw new ConcurrencyConflictException(
                $"etag mismatch for id='{record.Id}': record was modified");
        }

        var stored = record.Clone();
        stored.Etag = NewEtag();
        stored.CreatedAt = existing?.CreatedAt ?? DateTimeOffset.UtcNow;
        stored.UpdatedAt = DateTimeOffset.UtcNow;
        WriteFile(recordPath, stored);
        return stored;
    }

    private void DeleteSync(string id, string partitionKey, string? expectedEtag)
    {
        var recordPath = RecordPath(id, partitionKey);
        using var _ = AcquireLock(recordPath);
        StoreRecord existing;
        try
        {
            existing = ReadFile(recordPath);
        }
        catch (NotFoundException)
        {
            throw new NotFoundException($"no record for id='{id}' in partition");
        }

        if (expectedEtag is not null && expectedEtag != existing.Etag)
        {
            throw new ConcurrencyConflictException(
                $"etag mismatch for id='{id}': record was modified");
        }

        File.Delete(recordPath);
    }

    private IReadOnlyList<StoreRecord> QuerySync(string partitionKey, string? typeFilter)
    {
        ValidateKey(partitionKey, "partition_key");
        var partitionDir = Path.Combine(_base, Hash(partitionKey));
        if (!Directory.Exists(partitionDir))
        {
            return Array.Empty<StoreRecord>();
        }

        var records = new List<StoreRecord>();
        var files = Directory.EnumerateFiles(partitionDir)
            .Where(path => Path.GetExtension(path) == ".json")
            .OrderBy(path => path, StringComparer.Ordinal);
        foreach (var path in files)
        {
            var record = ReadFile(path);
            if (typeFilter is null || record.Type == typeFilter)
            {
                records.Add(record);
            }
        }

        return records;
    }

    private static string NewEtag() => Guid.NewGuid().ToString("N");
}
