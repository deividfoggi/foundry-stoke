using System.Collections.Frozen;

namespace Foundry.Stoke.Store;

/// <summary>
/// Allowlist of record type discriminators recognized by Stoke (SEC-002).
/// Mirrors the Python KNOWN_RECORD_TYPES set. A provider must not return a
/// record whose type is outside this allowlist.
/// </summary>
public static class RecordTypes
{
    public const string TrackedSession = "tracked-session";
    public const string WarmPoolRegistry = "warm-pool-registry";

    public static readonly FrozenSet<string> Known =
        new[] { TrackedSession, WarmPoolRegistry }.ToFrozenSet();
}
