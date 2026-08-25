using System.Globalization;
using System.Text.Json.Nodes;

namespace Foundry.Stoke.Store;

/// <summary>
/// Generic unit persisted by any durable store provider (ADR 0001, data-model.md).
/// The (<see cref="Id"/>, <see cref="PartitionKey"/>) pair uniquely identifies a
/// record. <see cref="Etag"/> is an opaque optimistic-concurrency token assigned
/// by the provider on each successful write; callers treat it as opaque.
/// Field names map one-to-one to the Python <c>StoreRecord</c> (FR-022); the
/// JSON-safe representation uses snake_case keys for cross-language parity.
/// </summary>
public sealed class StoreRecord
{
    public StoreRecord(string id, string partitionKey, string type, JsonObject payload)
    {
        Id = id;
        PartitionKey = partitionKey;
        Type = type;
        Payload = payload;
        var now = DateTimeOffset.UtcNow;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public string Id { get; set; }

    public string PartitionKey { get; set; }

    public string Type { get; set; }

    public JsonObject Payload { get; set; }

    public string Etag { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Deep copy of this record, including its payload. Providers clone on the
    /// way in and out so stored state is never aliased by callers (mirrors the
    /// Python copy.deepcopy isolation in the in-memory provider).
    /// </summary>
    public StoreRecord Clone()
    {
        return new StoreRecord(Id, PartitionKey, Type, (JsonObject)Payload.DeepClone())
        {
            Etag = Etag,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
        };
    }

    /// <summary>Serialize to a JSON-safe object (timestamps as ISO-8601 strings).</summary>
    public JsonObject ToJsonObject()
    {
        return new JsonObject
        {
            ["id"] = Id,
            ["partition_key"] = PartitionKey,
            ["type"] = Type,
            ["payload"] = (JsonObject)Payload.DeepClone(),
            ["etag"] = Etag,
            ["created_at"] = CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            ["updated_at"] = UpdatedAt.ToString("O", CultureInfo.InvariantCulture),
        };
    }

    /// <summary>
    /// Reconstruct a record from a JSON-safe object. Throws on missing or
    /// malformed fields; the FileSystem provider (future slice) translates those
    /// into a typed CorruptedRecordException (SEC-002).
    /// </summary>
    public static StoreRecord FromJsonObject(JsonObject data)
    {
        var payload = data["payload"]?.AsObject() ?? new JsonObject();
        return new StoreRecord(
            (string)data["id"]!,
            (string)data["partition_key"]!,
            (string)data["type"]!,
            (JsonObject)payload.DeepClone())
        {
            Etag = (string)data["etag"]!,
            CreatedAt = DateTimeOffset.Parse(
                (string)data["created_at"]!,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind),
            UpdatedAt = DateTimeOffset.Parse(
                (string)data["updated_at"]!,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind),
        };
    }
}
