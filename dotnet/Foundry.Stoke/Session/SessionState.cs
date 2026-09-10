using System.Collections.Frozen;

namespace Foundry.Stoke.Session;

/// <summary>
/// Compute state of a tracked session, reflected by Stoke (data-model.md).
/// Mirror of the Python <c>SessionState</c>: the eight official
/// <c>AgentSessionStatus</c> values plus an <see cref="Unknown"/> fallback.
///
/// A translator maps the raw platform status to this enum case-insensitively at
/// runtime (see <see cref="SessionStates.FromRaw"/>); any unrecognized or future
/// value maps to <see cref="Unknown"/>, never coerced to another state (FR-002,
/// CC-008). "Resumed" is not a status: it is the derived <c>idle</c> -&gt;
/// <c>active</c> transition surfaced via <see cref="TrackedSession.ResumedAt"/>
/// (FR-003).
///
/// Source: https://learn.microsoft.com/en-us/javascript/api/@azure/ai-projects/agentsessionstatus
/// </summary>
public enum SessionState
{
    Creating,
    Active,
    Idle,
    Updating,
    Failed,
    Deleting,
    Deleted,
    Expired,
    Unknown,
}

/// <summary>
/// Wire-value mapping and taxonomy helpers for <see cref="SessionState"/>. The
/// wire values are the lowercase official <c>AgentSessionStatus</c> strings,
/// matching the Python enum values one-to-one for cross-language parity (FR-022).
/// </summary>
public static class SessionStates
{
    private static readonly IReadOnlyDictionary<SessionState, string> WireValues =
        new Dictionary<SessionState, string>
        {
            [SessionState.Creating] = "creating",
            [SessionState.Active] = "active",
            [SessionState.Idle] = "idle",
            [SessionState.Updating] = "updating",
            [SessionState.Failed] = "failed",
            [SessionState.Deleting] = "deleting",
            [SessionState.Deleted] = "deleted",
            [SessionState.Expired] = "expired",
            [SessionState.Unknown] = "unknown",
        };

    // Case-insensitive lookup over the official values only; Unknown is the
    // fallback, never a translation target for a recognized value.
    private static readonly IReadOnlyDictionary<string, SessionState> ByWireValue =
        WireValues
            .Where(pair => pair.Key != SessionState.Unknown)
            .ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

    /// <summary>
    /// Terminal states: a session in one of these is gone for good and MUST be
    /// evicted from the warm pool (never counted as ready). Mirror of the Python
    /// <c>TERMINAL_SESSION_STATES</c>.
    /// </summary>
    public static readonly FrozenSet<SessionState> Terminal = new[]
    {
        SessionState.Failed,
        SessionState.Deleting,
        SessionState.Deleted,
        SessionState.Expired,
    }.ToFrozenSet();

    /// <summary>The lowercase official status string for this state.</summary>
    public static string ToWireValue(this SessionState state) => WireValues[state];

    /// <summary>
    /// Map a raw platform status to <see cref="SessionState"/> case-insensitively
    /// over the official taxonomy. Any unrecognized or future value maps to
    /// <see cref="SessionState.Unknown"/> (FR-002, CC-008). Mirror of the Python
    /// <c>default_status_translator</c>.
    /// </summary>
    public static SessionState FromRaw(string status) =>
        ByWireValue.TryGetValue(status.Trim().ToLowerInvariant(), out var state)
            ? state
            : SessionState.Unknown;
}

/// <summary>
/// Whether a session was born from the warm pool or created on demand. Mirror of
/// the Python <c>SessionOrigin</c>.
/// </summary>
public enum SessionOrigin
{
    Pool,
    OnDemand,
}
