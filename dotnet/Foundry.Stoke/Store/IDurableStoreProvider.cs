namespace Foundry.Stoke.Store;

/// <summary>
/// Pluggable persistence port for Stoke's own control-plane state (ADR 0001,
/// contracts/durable-store-provider.md). Technology-agnostic: the core must not
/// depend on any concrete store SDK (FR-011, CC-004). Reference providers
/// (InMemory, FileSystem) and production stores implement the same interface
/// without changing callers (FR-009).
///
/// Semantics: minimal CRUD plus query-by-partition, with optimistic concurrency
/// by etag. Methods are asynchronous so I/O-backed providers stay non-blocking;
/// the in-memory provider satisfies the same async surface trivially.
/// </summary>
public interface IDurableStoreProvider
{
    /// <summary>
    /// Create a new record. Throws AlreadyExistsException if (id, partitionKey)
    /// already exists. Returns the record with its initial etag assigned.
    /// </summary>
    Task<StoreRecord> CreateAsync(StoreRecord record, CancellationToken cancellationToken = default);

    /// <summary>Read by composite key. Throws NotFoundException if absent.</summary>
    Task<StoreRecord> ReadAsync(string id, string partitionKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Create or update a record. If the record exists, <paramref name="expectedEtag"/>
    /// must match the current etag or a ConcurrencyConflictException is raised
    /// (CC-003). <paramref name="expectedEtag"/> is only valid as null when
    /// creating a new record. Returns the record with a new etag.
    /// </summary>
    Task<StoreRecord> UpsertAsync(StoreRecord record, string? expectedEtag, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete by composite key. Throws NotFoundException if absent, or
    /// ConcurrencyConflictException if <paramref name="expectedEtag"/> is
    /// provided and stale.
    /// </summary>
    Task DeleteAsync(string id, string partitionKey, string? expectedEtag = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// List records in a partition, optionally filtered by type. This is
    /// deliberately not an arbitrary query language, to avoid coupling to a
    /// backend (ADR 0001).
    /// </summary>
    Task<IReadOnlyList<StoreRecord>> QueryByPartitionAsync(string partitionKey, string? typeFilter = null, CancellationToken cancellationToken = default);
}
