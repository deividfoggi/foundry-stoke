using Foundry.Stoke;
using Foundry.Stoke.Errors;

namespace Foundry.Stoke.Tests;

/// <summary>
/// Unit tests for <see cref="Endpoints.ValidateEndpoint"/> (SEC-010). Mirror of
/// the Python endpoint validation tests: https is mandatory and, when known, the
/// host must match. This limits the SSRF surface for a keepalive ping.
/// </summary>
public sealed class EndpointsTests
{
    [Fact]
    public void ValidateEndpoint_ReturnsEndpoint_WhenHttpsAndHostMatch()
    {
        const string endpoint = "https://example.services.ai.azure.com/api/projects/p";

        var result = Endpoints.ValidateEndpoint(endpoint, expectedHost: "example.services.ai.azure.com");

        Assert.Equal(endpoint, result);
    }

    [Fact]
    public void ValidateEndpoint_AllowsHttps_WhenNoExpectedHost()
    {
        const string endpoint = "https://example.com/probe";

        var result = Endpoints.ValidateEndpoint(endpoint);

        Assert.Equal(endpoint, result);
    }

    [Fact]
    public void ValidateEndpoint_Rejects_NonHttpsScheme()
    {
        Assert.Throws<InvalidEndpointException>(
            () => Endpoints.ValidateEndpoint("http://example.com/probe"));
    }

    [Fact]
    public void ValidateEndpoint_Rejects_HostMismatch()
    {
        Assert.Throws<InvalidEndpointException>(
            () => Endpoints.ValidateEndpoint("https://evil.example.net/probe", expectedHost: "example.com"));
    }

    [Fact]
    public void ValidateEndpoint_Rejects_NonAbsoluteUrl()
    {
        Assert.Throws<InvalidEndpointException>(
            () => Endpoints.ValidateEndpoint("example.com/probe"));
    }
}
