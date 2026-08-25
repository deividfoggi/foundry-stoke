using System.Text.Json;
using System.Text.Json.Nodes;
using Foundry.Stoke.Errors;
using Foundry.Stoke.Store;

namespace Foundry.Stoke.Tests.Conformance;

/// <summary>
/// Cross-language conformance harness (US5, T069, FR-022, SC-001, ADR 0004),
/// store domain only in this slice. Reads the same language-neutral fixtures
/// under conformance/fixtures/ as the Python harness and drives the .NET public
/// surface (Foundry.Stoke) to assert each expected observable outcome. It stays
/// thin on purpose: the scenarios live in the fixtures. Cases in domains other
/// than "store" are skipped here and picked up by later slices.
/// </summary>
[Trait("Category", "Conformance")]
public sealed class StoreConformanceTests
{
    private static readonly string FixturesDir =
        Path.Combine(AppContext.BaseDirectory, "conformance", "fixtures");

    // Neutral error identifiers used in fixtures -> concrete Foundry.Stoke types.
    private static readonly IReadOnlyDictionary<string, Type> ErrorTypes = new Dictionary<string, Type>
    {
        ["AlreadyExists"] = typeof(AlreadyExistsException),
        ["NotFound"] = typeof(NotFoundException),
        ["ConcurrencyConflict"] = typeof(ConcurrencyConflictException),
        ["InvalidRecordKey"] = typeof(InvalidRecordKeyException),
        ["UnknownRecordType"] = typeof(UnknownRecordTypeException),
    };

    public static IEnumerable<object[]> StoreCases()
    {
        foreach (var file in Directory.EnumerateFiles(FixturesDir, "*.json").OrderBy(path => path, StringComparer.Ordinal))
        {
            var suite = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            if ((string?)suite["domain"] != "store")
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
    [MemberData(nameof(StoreCases))]
    public async Task StoreDomain(string suite, string caseId, string caseJson)
    {
        _ = suite;
        _ = caseId;
        var caseObj = JsonNode.Parse(caseJson)!.AsObject();
        var store = new InMemoryStore();
        var etags = new Dictionary<string, string>();

        foreach (var stepNode in caseObj["steps"]!.AsArray())
        {
            await ExecuteStep(store, stepNode!.AsObject(), etags);
        }
    }

    private static async Task ExecuteStep(InMemoryStore store, JsonObject step, Dictionary<string, string> etags)
    {
        var op = (string)step["op"]!;
        var expect = step["expect"]?.AsObject() ?? new JsonObject();

        switch (op)
        {
            case "create":
                {
                    var result = await ExpectRecord(expect, () => store.CreateAsync(ToRecord(step["record"]!.AsObject())));
                    BindEtag(result, step, expect, etags);
                    break;
                }

            case "read":
                {
                    var result = await ExpectRecord(expect, () => store.ReadAsync((string)step["id"]!, (string)step["partition_key"]!));
                    if (result is not null && expect["payload"] is JsonObject expectedPayload)
                    {
                        Assert.True(
                            JsonNode.DeepEquals(result.Payload, expectedPayload),
                            $"payload mismatch: expected {expectedPayload.ToJsonString()}, got {result.Payload.ToJsonString()}");
                    }

                    break;
                }

            case "upsert":
                {
                    string? expectedEtag = null;
                    if (step["expected_etag_from"] is JsonNode from)
                    {
                        expectedEtag = etags[(string)from!];
                    }

                    var result = await ExpectRecord(expect, () => store.UpsertAsync(ToRecord(step["record"]!.AsObject()), expectedEtag));
                    BindEtag(result, step, expect, etags);
                    break;
                }

            case "query":
                {
                    var typeFilter = (string?)step["type_filter"];
                    var result = await store.QueryByPartitionAsync((string)step["partition_key"]!, typeFilter);
                    Assert.Equal((int)expect["count"]!, result.Count);
                    break;
                }

            case "validate_record":
                {
                    var record = ToRecord(step["record"]!.AsObject());
                    ExpectSync(expect, () => RecordInvariants.Validate(record));
                    break;
                }

            default:
                Assert.Fail($"unknown store op '{op}'");
                break;
        }
    }

    private static async Task<StoreRecord?> ExpectRecord(JsonObject expect, Func<Task<StoreRecord>> action)
    {
        var error = (string?)expect["error"];
        if (error is not null)
        {
            await Assert.ThrowsAsync(ErrorTypes[error], () => action());
            return null;
        }

        return await action();
    }

    private static void ExpectSync(JsonObject expect, Action action)
    {
        var error = (string?)expect["error"];
        if (error is not null)
        {
            Assert.Throws(ErrorTypes[error], action);
        }
        else
        {
            action();
        }
    }

    private static void BindEtag(StoreRecord? result, JsonObject step, JsonObject expect, Dictionary<string, string> etags)
    {
        if (result is null)
        {
            return;
        }

        if ((bool?)expect["etag_present"] == true)
        {
            Assert.False(string.IsNullOrEmpty(result.Etag));
        }

        if (step["bind"] is JsonNode bind)
        {
            etags[(string)bind!] = result.Etag;
        }
    }

    private static StoreRecord ToRecord(JsonObject data)
    {
        var payload = data["payload"]?.AsObject() ?? new JsonObject();
        return new StoreRecord(
            (string)data["id"]!,
            (string)data["partition_key"]!,
            (string)data["type"]!,
            (JsonObject)payload.DeepClone());
    }
}
