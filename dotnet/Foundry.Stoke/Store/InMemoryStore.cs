using Foundry.Stoke.Errors;

namespace Foundry.Stoke.Store;

/// <summary>
/// Reference <see cref="IDurableStoreProvider"/> backed by an in-memory index
/// (US2 tracer bullet). For dev and tests only: it does not persist across
/// processes. Implements optimistic concurrency by etag. Mirrors the Python
/// InMemoryStore, including deep-copy isolation of stored records.
/// </summary>
public sealed class InMemoryStore : IDurableStoreProvider
{
    private readonly Dictionary<(string PartitionKey, string Id), StoreRecord> _records = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<StoreRecord> CreateAsync(StoreRecord record, CancellationToken cancellationToken = default)
    {
        var key = (record.PartitionKey, record.Id);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_records.ContainsKey(key))
            {
                throw new AlreadyExistsException($"record already exists for id='{record.Id}' in partition");
            }

            var stored = record.Clone();
            stored.Etag = NewEtag();
            var now = DateTimeOffset.UtcNow;
            stored.CreatedAt = now;
            stored.UpdatedAt = now;
            _records[key] = stored;
            return stored.Clone();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<StoreRecord> ReadAsync(string id, string partitionKey, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_records.TryGetValue((partitionKey, id), out var existing))
            {
                throw new NotFoundException($"no record for id='{id}' in partition");
            }

            return existing.Clone();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<StoreRecord> UpsertAsync(StoreRecord record, string? expectedEtag, CancellationToken cancellationToken = default)
    {
        var key = (record.PartitionKey, record.Id);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _records.TryGetValue(key, out var existing);
            if (existing is not null && expectedEtag != existing.Etag)
            {
                throw new ConcurrencyConflictException($"etag mismatch for id='{record.Id}': record was modified");
            }

            var stored = record.Clone();
            stored.Etag = NewEtag();
            var now = DateTimeOffset.UtcNow;
            stored.CreatedAt = existing?.CreatedAt ?? now;
            stored.UpdatedAt = now;
            _records[key] = stored;
            return stored.Clone();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteAsync(string id, string partitionKey, string? expectedEtag = null, CancellationToken cancellationToken = default)
    {
        var key = (partitionKey, id);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_records.TryGetValue(key, out var existing))
            {
                throw new NotFoundException($"no record for id='{id}' in partition");
            }

            if (expectedEtag is not null && expectedEtag != existing.Etag)
            {
                throw new ConcurrencyConflictException($"etag mismatch for id='{id}': record was modified");
            }

            _records.Remove(key);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<StoreRecord>> QueryByPartitionAsync(string partitionKey, string? typeFilter = null, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _records
                .Where(entry => entry.Key.PartitionKey == partitionKey
                    && (typeFilter is null || entry.Value.Type == typeFilter))
                .Select(entry => entry.Value.Clone())
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    private static string NewEtag() => Guid.NewGuid().ToString("N");
}
