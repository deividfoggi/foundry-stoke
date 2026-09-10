using System.Text.Json.Nodes;
using Foundry.Stoke.Errors;
using Foundry.Stoke.Session;

namespace Foundry.Stoke.Tests.Conformance;

/// <summary>
/// Cross-language conformance harness (US5, T069, FR-022, SC-001, ADR 0004),
/// session domain. Reads the same language-neutral fixtures under
/// conformance/fixtures/ as the Python harness and drives the .NET
/// <see cref="SessionController"/> against a scripted <see cref="ISessionOperations"/>
/// fake to assert each expected observable outcome. It stays thin on purpose:
/// the scenarios live in the fixtures. Cases in domains other than "session" are
/// skipped here.
/// </summary>
[Trait("Category", "Conformance")]
public sealed class SessionConformanceTests
{
    private static readonly string FixturesDir =
        Path.Combine(AppContext.BaseDirectory, "conformance", "fixtures");

    // Neutral error identifiers used in fixtures -> concrete Foundry.Stoke types.
    private static readonly IReadOnlyDictionary<string, Type> ErrorTypes = new Dictionary<string, Type>
    {
        ["InvalidIdleTimeout"] = typeof(InvalidIdleTimeoutException),
        ["SessionClosed"] = typeof(SessionClosedException),
    };

    public static IEnumerable<object[]> SessionCases()
    {
        foreach (var file in Directory.EnumerateFiles(FixturesDir, "*.json").OrderBy(path => path, StringComparer.Ordinal))
        {
            var suite = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            if ((string?)suite["domain"] != "session")
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
    [MemberData(nameof(SessionCases))]
    public async Task SessionDomain(string suite, string caseId, string caseJson)
    {
        _ = suite;
        _ = caseId;
        var caseObj = JsonNode.Parse(caseJson)!.AsObject();
        var agent = (string)caseObj["agent_definition_id"]!;
        var script = caseObj["get_status_script"]?.AsArray().Select(node => (string)node!).ToList()
            ?? new List<string>();
        var controller = new SessionController(new FakeSessionOperations(script));
        string? sessionId = null;

        foreach (var stepNode in caseObj["steps"]!.AsArray())
        {
            var step = stepNode!.AsObject();
            var op = (string)step["op"]!;
            var expect = step["expect"]?.AsObject() ?? new JsonObject();

            switch (op)
            {
                case "create":
                    {
                        var timeout = (int)step["idle_timeout_seconds"]!;
                        var created = await ExpectSession(expect, () => controller.CreateSessionAsync(agent, timeout));
                        if (created is not null)
                        {
                            sessionId = created.AgentSessionId;
                            if ((bool?)expect["has_session_id"] == true)
                            {
                                Assert.False(string.IsNullOrEmpty(sessionId));
                            }

                            AssertState(created, expect);
                        }

                        break;
                    }

                case "get":
                    {
                        Assert.NotNull(sessionId);
                        var got = await ExpectSession(expect, () => controller.GetSessionAsync(agent, sessionId!));
                        if (got is not null)
                        {
                            AssertState(got, expect);
                        }

                        break;
                    }

                case "stop":
                    Assert.NotNull(sessionId);
                    await ExpectVoid(expect, () => controller.StopSessionAsync(agent, sessionId!));
                    break;

                case "delete":
                    Assert.NotNull(sessionId);
                    await ExpectVoid(expect, () => controller.DeleteSessionAsync(agent, sessionId!));
                    break;

                default:
                    Assert.Fail($"unknown session op '{op}'");
                    break;
            }
        }
    }

    private static async Task<TrackedSession?> ExpectSession(JsonObject expect, Func<Task<TrackedSession>> action)
    {
        var error = (string?)expect["error"];
        if (error is not null)
        {
            await Assert.ThrowsAsync(ErrorTypes[error], () => action());
            return null;
        }

        return await action();
    }

    private static async Task ExpectVoid(JsonObject expect, Func<Task> action)
    {
        var error = (string?)expect["error"];
        if (error is not null)
        {
            await Assert.ThrowsAsync(ErrorTypes[error], action);
        }
        else
        {
            await action();
        }
    }

    private static void AssertState(TrackedSession session, JsonObject expect)
    {
        if (expect["state"] is JsonNode stateNode)
        {
            Assert.Equal((string)stateNode!, session.State.ToWireValue());
        }

        if (expect["resumed"] is JsonNode resumedNode)
        {
            Assert.Equal((bool)resumedNode!, session.ResumedAt is not null);
        }
    }

    /// <summary>
    /// Control-plane fake that returns scripted raw statuses per get call. Mirror
    /// of the Python <c>_FakeSessionOperations</c>. When <c>getStatus</c> is set,
    /// every get returns that fixed status instead of consuming the script (used
    /// by the warm-pool eviction scenarios; unused in the session fixtures).
    /// </summary>
    private sealed class FakeSessionOperations : ISessionOperations
    {
        private readonly Queue<string> _getStatuses;
        private readonly string? _getStatus;
        private int _counter;

        public FakeSessionOperations(IEnumerable<string> getStatusScript, string? getStatus = null)
        {
            _getStatuses = new Queue<string>(getStatusScript);
            _getStatus = getStatus;
        }

        public Task<RawSession> CreateSessionAsync(
            string agentDefinitionId, int idleTimeoutSeconds, CancellationToken cancellationToken = default)
        {
            _counter++;
            return Task.FromResult(new RawSession($"{agentDefinitionId}-sess-{_counter}", "active"));
        }

        public Task<RawSession> GetSessionAsync(
            string agentDefinitionId, string agentSessionId, CancellationToken cancellationToken = default)
        {
            var status = _getStatus ?? (_getStatuses.Count > 0 ? _getStatuses.Dequeue() : "active");
            return Task.FromResult(new RawSession(agentSessionId, status));
        }

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
