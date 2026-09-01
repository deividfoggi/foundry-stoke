using Foundry.Stoke.Observability;

namespace Foundry.Stoke.Tests;

/// <summary>
/// Telemetry redaction tests (T013/T065/T067, SEC-003/SEC-009, ADR 0006).
/// Mirror of the Python test_telemetry_redaction: the guarantee is fail-safe, so
/// only allowlisted attributes are emitted and no secret-shaped value crosses the
/// telemetry boundary.
/// </summary>
public sealed class TelemetryTests
{
    [Fact]
    public void Allowlist_Drops_NonAllowlisted_And_SecretShaped_Attributes()
    {
        var attributes = new Dictionary<string, object?>
        {
            ["stoke.agent_definition_id"] = "agent-a",
            ["stoke.connection_string"] = "AccountKey=abc123==;",
            ["api_key"] = "sk-secret",
            ["authorization"] = "Bearer token-value",
        };

        var result = Redaction.RedactAttributes(attributes);

        Assert.Single(result);
        Assert.Equal("agent-a", result["stoke.agent_definition_id"]);
    }

    [Fact]
    public void SessionId_Hashed_At_Info_And_Retained_At_Error()
    {
        var info = Redaction.RedactAttributes(
            new Dictionary<string, object?> { ["stoke.agent_session_id"] = "sess-123" }, "info");
        var infoValue = (string)info["stoke.agent_session_id"]!;
        Assert.NotEqual("sess-123", infoValue);
        Assert.DoesNotContain("sess-123", infoValue);
        Assert.StartsWith("sha256:", infoValue);

        var error = Redaction.RedactAttributes(
            new Dictionary<string, object?> { ["stoke.agent_session_id"] = "sess-123" }, "error");
        Assert.Equal("sess-123", error["stoke.agent_session_id"]);
    }

    [Fact]
    public void HashSessionId_Matches_Python_Sha256_Scheme()
    {
        // sha256("sess-123") hex, first 12 chars, "sha256:" prefixed.
        Assert.Equal("sha256:c8d9cf2851b3", Redaction.HashSessionId("sess-123"));
    }

    [Fact]
    public void SanitizeExceptionMessage_Removes_Secret_Patterns()
    {
        var message = "connect failed: Endpoint=https://x;AccountKey=SECRETKEY==; token=abcd1234";
        var cleaned = Redaction.SanitizeExceptionMessage(message);
        Assert.DoesNotContain("SECRETKEY", cleaned);
        Assert.DoesNotContain("abcd1234", cleaned);
    }

    [Fact]
    public void Emit_Never_Leaks_Secret_Patterns_To_Sink()
    {
        var events = new List<TelemetryEvent>();
        var telemetry = new Telemetry(events.Add);

        telemetry.Emit(
            "stoke.warmup.refill",
            new Dictionary<string, object?>
            {
                ["stoke.agent_definition_id"] = "agent-a",
                ["stoke.agent_session_id"] = "sess-xyz",
                ["stoke.connection_string"] = "AccountKey=leak==;",
                ["api_key"] = "sk-leak",
            });

        Assert.NotEmpty(events);
        foreach (var value in events[0].Attributes.Values)
        {
            var text = value?.ToString() ?? string.Empty;
            Assert.DoesNotContain("AccountKey", text);
            Assert.DoesNotContain("sk-leak", text);
            Assert.DoesNotContain("sess-xyz", text); // handle hashed at info level
        }
    }

    [Fact]
    public void RecordException_Message_Is_Sanitized()
    {
        var events = new List<TelemetryEvent>();
        var telemetry = new Telemetry(events.Add);

        telemetry.RecordException(
            "stoke.warmup.refill",
            new InvalidOperationException("boom AccountKey=SECRET=="),
            agentDefinitionId: "agent-a");

        Assert.NotEmpty(events);
        var message = (string)events[0].Attributes["exception.message"]!;
        Assert.DoesNotContain("SECRET", message);
    }

    [Fact]
    public void NoSink_Emit_Is_Safe_NoOp()
    {
        var telemetry = new Telemetry();
        var exception = Record.Exception(() =>
            telemetry.Emit("stoke.warmup.refill", new Dictionary<string, object?>()));
        Assert.Null(exception);
    }
}
