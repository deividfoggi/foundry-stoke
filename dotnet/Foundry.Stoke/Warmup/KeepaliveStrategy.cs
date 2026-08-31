using Foundry.Stoke.Observability;
using Foundry.Stoke.Scheduling;

namespace Foundry.Stoke.Warmup;

/// <summary>
/// Keepalive warm-up strategy (US3, T047, ADR 0003). Mirror of the Python
/// <c>KeepaliveStrategy</c>. Keeps referenced sessions from going idle by
/// invoking a <see cref="IWarmupProbe"/> before the idle timeout. The loop is
/// non-blocking and driven by an injected <see cref="IClock"/>; timing tests use
/// a <see cref="VirtualClock"/>. The probe is user-supplied (CC-007): Stoke
/// sends no application traffic of its own.
/// </summary>
public sealed class KeepaliveStrategy : IWarmupStrategy
{
    private readonly IWarmupProbe _probe;
    private readonly IClock _clock;
    private readonly double _interval;
    private readonly string _agentDefinitionId;
    private readonly List<string> _sessionIds;
    private readonly ITelemetry _telemetry;

    private bool _running;
    private Task? _loopTask;
    private CancellationTokenSource? _cts;

    public KeepaliveStrategy(
        IWarmupProbe probe,
        IClock clock,
        double intervalSeconds,
        string agentDefinitionId,
        IEnumerable<string> sessionIds,
        ITelemetry? telemetry = null)
    {
        _probe = probe;
        _clock = clock;
        _interval = intervalSeconds;
        _agentDefinitionId = agentDefinitionId;
        _sessionIds = new List<string>(sessionIds);
        _telemetry = telemetry ?? new Telemetry();
    }

    public async Task<WarmupReport> ReconcileAsync()
    {
        var probed = 0;
        var failures = 0;
        foreach (var sessionId in _sessionIds.ToList())
        {
            ProbeResult result;
            try
            {
                result = await _probe.ProbeAsync(_agentDefinitionId, sessionId).ConfigureAwait(false);
            }
            catch (Exception exc)
            {
                // A failing probe must not stop the loop.
                failures++;
                _telemetry.RecordException("stoke.warmup.probe", exc, _agentDefinitionId);
                continue;
            }

            probed++;
            if (!result.Ok)
            {
                failures++;
            }

            _telemetry.Emit(
                "stoke.warmup.probe",
                new Dictionary<string, object?>
                {
                    ["stoke.agent_definition_id"] = _agentDefinitionId,
                    ["stoke.agent_session_id"] = sessionId,
                    ["stoke.warmup.strategy"] = "keepalive",
                    ["stoke.probe.ok"] = result.Ok,
                });
        }

        return new WarmupReport(
            strategy: "keepalive",
            reconciledAt: _clock.Now(),
            probed: probed,
            failures: failures);
    }

    public Task StartAsync()
    {
        if (_running)
        {
            return Task.CompletedTask;
        }

        _running = true;
        _cts = new CancellationTokenSource();
        _loopTask = LoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _running = false;
        _cts?.Cancel();
        if (_loopTask is not null)
        {
            try
            {
                await _loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cooperative cancellation of the delay is expected.
            }

            _loopTask = null;
        }

        _cts?.Dispose();
        _cts = null;
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (_running)
        {
            try
            {
                await DelayOrCancel(_interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (!_running || cancellationToken.IsCancellationRequested)
            {
                break;
            }

            await ReconcileAsync().ConfigureAwait(false);
        }
    }

    // Awaits the injected clock's delay but stays cancellable: a VirtualClock
    // delay that is never advanced would otherwise leave the loop parked past
    // StopAsync. Racing the delay against the token lets the loop exit promptly.
    private async Task DelayOrCancel(double seconds, CancellationToken cancellationToken)
    {
        var delay = _clock.DelayAsync(seconds);
        var cancelSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => cancelSignal.TrySetResult());
        var completed = await Task.WhenAny(delay, cancelSignal.Task).ConfigureAwait(false);
        if (completed == cancelSignal.Task)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        await delay.ConfigureAwait(false);
    }
}
