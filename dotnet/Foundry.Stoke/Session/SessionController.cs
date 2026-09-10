using System.Diagnostics;
using Foundry.Stoke.Errors;
using Foundry.Stoke.Observability;

namespace Foundry.Stoke.Session;

/// <summary>
/// Case-insensitive translator from a raw platform status to a
/// <see cref="SessionState"/>. Any unrecognized or future value maps to
/// <see cref="SessionState.Unknown"/>, never coerced to another state (FR-002,
/// CC-008). Inject a custom translator to override.
/// </summary>
public delegate SessionState StatusTranslator(string status);

/// <summary>
/// Control-plane session lifecycle over an <see cref="ISessionOperations"/> port
/// (US1, ADR 0002, contracts/session-controller.md). Mirror of the Python
/// <c>SessionController</c>.
///
/// The confirmed operations (create, get, list, stop, delete) are reached
/// through the port so the real REST adapter and a test fake are interchangeable.
/// "Resumed" is not a status nor an explicit operation: it is the effect of
/// referencing an idle session again. The controller derives it in
/// <see cref="GetSessionAsync"/> by remembering the last state observed per
/// session and setting <see cref="TrackedSession.ResumedAt"/> when a session
/// previously seen <c>idle</c> is now observed <c>active</c> (FR-003).
/// </summary>
public sealed class SessionController
{
    public const int MinIdleTimeoutSeconds = 300;
    public const int MaxIdleTimeoutSeconds = 3600;
    public const int DefaultIdleTimeoutSeconds = 900;

    private readonly ISessionOperations _ops;
    private readonly StatusTranslator _translate;

    // Sessions deleted in this controller's lifetime; subsequent operations on
    // them return SessionClosed deterministically (FR-005).
    private readonly HashSet<(string Agent, string Session)> _closed = new();

    // Last state observed per session; used to derive the resume marker
    // (idle -> active) since "resumed" is not a first-class status (FR-003).
    private readonly Dictionary<(string Agent, string Session), SessionState> _lastState = new();

    public SessionController(ISessionOperations operations, StatusTranslator? statusTranslator = null)
    {
        _ops = operations;
        _translate = statusTranslator ?? SessionStates.FromRaw;
    }

    public async Task<TrackedSession> CreateSessionAsync(
        string agentDefinitionId,
        int idleTimeoutSeconds = DefaultIdleTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        using var span = SessionTracing.Start("stoke.session.create");
        try
        {
            if (idleTimeoutSeconds < MinIdleTimeoutSeconds || idleTimeoutSeconds > MaxIdleTimeoutSeconds)
            {
                throw new InvalidIdleTimeoutException(
                    $"idleTimeoutSeconds must be within {MinIdleTimeoutSeconds}..{MaxIdleTimeoutSeconds} " +
                    $"(got {idleTimeoutSeconds})");
            }

            var raw = await _ops.CreateSessionAsync(agentDefinitionId, idleTimeoutSeconds, cancellationToken)
                .ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var state = _translate(raw.Status);
            _lastState[(agentDefinitionId, raw.AgentSessionId)] = state;
            var result = new TrackedSession(
                raw.AgentSessionId,
                agentDefinitionId,
                state,
                idleTimeoutSeconds,
                lastActivityAt: now,
                createdAt: now,
                origin: SessionOrigin.OnDemand);
            span?.SetStatus(ActivityStatusCode.Ok);
            return result;
        }
        catch
        {
            span?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
    }

    public async Task<TrackedSession> GetSessionAsync(
        string agentDefinitionId,
        string agentSessionId,
        int idleTimeoutSeconds = DefaultIdleTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        using var span = SessionTracing.Start("stoke.session.get", agentSessionId);
        try
        {
            EnsureOpen(agentDefinitionId, agentSessionId);
            var raw = await _ops.GetSessionAsync(agentDefinitionId, agentSessionId, cancellationToken)
                .ConfigureAwait(false);
            var state = _translate(raw.Status);
            var now = DateTimeOffset.UtcNow;
            var key = (agentDefinitionId, agentSessionId);

            // Derived resume: a session previously seen idle, now active again.
            var resumed = _lastState.TryGetValue(key, out var previous)
                && previous == SessionState.Idle
                && state == SessionState.Active;
            _lastState[key] = state;

            var result = new TrackedSession(
                raw.AgentSessionId,
                agentDefinitionId,
                state,
                idleTimeoutSeconds,
                lastActivityAt: now,
                resumedAt: resumed ? now : null);
            span?.SetStatus(ActivityStatusCode.Ok);
            return result;
        }
        catch
        {
            span?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
    }

    public async Task<IReadOnlyList<TrackedSession>> ListSessionsAsync(
        string agentDefinitionId,
        CancellationToken cancellationToken = default)
    {
        var raws = await _ops.ListSessionsAsync(agentDefinitionId, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        return raws
            .Select(raw => new TrackedSession(
                raw.AgentSessionId,
                agentDefinitionId,
                _translate(raw.Status),
                DefaultIdleTimeoutSeconds,
                lastActivityAt: now))
            .ToList();
    }

    public async Task StopSessionAsync(
        string agentDefinitionId, string agentSessionId, CancellationToken cancellationToken = default)
    {
        using var span = SessionTracing.Start("stoke.session.stop", agentSessionId);
        try
        {
            EnsureOpen(agentDefinitionId, agentSessionId);
            await _ops.StopSessionAsync(agentDefinitionId, agentSessionId, cancellationToken).ConfigureAwait(false);
            span?.SetStatus(ActivityStatusCode.Ok);
        }
        catch
        {
            span?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
    }

    public async Task DeleteSessionAsync(
        string agentDefinitionId, string agentSessionId, CancellationToken cancellationToken = default)
    {
        using var span = SessionTracing.Start("stoke.session.delete", agentSessionId);
        try
        {
            EnsureOpen(agentDefinitionId, agentSessionId);
            await _ops.DeleteSessionAsync(agentDefinitionId, agentSessionId, cancellationToken).ConfigureAwait(false);
            _closed.Add((agentDefinitionId, agentSessionId));
            span?.SetStatus(ActivityStatusCode.Ok);
        }
        catch
        {
            span?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
    }

    private void EnsureOpen(string agentDefinitionId, string agentSessionId)
    {
        if (_closed.Contains((agentDefinitionId, agentSessionId)))
        {
            throw new SessionClosedException("session has been deleted and no longer accepts operations");
        }
    }
}
