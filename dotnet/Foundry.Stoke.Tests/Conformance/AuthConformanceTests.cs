using System.Text.Json.Nodes;
using Foundry.Stoke.Auth;
using Foundry.Stoke.Errors;

namespace Foundry.Stoke.Tests.Conformance;

/// <summary>
/// Cross-language conformance harness (US5, T069, FR-022, SC-001, ADR 0004),
/// auth domain. Reads the same language-neutral fixtures under
/// conformance/fixtures/ as the Python harness and drives the .NET
/// <see cref="CredentialProvider"/> with fakes to assert each expected
/// observable outcome (CC-005). It stays thin on purpose: the scenarios live in
/// the fixtures. Cases in domains other than "auth" are skipped here.
///
/// Neutral fixture concepts map to the credential seam: <c>primary_available</c>
/// drives the factory (a throwing factory models real unavailability, not a
/// missing package); <c>probe_fails</c> opts into runtime failover via a token
/// probe that throws; <c>env</c> supplies fallback configuration; <c>injected</c>
/// supplies an explicit credential.
/// </summary>
[Trait("Category", "Conformance")]
public sealed class AuthConformanceTests
{
    private static readonly string FixturesDir =
        Path.Combine(AppContext.BaseDirectory, "conformance", "fixtures");

    // Neutral error identifiers used in fixtures -> concrete Foundry.Stoke types.
    private static readonly IReadOnlyDictionary<string, Type> ErrorTypes = new Dictionary<string, Type>
    {
        ["NoCredentialAvailable"] = typeof(NoCredentialAvailableException),
    };

    public static IEnumerable<object[]> AuthCases()
    {
        foreach (var file in Directory.EnumerateFiles(FixturesDir, "*.json").OrderBy(path => path, StringComparer.Ordinal))
        {
            var suite = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            if ((string?)suite["domain"] != "auth")
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
    [MemberData(nameof(AuthCases))]
    public void AuthDomain(string suite, string caseId, string caseJson)
    {
        _ = suite;
        _ = caseId;
        var caseObj = JsonNode.Parse(caseJson)!.AsObject();
        Assert.Equal("resolve", (string?)caseObj["scenario"]);

        var expect = caseObj["expect"]!.AsObject();
        var env = ReadEnv(caseObj);

        // A throwing factory models real unavailability (not a missing package);
        // a returning factory models an available primary.
        var primaryAvailable = (bool?)caseObj["primary_available"] ?? false;
        var primary = new FakePrimaryCredential();
        Func<object> factory = primaryAvailable
            ? () => primary
            : () => throw new InvalidOperationException("primary unavailable");

        Action<object>? tokenProbe = null;
        if ((bool?)caseObj["probe_fails"] == true)
        {
            tokenProbe = _ => throw new InvalidOperationException("token acquisition failed");
        }

        object? injected = (bool?)caseObj["injected"] == true ? new FakeInjectedCredential() : null;

        var provider = new CredentialProvider(
            credential: injected,
            environ: env,
            entraCredentialFactory: factory,
            tokenProbe: tokenProbe);

        if (expect["error"] is not null)
        {
            var expectedType = ErrorTypes[(string)expect["error"]!];
            var ex = Assert.ThrowsAny<Exception>(() => provider.ResolveCredential());
            Assert.IsType(expectedType, ex);
            return;
        }

        var credential = provider.ResolveCredential();
        AssertCredentialKind(credential, (string)expect["credential_kind"]!, injected, primary, env);
    }

    private static Dictionary<string, string> ReadEnv(JsonObject caseObj)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (caseObj["env"] is JsonObject envObj)
        {
            foreach (var pair in envObj)
            {
                if (pair.Value is not null)
                {
                    env[pair.Key] = (string)pair.Value!;
                }
            }
        }

        return env;
    }

    private static void AssertCredentialKind(
        object credential,
        string kind,
        object? injected,
        FakePrimaryCredential primary,
        IReadOnlyDictionary<string, string> env)
    {
        switch (kind)
        {
            case "injected":
                Assert.Same(injected, credential);
                break;
            case "primary":
                Assert.Same(primary, credential);
                break;
            case "api_key":
                Assert.IsType<ApiKeyCredential>(credential);
                AssertNoSecretLeak(credential, env);
                break;
            case "connection_string":
                Assert.IsType<ConnectionStringCredential>(credential);
                AssertNoSecretLeak(credential, env);
                break;
            default:
                Assert.Fail($"unknown credential_kind '{kind}'");
                break;
        }
    }

    // SEC-005: the string representation must never expose credential material.
    private static void AssertNoSecretLeak(object credential, IReadOnlyDictionary<string, string> env)
    {
        var rendered = credential.ToString() ?? string.Empty;
        foreach (var value in env.Values)
        {
            Assert.DoesNotContain(value, rendered);
        }
    }

    private sealed class FakeInjectedCredential
    {
    }

    /// <summary>Stands in for the Entra ID primary path when the factory succeeds.</summary>
    private sealed class FakePrimaryCredential
    {
    }
}
