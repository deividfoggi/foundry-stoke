using Foundry.Stoke.Errors;

namespace Foundry.Stoke;

/// <summary>
/// Endpoint validation shared by the config facade and the warm-up probe
/// (SEC-010, ADR 0007). Mirror of the Python <c>endpoints.validate_endpoint</c>.
///
/// Endpoints used to reach Foundry (the project endpoint and the probe target)
/// come exclusively from trusted configuration and are validated before use
/// (https scheme and, when known, the expected host). This limits the SSRF
/// surface: a keepalive ping carrying a token must never be redirected to an
/// arbitrary endpoint.
/// </summary>
public static class Endpoints
{
    /// <summary>
    /// Return <paramref name="endpoint"/> unchanged if it is a trusted https URL.
    /// </summary>
    /// <exception cref="InvalidEndpointException">
    /// Raised when the endpoint is not an absolute URL, its scheme is not https,
    /// the host is missing, or (when provided) the host differs from
    /// <paramref name="expectedHost"/>.
    /// </exception>
    public static string ValidateEndpoint(string endpoint, string? expectedHost = null)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var parsed))
        {
            throw new InvalidEndpointException(
                $"endpoint is not a valid absolute URL (got '{endpoint}')");
        }

        if (!string.Equals(parsed.Scheme, "https", StringComparison.Ordinal))
        {
            throw new InvalidEndpointException(
                $"endpoint must use https (got scheme '{parsed.Scheme}')");
        }

        if (string.IsNullOrEmpty(parsed.Host))
        {
            throw new InvalidEndpointException("endpoint must include a host");
        }

        if (expectedHost is not null && !string.Equals(parsed.Host, expectedHost, StringComparison.Ordinal))
        {
            throw new InvalidEndpointException(
                $"endpoint host '{parsed.Host}' does not match expected host '{expectedHost}'");
        }

        return endpoint;
    }
}
