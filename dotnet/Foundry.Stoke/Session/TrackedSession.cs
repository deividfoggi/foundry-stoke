namespace Foundry.Stoke.Session;

/// <summary>
/// State of an agent session tracked by Stoke (US1, data-model.md). Mirror of
/// the Python <c>TrackedSession</c>. Field names map one-to-one to the Python
/// dataclass (FR-022).
///
/// A closed (deleted) session never accepts further operations without a
/// deterministic error (FR-005); that invariant is enforced by the
/// <see cref="SessionController"/>, not by this data holder.
/// </summary>
public sealed class TrackedSession
{
    public TrackedSession(
        string agentSessionId,
        string agentDefinitionId,
        SessionState state,
        int idleTimeoutSeconds,
        DateTimeOffset? lastActivityAt = null,
        DateTimeOffset? createdAt = null,
        SessionOrigin origin = SessionOrigin.OnDemand,
        DateTimeOffset? resumedAt = null)
    {
        var now = DateTimeOffset.UtcNow;
        AgentSessionId = agentSessionId;
        AgentDefinitionId = agentDefinitionId;
        State = state;
        IdleTimeoutSeconds = idleTimeoutSeconds;
        LastActivityAt = lastActivityAt ?? now;
        CreatedAt = createdAt ?? now;
        Origin = origin;
        ResumedAt = resumedAt;
    }

    public string AgentSessionId { get; }

    public string AgentDefinitionId { get; }

    public SessionState State { get; }

    public int IdleTimeoutSeconds { get; }

    public DateTimeOffset LastActivityAt { get; }

    public DateTimeOffset CreatedAt { get; }

    public SessionOrigin Origin { get; }

    /// <summary>Set when a previously idle session is observed active again (FR-003).</summary>
    public DateTimeOffset? ResumedAt { get; }
}
