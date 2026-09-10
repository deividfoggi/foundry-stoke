namespace Foundry.Stoke.Warmup;

/// <summary>
/// Outcome of a single reconciliation cycle (US3, ADR 0003,
/// contracts/warmup-strategy.md). Mirror of the Python <c>WarmupReport</c>.
/// Fields not relevant to a given strategy stay at zero (e.g. a keepalive cycle
/// reports <see cref="Probed"/> but not <see cref="Created"/>).
/// </summary>
public sealed class WarmupReport
{
    public WarmupReport(
        string strategy,
        double reconciledAt,
        int ready = 0,
        int created = 0,
        int probed = 0,
        int failures = 0,
        int evicted = 0)
    {
        Strategy = strategy;
        ReconciledAt = reconciledAt;
        Ready = ready;
        Created = created;
        Probed = probed;
        Failures = failures;
        Evicted = evicted;
    }

    public string Strategy { get; }

    public double ReconciledAt { get; }

    public int Ready { get; }

    public int Created { get; }

    public int Probed { get; }

    public int Failures { get; }

    public int Evicted { get; }
}
