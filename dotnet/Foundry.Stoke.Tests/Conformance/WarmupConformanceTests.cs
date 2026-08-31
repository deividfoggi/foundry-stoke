using System.Text.Json.Nodes;
using Foundry.Stoke.Errors;
using Foundry.Stoke.Scheduling;
using Foundry.Stoke.Session;
using Foundry.Stoke.Store;
using Foundry.Stoke.Warmup;

namespace Foundry.Stoke.Tests.Conformance;

/// <summary>
/// Cross-language conformance harness (US5, T069, FR-022, SC-001, ADR 0004),
/// warm-up domain. Reads the same language-neutral fixtures under
/// conformance/fixtures/ as the Python harness and drives the .NET warm-up
/// strategies against an <see cref="InMemoryStore"/>, a <see cref="VirtualClock"/>,
/// a scripted <see cref="ISessionOperations"/> fake, and a
/// <see cref="CallableProbe"/> to assert each expected observable outcome. It
/// stays thin on purpose: the scenarios live in the fixtures. Cases in domains
/// other than "warmup" are skipped here.
/// </summary>
[Trait("Category", "Conformance")]
public sealed class WarmupConformanceTests
{
    private static readonly string FixturesDir =
        Path.Combine(AppContext.BaseDirectory, "conformance", "fixtures");

    // Neutral error identifiers used in fixtures -> concrete Foundry.Stoke types.
    private static readonly IReadOnlyDictionary<string, Type> ErrorTypes = new Dictionary<string, Type>
    {
        ["TargetSizeExceeded"] = typeof(TargetSizeExceededException),
    };

    public static IEnumerable<object[]> WarmupCases()
    {
        foreach (var file in Directory.EnumerateFiles(FixturesDir, "*.json").OrderBy(path => path, StringComparer.Ordinal))
        {
            var suite = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            if ((string?)suite["domain"] != "warmup")
            {
                continue;
            }

            var suiteName = (string?)suite["suite"] ?? string.Empty;
            foreach (var caseNode in suite["cases"]!.AsArray())
            {
                var caseObj = caseNode!.AsObject();
                var caseId = (string?)caseObj["id"] ?? string.Empty;

                // Pass the case as a JSON string: xUnit theory data must be
                // serializable for stable test discovery. Re-parsed in the test.
                yield return new object[] { suiteName, caseId, caseObj.ToJsonString() };
            }
        }
    }

    [Theory]
    [MemberData(nameof(WarmupCases))]
    public async Task WarmupDomain(string suite, string caseId, string caseJson)
    {
        _ = suite;
        _ = caseId;
        var caseObj = JsonNode.Parse(caseJson)!.AsObject();
        var scenario = (string)caseObj["scenario"]!;

        switch (scenario)
        {
            case "pool_reconcile_refill":
                await PoolReconcileRefill(caseObj);
                break;
            case "pool_two_definitions":
                await PoolTwoDefinitions(caseObj);
                break;
            case "pool_evicts_terminal":
                await PoolEvictsTerminal(caseObj);
                break;
            case "pool_target_ceiling":
                PoolTargetCeiling(caseObj);
                break;
            case "keepalive_fires":
                await KeepaliveFires(caseObj);
                break;
            case "keepalive_user_probe":
                await KeepaliveUserProbe(caseObj);
                break;
            default:
                Assert.Fail($"unknown warmup scenario '{scenario}'");
                break;
        }
    }

    private static PreProvisionPoolStrategy MakePool(
        InMemoryStore store, string agentDefinitionId, int targetSize, int? maxTargetSize = null)
    {
        var controller = new SessionController(new FakeSessionOperations());
        return maxTargetSize is null
            ? new PreProvisionPoolStrategy(
                controller, store, agentDefinitionId, targetSize, new VirtualClock(autoAdvance: true))
            : new PreProvisionPoolStrategy(
                controller, store, agentDefinitionId, targetSize, new VirtualClock(autoAdvance: true),
                maxTargetSize: maxTargetSize.Value);
    }

    private static async Task PoolReconcileRefill(JsonObject caseObj)
    {
        var expect = caseObj["expect"]!.AsObject();
        var store = new InMemoryStore();
        var agent = (string)caseObj["agent_definition_id"]!;
        var pool = MakePool(store, agent, (int)caseObj["target_size"]!);

        var first = await pool.ReconcileAsync();
        Assert.Equal((int)expect["first_created"]!, first.Created);
        Assert.Equal((int)expect["first_ready"]!, first.Ready);

        for (var i = 0; i < (int)caseObj["consume"]!; i++)
        {
            Assert.NotNull(await pool.AcquireAsync());
        }

        var record = await store.ReadAsync(pool.RegistryId, agent);
        Assert.Equal(
            (int)expect["after_consume_ready"]!,
            record.Payload["tracked_session_ids"]!.AsArray().Count);

        var refill = await pool.ReconcileAsync();
        Assert.Equal((int)expect["refill_created"]!, refill.Created);
        Assert.Equal((int)expect["refill_ready"]!, refill.Ready);
    }

    private static async Task PoolTwoDefinitions(JsonObject caseObj)
    {
        var store = new InMemoryStore();
        var definitions = caseObj["definitions"]!.AsArray();
        var expects = caseObj["expect"]!.AsArray();
        for (var i = 0; i < definitions.Count; i++)
        {
            var definition = definitions[i]!.AsObject();
            var pool = MakePool(
                store, (string)definition["agent_definition_id"]!, (int)definition["target_size"]!);
            var report = await pool.ReconcileAsync();
            Assert.Equal((int)expects[i]!.AsObject()["ready"]!, report.Ready);
        }
    }

    private static async Task PoolEvictsTerminal(JsonObject caseObj)
    {
        var expect = caseObj["expect"]!.AsObject();
        var store = new InMemoryStore();
        var agent = (string)caseObj["agent_definition_id"]!;
        var controller = new SessionController(
            new FakeSessionOperations(getStatus: (string)caseObj["get_status"]!));
        var pool = new PreProvisionPoolStrategy(
            controller, store, agent, (int)caseObj["target_size"]!, new VirtualClock(autoAdvance: true));

        await pool.ReconcileAsync(); // seed the pool to target with active sessions
        var report = await pool.ReconcileAsync(); // existing sessions now report get_status
        Assert.Equal((int)expect["evicted"]!, report.Evicted);
        Assert.Equal((int)expect["created"]!, report.Created);
        Assert.Equal((int)expect["ready"]!, report.Ready);
    }

    private static void PoolTargetCeiling(JsonObject caseObj)
    {
        var error = (string)caseObj["expect"]!.AsObject()["error"]!;
        Assert.Throws(ErrorTypes[error], () =>
            MakePool(
                new InMemoryStore(),
                (string)caseObj["agent_definition_id"]!,
                (int)caseObj["target_size"]!,
                maxTargetSize: (int)caseObj["max_target_size"]!));
    }

    private static async Task KeepaliveFires(JsonObject caseObj)
    {
        var expect = caseObj["expect"]!.AsObject();
        var interval = (double)caseObj["interval_seconds"]!;

        // Semantic invariant: keepalive must fire strictly before the idle timeout.
        Assert.True(interval < (double)caseObj["idle_timeout_seconds"]!);

        var probe = new RecordingProbe();
        var clock = new VirtualClock();
        var sessionIds = caseObj["session_ids"]!.AsArray().Select(node => (string)node!).ToList();
        var strategy = new KeepaliveStrategy(
            probe, clock, interval, (string)caseObj["agent_definition_id"]!, sessionIds);

        await strategy.StartAsync();
        await PumpUntil(() => probe.Count >= (int)expect["before_first_interval"]!);
        Assert.Equal((int)expect["before_first_interval"]!, probe.Count);

        var perInterval = (int)expect["probed_per_interval"]!;
        var advanceIntervals = (int)caseObj["advance_intervals"]!;
        for (var elapsed = 1; elapsed <= advanceIntervals; elapsed++)
        {
            await clock.AdvanceAsync(interval);
            await PumpUntil(() => probe.Count >= perInterval * elapsed);
            Assert.Equal(perInterval * elapsed, probe.Count);
        }

        Assert.Equal((int)expect["total_after_advance"]!, probe.Count);
        await strategy.StopAsync();
    }

    private static async Task KeepaliveUserProbe(JsonObject caseObj)
    {
        var calls = new List<(string Agent, string Session)>();
        var probe = new CallableProbe((agent, session) =>
        {
            calls.Add((agent, session));
            return Task.FromResult(new ProbeResult(ok: true, latencySeconds: 0.0));
        });
        var sessionIds = caseObj["session_ids"]!.AsArray().Select(node => (string)node!).ToList();
        var strategy = new KeepaliveStrategy(
            probe, new VirtualClock(autoAdvance: true), 60, (string)caseObj["agent_definition_id"]!, sessionIds);

        var report = await strategy.ReconcileAsync();
        var expected = (int)caseObj["expect"]!.AsObject()["probed"]!;
        Assert.Equal(expected, report.Probed);
        Assert.Equal(expected, calls.Count);
    }

    // Deterministic pump for the manual-advance keepalive loop: yields until the
    // background reconcile continuations scheduled by a virtual-clock advance
    // have run. No real time elapses; the cap guards against a logic regression
    // never reaching the expected count.
    private static async Task PumpUntil(Func<bool> condition, int maxYields = 1000)
    {
        for (var i = 0; i < maxYields && !condition(); i++)
        {
            await Task.Yield();
        }
    }

    private sealed class RecordingProbe : IWarmupProbe
    {
        private readonly List<string> _probed = new();

        public int Count => _probed.Count;

        public Task<ProbeResult> ProbeAsync(string agentDefinitionId, string agentSessionId)
        {
            _probed.Add(agentSessionId);
            return Task.FromResult(new ProbeResult(ok: true, latencySeconds: 0.0));
        }
    }

    /// <summary>
    /// Control-plane fake that returns scripted raw statuses per get call. Mirror
    /// of the Python <c>_FakeSessionOperations</c>. When <c>getStatus</c> is set,
    /// every get returns that fixed status instead of consuming the script (used
    /// by the warm-pool eviction scenarios). Create always returns a fresh active
    /// session.
    /// </summary>
    private sealed class FakeSessionOperations : ISessionOperations
    {
        private readonly string? _getStatus;
        private int _counter;

        public FakeSessionOperations(string? getStatus = null)
        {
            _getStatus = getStatus;
        }

        public Task<RawSession> CreateSessionAsync(
            string agentDefinitionId, int idleTimeoutSeconds, CancellationToken cancellationToken = default)
        {
            _counter++;
            return Task.FromResult(new RawSession($"{agentDefinitionId}-sess-{_counter}", "active"));
        }

        public Task<RawSession> GetSessionAsync(
            string agentDefinitionId, string agentSessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RawSession(agentSessionId, _getStatus ?? "active"));

        public Task<IReadOnlyList<RawSession>> ListSessionsAsync(
            string agentDefinitionId, CancellationToken cancellationToken = default) =>
            Task.FromResult((IReadOnlyList<RawSession>)Array.Empty<RawSession>());

        public Task StopSessionAsync(
            string agentDefinitionId, string agentSessionId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteSessionAsync(
            string agentDefinitionId, string agentSessionId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
