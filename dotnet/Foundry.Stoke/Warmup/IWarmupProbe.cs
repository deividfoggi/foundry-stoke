namespace Foundry.Stoke.Warmup;

/// <summary>
/// Outcome of a single probe call (US4, ADR 0003, warmup-probe.md). Mirror of
/// the Python <c>ProbeResult</c>.
/// </summary>
public sealed class ProbeResult
{
    public ProbeResult(bool ok, double latencySeconds, string? error = null)
    {
        Ok = ok;
        LatencySeconds = latencySeconds;
        Error = error;
    }

    public bool Ok { get; }

    public double LatencySeconds { get; }

    public string? Error { get; }
}

/// <summary>
/// Port for keepalive probes (US4, ADR 0003/0007, warmup-probe.md). Mirror of
/// the Python <c>WarmupProbe</c> protocol. A probe generates minimal activity
/// within the idle window to keep a session usable, without Stoke embedding a
/// general-purpose data-plane client (ADR 0002).
/// </summary>
public interface IWarmupProbe
{
    /// <summary>Run a minimal activity against the session to renew its idle timer.</summary>
    Task<ProbeResult> ProbeAsync(string agentDefinitionId, string agentSessionId);
}
