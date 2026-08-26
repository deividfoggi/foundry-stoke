using System.Diagnostics;
using Foundry.Stoke.Scheduling;

namespace Foundry.Stoke.Tests;

/// <summary>
/// Unit tests for the Clock abstraction (T015, ADR 0003). Verify the virtual
/// clock is deterministic and non-blocking and mirrors the Python semantics
/// (manual advance, auto-advance, monotonic now, total-delay accounting).
/// </summary>
public sealed class ClockTests
{
    [Fact]
    public async Task VirtualClock_ManualAdvance_ResolvesPendingDelay()
    {
        var clock = new VirtualClock();
        var delay = clock.DelayAsync(120);

        Assert.False(delay.IsCompleted);

        await clock.AdvanceAsync(120);
        await delay;

        Assert.Equal(120, clock.Now());
        Assert.Equal(120, clock.TotalDelay);
    }

    [Fact]
    public async Task VirtualClock_ManualAdvance_DoesNotResolveBeforeDue()
    {
        var clock = new VirtualClock();
        var delay = clock.DelayAsync(300);

        await clock.AdvanceAsync(120);

        Assert.False(delay.IsCompleted);
        Assert.Equal(120, clock.Now());
    }

    [Fact]
    public async Task VirtualClock_AutoAdvance_ReturnsImmediatelyAndMovesTime()
    {
        var clock = new VirtualClock(autoAdvance: true);

        await clock.DelayAsync(60);

        Assert.Equal(60, clock.Now());
        Assert.Equal(60, clock.TotalDelay);
    }

    [Fact]
    public async Task VirtualClock_NonPositiveDelay_ReturnsWithoutAdvancing()
    {
        var clock = new VirtualClock(start: 5);

        await clock.DelayAsync(0);

        Assert.Equal(5, clock.Now());
        Assert.Equal(0, clock.TotalDelay);
    }

    [Fact]
    public async Task SystemClock_Delay_IsNonBlockingAndMonotonic()
    {
        var clock = new SystemClock();
        var before = clock.Now();

        var stopwatch = Stopwatch.StartNew();
        await clock.DelayAsync(0.01);
        stopwatch.Stop();

        Assert.True(clock.Now() >= before);
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(5));
    }
}
