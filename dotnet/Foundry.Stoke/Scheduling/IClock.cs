namespace Foundry.Stoke.Scheduling;

/// <summary>
/// Injectable time source used by the warm-up schedulers (ADR 0003,
/// contracts/clock-scheduler.md). Mirror of the Python <c>Clock</c> protocol.
///
/// Hard constraint (non-blocking): <see cref="DelayAsync"/> MUST be awaitable
/// and never block a thread. The production clock is built on
/// <see cref="System.Threading.Tasks.Task.Delay(System.TimeSpan)"/> and a
/// monotonic time source; the virtual clock resolves scheduled delays when test
/// code advances virtual time, exercising idle windows (minutes) instantly and
/// deterministically.
/// </summary>
public interface IClock
{
    /// <summary>Return a monotonic timestamp in seconds.</summary>
    double Now();

    /// <summary>Wait <paramref name="seconds"/> without ever blocking a thread.</summary>
    Task DelayAsync(double seconds);
}
