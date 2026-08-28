namespace Foundry.Stoke.Warmup;

/// <summary>
/// Adapter over a user-supplied probe callable (US4, SEC-010, ADR 0007). Mirror
/// of the Python <c>CallableProbe</c>. Used for Invocations/custom containers
/// whose schema is defined by the user; Stoke never attaches credentials when
/// invoking it and passes only the agent definition and session identifiers.
/// The built-in generic <see cref="ResponsesPingProbe"/> covers Responses-
/// compatible agents.
/// </summary>
public sealed class CallableProbe : IWarmupProbe
{
    private readonly Func<string, string, Task<ProbeResult>> _callback;

    public CallableProbe(Func<string, string, Task<ProbeResult>> callback)
    {
        _callback = callback;
    }

    public Task<ProbeResult> ProbeAsync(string agentDefinitionId, string agentSessionId) =>
        _callback(agentDefinitionId, agentSessionId);
}
