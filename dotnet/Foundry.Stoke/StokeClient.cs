using Foundry.Stoke.Auth;
using Foundry.Stoke.Errors;
using Foundry.Stoke.Session;
using Foundry.Stoke.Store;

namespace Foundry.Stoke;

/// <summary>Trusted configuration for the T017 composition entrypoint.</summary>
public sealed class StokeOptions
{
    public StokeOptions(string projectEndpoint)
    {
        ProjectEndpoint = projectEndpoint;
    }

    public string ProjectEndpoint { get; }

    public string? ExpectedHost { get; init; }

    public object? Credential { get; init; }

    public IDurableStoreProvider? Store { get; init; }
}

/// <summary>
/// Composes the existing providers and session controller (T017). The caller
/// supplies authenticated session operations; this core entrypoint creates no
/// network adapter and does not pass credentials to the store. Idle timeout is
/// configured per session through <see cref="SessionController.CreateSessionAsync"/>.
/// </summary>
public sealed class StokeClient
{
    public const string ProjectEndpointEnv = "FOUNDRY_PROJECT_ENDPOINT";
    public const string ExpectedHostEnv = "FOUNDRY_EXPECTED_HOST";

    public StokeClient(StokeOptions options, ISessionOperations operations)
        : this(options, operations, null)
    {
    }

    private StokeClient(
        StokeOptions options, ISessionOperations operations, IReadOnlyDictionary<string, string>? environ)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(operations);
        Endpoints.ValidateEndpoint(options.ProjectEndpoint, options.ExpectedHost);

        Options = options;
        CredentialProvider = new CredentialProvider(credential: options.Credential, environ: environ);
        Store = options.Store ?? new InMemoryStore();
        Sessions = new SessionController(operations);
    }

    public StokeOptions Options { get; }

    public ICredentialProvider CredentialProvider { get; }

    public IDurableStoreProvider Store { get; }

    public SessionController Sessions { get; }

    /// <summary>
    /// Reads trusted configuration from the process environment or an explicit
    /// replacement map, also used for credential fallback at resolution time.
    /// </summary>
    public static StokeClient FromEnvironment(
        ISessionOperations operations, IReadOnlyDictionary<string, string>? environ = null)
    {
        ArgumentNullException.ThrowIfNull(operations);
        var endpoint = ReadEnvironment(ProjectEndpointEnv);
        if (string.IsNullOrEmpty(endpoint))
        {
            throw new ConfigurationException($"{ProjectEndpointEnv} must be set");
        }

        var options = new StokeOptions(endpoint) { ExpectedHost = ReadEnvironment(ExpectedHostEnv) };
        return new StokeClient(options, operations, environ);

        string? ReadEnvironment(string key) => environ is not null
            ? (environ.TryGetValue(key, out var value) ? value : null)
            : Environment.GetEnvironmentVariable(key);
    }
}
