using Foundry.Stoke.Errors;

namespace Foundry.Stoke.Auth;

/// <summary>
/// Fallback credential wrapping an API key (SEC-005). Mirror of the Python
/// <c>ApiKeyCredential</c>. The secret is held in a single slot, never rendered
/// by <see cref="ToString"/>, and can be cleared to minimize its in-memory
/// lifetime.
/// </summary>
public sealed class ApiKeyCredential
{
    private string _apiKey;

    public ApiKeyCredential(string apiKey)
    {
        _apiKey = apiKey;
    }

    public string GetApiKey() => _apiKey;

    /// <summary>Zero out the secret to minimize its in-memory lifetime.</summary>
    public void Clear() => _apiKey = string.Empty;

    // SEC-005: never expose credential material.
    public override string ToString() => "ApiKeyCredential(***)";
}

/// <summary>
/// Fallback credential wrapping a connection string (SEC-005). Mirror of the
/// Python <c>ConnectionStringCredential</c>.
/// </summary>
public sealed class ConnectionStringCredential
{
    private string _connectionString;

    public ConnectionStringCredential(string connectionString)
    {
        _connectionString = connectionString;
    }

    public string GetConnectionString() => _connectionString;

    /// <summary>Zero out the secret to minimize its in-memory lifetime.</summary>
    public void Clear() => _connectionString = string.Empty;

    // SEC-005: never expose credential material.
    public override string ToString() => "ConnectionStringCredential(***)";
}

/// <summary>
/// Resolves the control-plane credential following the documented precedence
/// (US4, contracts/credential-provider.md, ADR 0005). Mirror of the Python
/// <c>CredentialProvider</c>, including the REV-001 resolution (factory seam +
/// token probe).
///
/// The core library never references an Azure SDK (CC-004, ADR 0005/0007).
/// Unlike Python, where <c>DefaultAzureCredential</c> is lazily imported, a .NET
/// type reference requires the assembly at compile time. The provider is
/// therefore seam-based:
/// <list type="bullet">
///   <item>An injected credential (highest precedence, SEC-004 deterministic
///   production behavior).</item>
///   <item>An <c>entraCredentialFactory</c> (<see cref="Func{TResult}"/> of
///   <see cref="object"/>): the primary (Entra ID) path. A factory that throws
///   marks the primary as unavailable. The default factory throws, deferring the
///   real <c>DefaultAzureCredential</c> to a future adapter package
///   (e.g. Foundry.Stoke.Azure), mirroring Python's optional <c>[azure]</c> extra.</item>
///   <item>An optional <c>tokenProbe</c> (<see cref="Action{T}"/> of
///   <see cref="object"/>): a runtime-failover hook that runs against the
///   constructed primary and, if it throws, treats the primary as unavailable.
///   Defaults to <see langword="null"/> so resolution stays non-blocking.</item>
///   <item>API-key / connection-string fallback resolved from the environment at
///   resolve time (SEC-005: minimized in-memory lifetime, never persisted).</item>
/// </list>
/// </summary>
public sealed class CredentialProvider : ICredentialProvider
{
    public const string ApiKeyEnv = "FOUNDRY_API_KEY";
    public const string ConnectionStringEnv = "FOUNDRY_CONNECTION_STRING";

    private readonly object? _injectedCredential;
    private readonly IReadOnlyDictionary<string, string>? _environ;
    private readonly Func<object> _entraCredentialFactory;
    private readonly Action<object>? _tokenProbe;

    /// <param name="credential">
    /// An explicit credential injected for deterministic production behavior or a
    /// fake in tests (SEC-004). Takes precedence over every other source.
    /// </param>
    /// <param name="environ">
    /// Overrides the source of fallback configuration for testability. Defaults to
    /// the process environment read at resolve time, so no secret is retained on
    /// this object (SEC-005).
    /// </param>
    /// <param name="entraCredentialFactory">
    /// Supplies the primary (Entra ID) credential. A factory that throws marks the
    /// primary as unavailable. The default throws, deferring the real
    /// <c>DefaultAzureCredential</c> to the Azure adapter package (CC-004).
    /// </param>
    /// <param name="tokenProbe">
    /// Optional runtime-failover hook: runs against the constructed primary and,
    /// if it throws, falls through to the fallback. Non-blocking by default.
    /// </param>
    public CredentialProvider(
        object? credential = null,
        IReadOnlyDictionary<string, string>? environ = null,
        Func<object>? entraCredentialFactory = null,
        Action<object>? tokenProbe = null)
    {
        _injectedCredential = credential;
        _environ = environ;
        _entraCredentialFactory = entraCredentialFactory ?? DefaultEntraCredentialFactory;
        _tokenProbe = tokenProbe;
    }

    public object ResolveCredential()
    {
        if (_injectedCredential is not null)
        {
            return _injectedCredential;
        }

        var primary = ResolvePrimary();
        if (primary is not null)
        {
            return primary;
        }

        var fallback = ResolveFallback();
        if (fallback is not null)
        {
            return fallback;
        }

        throw new NoCredentialAvailableException(
            "no credential available: reference the Azure adapter package for "
            + "DefaultAzureCredential, inject an explicit credential, or configure "
            + $"{ApiKeyEnv}/{ConnectionStringEnv}");
    }

    /// <summary>
    /// Return the primary credential, or <see langword="null"/> if it is
    /// unavailable. The primary is unavailable when the factory throws
    /// (construction failure, or a missing adapter package) or when the optional
    /// token probe rejects the constructed credential. Any such exception is
    /// treated as unavailability and never propagated, so resolution can fall
    /// through to the fallback.
    /// </summary>
    private object? ResolvePrimary()
    {
        object credential;
        try
        {
            credential = _entraCredentialFactory();
        }
        catch (Exception)
        {
            return null;
        }

        if (_tokenProbe is not null)
        {
            try
            {
                _tokenProbe(credential);
            }
            catch (Exception)
            {
                return null;
            }
        }

        return credential;
    }

    // SEC-005: read secrets at resolve time; never stored on the provider.
    private object? ResolveFallback()
    {
        var apiKey = ReadEnv(ApiKeyEnv);
        if (!string.IsNullOrEmpty(apiKey))
        {
            return new ApiKeyCredential(apiKey);
        }

        var connectionString = ReadEnv(ConnectionStringEnv);
        if (!string.IsNullOrEmpty(connectionString))
        {
            return new ConnectionStringCredential(connectionString);
        }

        return null;
    }

    private string? ReadEnv(string key) =>
        _environ is not null
            ? (_environ.TryGetValue(key, out var value) ? value : null)
            : Environment.GetEnvironmentVariable(key);

    // The Entra ID primary path requires an Azure SDK, which the core must not
    // reference (CC-004). Throwing marks the primary as unavailable so resolution
    // falls through to an injected credential or the configured fallback.
    // TODO(Foundry.Stoke.Azure): the adapter package supplies a factory building a
    // real DefaultAzureCredential, mirroring Python's optional [azure] extra.
    private static object DefaultEntraCredentialFactory() =>
        throw new NotSupportedException(
            "the Entra ID primary path requires the Foundry.Stoke.Azure adapter "
            + "package (DefaultAzureCredential); inject a credential or an "
            + "entraCredentialFactory, or configure a fallback");

    // SEC-005: never expose credential material in the string representation.
    public override string ToString() =>
        $"CredentialProvider(hasInjectedCredential={_injectedCredential is not null})";
}
