namespace Foundry.Stoke.Warmup;

/// <summary>
/// User-selectable warm-up strategy (US3, ADR 0003,
/// contracts/warmup-strategy.md). Mirror of the Python <c>WarmupStrategy</c>
/// protocol. A strategy runs a non-blocking reconciliation loop driven by an
/// injected <see cref="Foundry.Stoke.Scheduling.IClock"/>;
/// <see cref="ReconcileAsync"/> performs a single cycle and is directly
/// testable, while <see cref="StartAsync"/>/<see cref="StopAsync"/> manage the
/// background loop.
/// </summary>
public interface IWarmupStrategy
{
    /// <summary>Run one reconciliation cycle and return its report.</summary>
    Task<WarmupReport> ReconcileAsync();

    /// <summary>Start the non-blocking reconciliation loop.</summary>
    Task StartAsync();

    /// <summary>Stop the loop cooperatively.</summary>
    Task StopAsync();
}
