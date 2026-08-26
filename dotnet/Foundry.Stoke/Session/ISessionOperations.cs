namespace Foundry.Stoke.Session;

/// <summary>
/// Untranslated session view returned by the control-plane adapter. Mirror of
/// the Python <c>RawSession</c>. <see cref="Status"/> is the raw string as
/// returned by the platform; it is mapped to <see cref="SessionState"/> by an
/// injectable translator (case-insensitive over the official
/// <c>AgentSessionStatus</c> taxonomy).
/// </summary>
public sealed class RawSession
{
    public RawSession(string agentSessionId, string status)
    {
        AgentSessionId = agentSessionId;
        Status = status;
    }

    public string AgentSessionId { get; }

    public string Status { get; }
}

/// <summary>
/// Port for the confirmed Foundry <c>/sessions</c> control-plane operations
/// (ADR 0002, contracts/session-controller.md). Mirror of the Python
/// <c>SessionOperations</c> protocol. The real <c>azure-ai-projects</c> REST
/// adapter and a test fake are interchangeable behind this seam; the adapter is
/// out of this slice so the core stays free of any Azure SDK dependency (CC-004).
/// </summary>
public interface ISessionOperations
{
    Task<RawSession> CreateSessionAsync(
        string agentDefinitionId, int idleTimeoutSeconds, CancellationToken cancellationToken = default);

    Task<RawSession> GetSessionAsync(
        string agentDefinitionId, string agentSessionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RawSession>> ListSessionsAsync(
        string agentDefinitionId, CancellationToken cancellationToken = default);

    Task StopSessionAsync(
        string agentDefinitionId, string agentSessionId, CancellationToken cancellationToken = default);

    Task DeleteSessionAsync(
        string agentDefinitionId, string agentSessionId, CancellationToken cancellationToken = default);
}
