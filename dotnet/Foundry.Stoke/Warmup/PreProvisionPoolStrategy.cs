using System.Globalization;
using System.Text.Json.Nodes;
using Foundry.Stoke.Errors;
using Foundry.Stoke.Observability;
using Foundry.Stoke.Scheduling;
using Foundry.Stoke.Session;
using Foundry.Stoke.Store;

namespace Foundry.Stoke.Warmup;

/// <summary>
/// Pre-provision pool warm-up strategy (US3, T048/T049, SEC-007, ADR 0003).
/// Mirror of the Python <c>PreProvisionPoolStrategy</c>. Keeps N ready sessions
/// per agent definition, reconciling to the target size via the
/// <see cref="SessionController"/> (create/get) with no data-plane protocol.
/// Warm-pool state is persisted through the durable store as a
/// <c>warm-pool-registry</c> record.
///
/// SEC-007: the target size has a configurable, validated ceiling; reconcile
/// failures use exponential backoff with full jitter and a retry ceiling to
/// avoid tight loops when the service is unavailable; a
/// <c>stoke.warmup.refill</c> metric is emitted each cycle.
/// </summary>
public sealed class PreProvisionPoolStrategy : IWarmupStrategy
{
    public const int DefaultMaxTargetSize = 100;

    private readonly SessionController _controller;
    private readonly IDurableStoreProvider _store;
    private readonly string _agentDefinitionId;
    private readonly int _targetSize;
    private readonly IClock _clock;
    private readonly double _interval;
    private readonly int _maxRetries;
    private readonly double _baseBackoff;
    private readonly double _maxBackoff;
    private readonly ITelemetry _telemetry;
    private readonly Random _rng;
    private readonly string _registryId;

    private bool _running;
    private Task? _loopTask;
    private CancellationTokenSource? _cts;

    public PreProvisionPoolStrategy(
        SessionController controller,
        IDurableStoreProvider store,
        string agentDefinitionId,
        int targetSize,
        IClock clock,
        double refillIntervalSeconds = 60.0,
        int maxTargetSize = DefaultMaxTargetSize,
        int maxRetries = 5,
        double baseBackoffSeconds = 1.0,
        double maxBackoffSeconds = 60.0,
        ITelemetry? telemetry = null,
        Random? rng = null)
    {
        if (targetSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetSize), "target_size must be >= 0");
        }

        if (targetSize > maxTargetSize)
        {
            throw new TargetSizeExceededException(
                $"target_size {targetSize} exceeds the maximum {maxTargetSize} (SEC-007)");
        }

        _controller = controller;
        _store = store;
        _agentDefinitionId = agentDefinitionId;
        _targetSize = targetSize;
        _clock = clock;
        _interval = refillIntervalSeconds;
        _maxRetries = maxRetries;
        _baseBackoff = baseBackoffSeconds;
        _maxBackoff = maxBackoffSeconds;
        _telemetry = telemetry ?? new Telemetry();
        _rng = rng ?? new Random();
        _registryId = $"warm-pool:{agentDefinitionId}";
    }

    public string RegistryId => _registryId;

    public async Task<WarmupReport> ReconcileAsync()
    {
        var (etag, registry) = await LoadRegistryAsync().ConfigureAwait(false);
        var (ready, evicted) = await FilterReadyAsync(registry.TrackedSessionIds).ConfigureAwait(false);
        var created = 0;
        var failures = 0;
        var attempt = 0;
        while (ready.Count < _targetSize)
        {
            TrackedSession session;
            try
            {
                session = await _controller.CreateSessionAsync(_agentDefinitionId).ConfigureAwait(false);
            }
            catch (Exception exc)
            {
                // Transient unavailability: retry with backoff up to the ceiling.
                failures++;
                attempt++;
                _telemetry.RecordException("stoke.warmup.refill", exc, _agentDefinitionId);
                if (attempt > _maxRetries)
                {
                    break;
                }

                await _clock.DelayAsync(Backoff(attempt)).ConfigureAwait(false);
                continue;
            }

            ready.Add(session.AgentSessionId);
            created++;
            attempt = 0;
        }

        registry.TrackedSessionIds = ready;
        registry.TargetSize = _targetSize;
        registry.LastReconciledAt = DateTimeOffset.UtcNow;
        await SaveRegistryAsync(etag, registry).ConfigureAwait(false);

        _telemetry.Emit(
            "stoke.warmup.refill",
            new Dictionary<string, object?>
            {
                ["stoke.agent_definition_id"] = _agentDefinitionId,
                ["stoke.warmup.strategy"] = "pre-provision-pool",
                ["stoke.warmup.target_size"] = _targetSize,
                ["stoke.warmup.ready"] = ready.Count,
                ["stoke.warmup.created"] = created,
                ["stoke.warmup.evicted"] = evicted,
                ["stoke.warmup.failures"] = failures,
            });

        return new WarmupReport(
            strategy: "pre-provision-pool",
            reconciledAt: _clock.Now(),
            ready: ready.Count,
            created: created,
            failures: failures,
            evicted: evicted);
    }

    /// <summary>Take a ready session from the pool, persisting the reduced registry.</summary>
    public async Task<string?> AcquireAsync()
    {
        var (etag, registry) = await LoadRegistryAsync().ConfigureAwait(false);
        if (registry.TrackedSessionIds.Count == 0)
        {
            return null;
        }

        var sessionId = registry.TrackedSessionIds[0];
        registry.TrackedSessionIds.RemoveAt(0);
        await SaveRegistryAsync(etag, registry).ConfigureAwait(false);
        return sessionId;
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

    // Keep only sessions still ready; evict terminal/unknown ones (data-model).
    // Terminal states (FAILED, EXPIRED, DELETED, DELETING) and UNKNOWN are never
    // counted toward the target: they are dropped so the refill loop replaces
    // them. IDLE stays (a reprovision/keepalive candidate). A session that can no
    // longer be queried is treated as not ready.
    private async Task<(List<string> Ready, int Evicted)> FilterReadyAsync(List<string> sessionIds)
    {
        var ready = new List<string>();
        var evicted = 0;
        foreach (var sessionId in sessionIds)
        {
            TrackedSession session;
            try
            {
                session = await _controller.GetSessionAsync(_agentDefinitionId, sessionId).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // An unqueryable session is not ready.
                evicted++;
                continue;
            }

            if (SessionStates.Terminal.Contains(session.State) || session.State == SessionState.Unknown)
            {
                evicted++;
                continue;
            }

            ready.Add(sessionId);
        }

        return (ready, evicted);
    }

    private double Backoff(int attempt)
    {
        var raw = Math.Min(_maxBackoff, _baseBackoff * Math.Pow(2, attempt - 1));
        return _rng.NextDouble() * raw; // full jitter
    }

    private async Task<(string? Etag, WarmPoolRegistry Registry)> LoadRegistryAsync()
    {
        StoreRecord record;
        try
        {
            record = await _store.ReadAsync(_registryId, _agentDefinitionId).ConfigureAwait(false);
        }
        catch (NotFoundException)
        {
            return (null, new WarmPoolRegistry(
                _agentDefinitionId, _targetSize, WarmupStrategyKind.PreProvisionPool));
        }

        RecordInvariants.Validate(record); // SEC-008: never trust returned records blindly
        return (record.Etag, RegistryFromPayload(record.Payload));
    }

    private async Task SaveRegistryAsync(string? etag, WarmPoolRegistry registry)
    {
        var record = new StoreRecord(
            _registryId,
            _agentDefinitionId,
            RecordTypes.WarmPoolRegistry,
            RegistryToPayload(registry));

        if (etag is null)
        {
            try
            {
                await _store.CreateAsync(record).ConfigureAwait(false);
                return;
            }
            catch (AlreadyExistsException)
            {
                etag = (await _store.ReadAsync(_registryId, _agentDefinitionId).ConfigureAwait(false)).Etag;
            }
        }

        await _store.UpsertAsync(record, etag).ConfigureAwait(false);
    }

    private static JsonObject RegistryToPayload(WarmPoolRegistry registry)
    {
        var ids = new JsonArray();
        foreach (var id in registry.TrackedSessionIds)
        {
            ids.Add(id);
        }

        return new JsonObject
        {
            ["agent_definition_id"] = registry.AgentDefinitionId,
            ["target_size"] = registry.TargetSize,
            ["strategy"] = registry.Strategy.ToWireValue(),
            ["tracked_session_ids"] = ids,
            ["last_reconciled_at"] = registry.LastReconciledAt.ToString("O", CultureInfo.InvariantCulture),
        };
    }

    private static WarmPoolRegistry RegistryFromPayload(JsonObject payload)
    {
        var ids = payload["tracked_session_ids"]?.AsArray().Select(node => (string)node!).ToList()
            ?? new List<string>();
        return new WarmPoolRegistry(
            (string)payload["agent_definition_id"]!,
            (int)payload["target_size"]!,
            WarmupStrategyKinds.FromWireValue((string)payload["strategy"]!),
            ids,
            DateTimeOffset.Parse(
                (string)payload["last_reconciled_at"]!,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind));
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

    // Awaits the injected clock's delay but stays cancellable so StopAsync can
    // exit a loop parked on a VirtualClock delay that is never advanced.
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
