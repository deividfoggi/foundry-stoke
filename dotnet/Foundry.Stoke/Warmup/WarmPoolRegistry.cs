namespace Foundry.Stoke.Warmup;

/// <summary>
/// Warm-pool state per agent definition (US3, data-model.md). Mirror of the
/// Python <c>WarmPoolRegistry</c>. Persisted through the durable store as a
/// <c>warm-pool-registry</c> record; the strategy reconciles the tracked
/// sessions toward <see cref="TargetSize"/>.
/// </summary>
public sealed class WarmPoolRegistry
{
    public WarmPoolRegistry(
        string agentDefinitionId,
        int targetSize,
        WarmupStrategyKind strategy,
        IEnumerable<string>? trackedSessionIds = null,
        DateTimeOffset? lastReconciledAt = null)
    {
        AgentDefinitionId = agentDefinitionId;
        TargetSize = targetSize;
        Strategy = strategy;
        TrackedSessionIds = trackedSessionIds is null ? new List<string>() : new List<string>(trackedSessionIds);
        LastReconciledAt = lastReconciledAt ?? DateTimeOffset.UtcNow;
    }

    public string AgentDefinitionId { get; }

    public int TargetSize { get; set; }

    public WarmupStrategyKind Strategy { get; }

    public List<string> TrackedSessionIds { get; set; }

    public DateTimeOffset LastReconciledAt { get; set; }
}
