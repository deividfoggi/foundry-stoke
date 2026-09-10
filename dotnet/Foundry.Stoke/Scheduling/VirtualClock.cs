namespace Foundry.Stoke.Scheduling;

/// <summary>
/// Deterministic clock for tests. Mirror of the Python <c>VirtualClock</c>.
///
/// With <c>autoAdvance = false</c> (default), <see cref="DelayAsync"/> blocks
/// until test code calls <see cref="AdvanceAsync"/>, which makes it possible to
/// assert exactly how many probes fire within an idle window. With
/// <c>autoAdvance = true</c>, <see cref="DelayAsync"/> advances virtual time
/// immediately and returns, convenient when driving a single reconcile cycle
/// whose internal backoff waits should not require an external driver. Neither
/// mode performs a real sleep.
/// </summary>
public sealed class VirtualClock : IClock
{
    private readonly bool _autoAdvance;
    private readonly object _gate = new();
    private readonly List<(double DueAt, TaskCompletionSource Waiter)> _waiters = new();
    private double _now;

    public VirtualClock(double start = 0.0, bool autoAdvance = false)
    {
        _now = start;
        _autoAdvance = autoAdvance;
    }

    /// <summary>Total virtual seconds requested through <see cref="DelayAsync"/>.</summary>
    public double TotalDelay { get; private set; }

    public double Now()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public async Task DelayAsync(double seconds)
    {
        if (seconds <= 0)
        {
            await Task.Yield();
            return;
        }

        TaskCompletionSource? waiter = null;
        lock (_gate)
        {
            TotalDelay += seconds;
            if (_autoAdvance)
            {
                _now += seconds;
            }
            else
            {
                waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((_now + seconds, waiter));
            }
        }

        if (waiter is null)
        {
            await Task.Yield();
            return;
        }

        await waiter.Task.ConfigureAwait(false);
    }

    /// <summary>Move virtual time forward, resolving every delay that comes due.</summary>
    public async Task AdvanceAsync(double seconds)
    {
        double target;
        lock (_gate)
        {
            target = _now + seconds;
        }

        while (true)
        {
            List<TaskCompletionSource> toResolve;
            lock (_gate)
            {
                var due = _waiters
                    .Where(w => !w.Waiter.Task.IsCompleted && w.DueAt <= target)
                    .ToList();
                if (due.Count == 0)
                {
                    break;
                }

                var earliest = due.Min(w => w.DueAt);
                _now = earliest;
                toResolve = _waiters
                    .Where(w => !w.Waiter.Task.IsCompleted && w.DueAt <= earliest)
                    .Select(w => w.Waiter)
                    .ToList();
            }

            foreach (var waiter in toResolve)
            {
                waiter.TrySetResult();
            }

            lock (_gate)
            {
                _waiters.RemoveAll(w => w.Waiter.Task.IsCompleted);
            }

            // Yield so coroutines whose delay just resolved can run and schedule
            // their next delay before we continue advancing.
            await Task.Yield();
        }

        lock (_gate)
        {
            _now = target;
        }
    }
}
