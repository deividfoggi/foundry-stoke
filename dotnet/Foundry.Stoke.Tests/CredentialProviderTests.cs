using Foundry.Stoke.Auth;
using Foundry.Stoke.Errors;

namespace Foundry.Stoke.Tests;

/// <summary>
/// Unit tests for the credential seam (US4, T058-T060/T063, SEC-004/005/008,
/// ADR 0005). Mirror of the Python credential tests. Covers the resolution
/// precedence (injected > primary > api-key > connection-string >
/// NoCredentialAvailable), the runtime-failover token probe, secret slots that
/// never leak through <see cref="object.ToString"/>, and <c>Clear()</c> to
/// minimize secret lifetime.
/// </summary>
public sealed class CredentialProviderTests
{
    private sealed class FakeCredential
    {
    }

    private static Func<object> Available(object credential) => () => credential;

    private static Func<object> Unavailable() =>
        () => throw new InvalidOperationException("primary unavailable");

    [Fact]
    public void ResolveCredential_PrefersInjected_OverEverything()
    {
        var injected = new FakeCredential();
        var provider = new CredentialProvider(
            credential: injected,
            environ: new Dictionary<string, string> { [CredentialProvider.ApiKeyEnv] = "sk-unused" },
            entraCredentialFactory: Available(new FakeCredential()));

        Assert.Same(injected, provider.ResolveCredential());
    }

    [Fact]
    public void ResolveCredential_ReturnsPrimary_WhenAvailable_OverFallback()
    {
        var primary = new FakeCredential();
        var provider = new CredentialProvider(
            environ: new Dictionary<string, string> { [CredentialProvider.ApiKeyEnv] = "sk-unused" },
            entraCredentialFactory: Available(primary));

        Assert.Same(primary, provider.ResolveCredential());
    }

    [Fact]
    public void ResolveCredential_FallsBackToApiKey_WhenPrimaryUnavailable()
    {
        var provider = new CredentialProvider(
            environ: new Dictionary<string, string> { [CredentialProvider.ApiKeyEnv] = "sk-secret" },
            entraCredentialFactory: Unavailable());

        var credential = Assert.IsType<ApiKeyCredential>(provider.ResolveCredential());
        Assert.Equal("sk-secret", credential.GetApiKey());
    }

    [Fact]
    public void ResolveCredential_PrefersApiKey_OverConnectionString()
    {
        var provider = new CredentialProvider(
            environ: new Dictionary<string, string>
            {
                [CredentialProvider.ApiKeyEnv] = "sk-secret",
                [CredentialProvider.ConnectionStringEnv] = "Endpoint=https://x;AccountKey=k",
            },
            entraCredentialFactory: Unavailable());

        Assert.IsType<ApiKeyCredential>(provider.ResolveCredential());
    }

    [Fact]
    public void ResolveCredential_FallsBackToConnectionString_WhenOnlyConnStringConfigured()
    {
        var provider = new CredentialProvider(
            environ: new Dictionary<string, string>
            {
                [CredentialProvider.ConnectionStringEnv] = "Endpoint=https://x;AccountKey=k",
            },
            entraCredentialFactory: Unavailable());

        Assert.IsType<ConnectionStringCredential>(provider.ResolveCredential());
    }

    [Fact]
    public void ResolveCredential_TokenProbeFailure_TriggersFallback()
    {
        var provider = new CredentialProvider(
            environ: new Dictionary<string, string> { [CredentialProvider.ApiKeyEnv] = "sk-secret" },
            entraCredentialFactory: Available(new FakeCredential()),
            tokenProbe: _ => throw new InvalidOperationException("token acquisition failed"));

        Assert.IsType<ApiKeyCredential>(provider.ResolveCredential());
    }

    [Fact]
    public void ResolveCredential_Throws_WhenNoCredentialAvailable()
    {
        var provider = new CredentialProvider(
            environ: new Dictionary<string, string>(),
            entraCredentialFactory: Unavailable());

        Assert.Throws<NoCredentialAvailableException>(() => provider.ResolveCredential());
    }

    [Fact]
    public void DefaultFactory_MarksPrimaryUnavailable_SoFallbackIsReached()
    {
        // The core has no Azure SDK (CC-004); the default factory throws, so
        // without an injected credential resolution falls through to the fallback.
        var provider = new CredentialProvider(
            environ: new Dictionary<string, string> { [CredentialProvider.ApiKeyEnv] = "sk-secret" });

        Assert.IsType<ApiKeyCredential>(provider.ResolveCredential());
    }

    [Fact]
    public void Provider_ToString_DoesNotExposeInjectedCredential()
    {
        var provider = new CredentialProvider(credential: new FakeCredential());

        Assert.DoesNotContain("FakeCredential", provider.ToString());
    }

    [Fact]
    public void ApiKeyCredential_ToString_DoesNotExposeSecret()
    {
        var credential = new ApiKeyCredential("sk-super-secret");

        Assert.DoesNotContain("sk-super-secret", credential.ToString());
        Assert.Equal("ApiKeyCredential(***)", credential.ToString());
    }

    [Fact]
    public void ApiKeyCredential_Clear_ZeroesSecret()
    {
        var credential = new ApiKeyCredential("sk-super-secret");

        credential.Clear();

        Assert.Equal(string.Empty, credential.GetApiKey());
    }

    [Fact]
    public void ConnectionStringCredential_ToString_DoesNotExposeSecret()
    {
        var credential = new ConnectionStringCredential("Endpoint=https://x;AccountKey=super-secret");

        Assert.DoesNotContain("super-secret", credential.ToString());
        Assert.Equal("ConnectionStringCredential(***)", credential.ToString());
    }

    [Fact]
    public void ConnectionStringCredential_Clear_ZeroesSecret()
    {
        var credential = new ConnectionStringCredential("Endpoint=https://x;AccountKey=super-secret");

        credential.Clear();

        Assert.Equal(string.Empty, credential.GetConnectionString());
    }
}
