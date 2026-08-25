using Foundry.Stoke.Errors;

namespace Foundry.Stoke.Store;

/// <summary>
/// Basic invariant validation for records returned by a pluggable provider
/// (SEC-008, ADR 0007). Store providers run in-process with full trust and are
/// not sandboxed, but Stoke does not trust returned records blindly: it rejects
/// records with an empty id/partitionKey or a type outside the allowlist,
/// surfacing a typed error instead of accepting malformed state. The
/// optimistic-concurrency guarantee still depends on the provider honoring the
/// etag; that responsibility is documented in the provider contract.
/// </summary>
public static class RecordInvariants
{
    public static StoreRecord Validate(StoreRecord record)
    {
        if (string.IsNullOrEmpty(record.Id) || string.IsNullOrEmpty(record.PartitionKey))
        {
            throw new InvalidRecordKeyException("record id and partitionKey must be non-empty");
        }

        if (!RecordTypes.Known.Contains(record.Type))
        {
            throw new UnknownRecordTypeException($"record type '{record.Type}' is not in the allowlist");
        }

        return record;
    }
}
