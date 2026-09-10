using System.Text.Json.Nodes;
using Foundry.Stoke.Observability;

namespace Foundry.Stoke.Tests.Conformance;

/// <summary>
/// Cross-language conformance harness (US5, T069, FR-022, SC-001, ADR 0004),
/// telemetry domain. Reads the same language-neutral fixtures under
/// conformance/fixtures/ as the Python harness and drives the .NET
/// <see cref="Redaction"/> policy to assert each expected observable outcome
/// (SEC-003/SEC-009, ADR 0006). Cases in domains other than "telemetry" are
/// skipped here.
///
/// The <c>redact</c> scenario calls <see cref="Redaction.RedactAttributes"/> and
/// checks that allowlisted keys are present, secret-shaped keys are dropped, and
/// the session id is hashed at info level or kept plaintext at error level. The
/// <c>sanitize_message</c> scenario calls
/// <see cref="Redaction.SanitizeExceptionMessage"/> and asserts the secret-shaped
/// substrings are gone.
/// </summary>
[Trait("Category", "Conformance")]
public sealed class TelemetryConformanceTests
{
    private static readonly string FixturesDir =
        Path.Combine(AppContext.BaseDirectory, "conformance", "fixtures");

    public static IEnumerable<object[]> TelemetryCases()
    {
        foreach (var file in Directory.EnumerateFiles(FixturesDir, "*.json").OrderBy(path => path, StringComparer.Ordinal))
        {
            var suite = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            if ((string?)suite["domain"] != "telemetry")
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
    [MemberData(nameof(TelemetryCases))]
    public void TelemetryDomain(string suite, string caseId, string caseJson)
    {
        _ = suite;
        _ = caseId;
        var caseObj = JsonNode.Parse(caseJson)!.AsObject();
        var scenario = (string?)caseObj["scenario"];
        var expect = caseObj["expect"]!.AsObject();

        switch (scenario)
        {
            case "redact":
                AssertRedact(caseObj, expect);
                break;
            case "sanitize_message":
                AssertSanitizeMessage(caseObj, expect);
                break;
            default:
                Assert.Fail($"unknown telemetry scenario '{scenario}'");
                break;
        }
    }

    private static void AssertRedact(JsonObject caseObj, JsonObject expect)
    {
        var level = (string?)caseObj["level"] ?? "info";
        var attributes = ReadAttributes(caseObj["attributes"]!.AsObject());
        var result = Redaction.RedactAttributes(attributes, level);

        if (expect["present"] is JsonArray present)
        {
            foreach (var key in present)
            {
                Assert.Contains((string)key!, result.Keys);
            }
        }

        if (expect["absent"] is JsonArray absent)
        {
            foreach (var key in absent)
            {
                Assert.DoesNotContain((string)key!, result.Keys);
            }
        }

        if (expect["session_id"] is not null)
        {
            var emitted = (string?)result[Redaction.SensitiveSessionIdAttribute];
            var original = (string?)caseObj["attributes"]!.AsObject()[Redaction.SensitiveSessionIdAttribute];
            if ((string)expect["session_id"]! == "hashed")
            {
                Assert.NotNull(emitted);
                Assert.StartsWith("sha256:", emitted);
                Assert.NotEqual(original, emitted);
            }
            else
            {
                Assert.Equal(original, emitted);
            }
        }
    }

    private static void AssertSanitizeMessage(JsonObject caseObj, JsonObject expect)
    {
        var sanitized = Redaction.SanitizeExceptionMessage((string)caseObj["message"]!);
        foreach (var substring in expect["absent_substrings"]!.AsArray())
        {
            Assert.DoesNotContain((string)substring!, sanitized);
        }
    }

    private static Dictionary<string, object?> ReadAttributes(JsonObject attributesObj)
    {
        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in attributesObj)
        {
            attributes[pair.Key] = (string?)pair.Value;
        }

        return attributes;
    }
}
