using System.Diagnostics;

namespace Foundry.Stoke.Scheduling;

/// <summary>
/// Real clock: monotonic <see cref="Now"/> and an async <see cref="DelayAsync"/>
/// backed by <see cref="Task.Delay(System.TimeSpan)"/>. Mirror of the Python
/// <c>SystemClock</c> (<c>time.monotonic</c> + <c>asyncio.sleep</c>).
/// </summary>
public sealed class SystemClock : IClock
{
    public double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public Task DelayAsync(double seconds) =>
        Task.Delay(TimeSpan.FromSeconds(Math.Max(0.0, seconds)));
}
