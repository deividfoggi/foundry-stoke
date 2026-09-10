using System.Net;
using Foundry.Stoke.Errors;
using Foundry.Stoke.Warmup;

namespace Foundry.Stoke.Tests;

/// <summary>
/// Unit tests for <see cref="ResponsesPingProbe"/> (US4, T061, SEC-010, ADR 0007).
/// Mirror of the Python probe tests: the endpoint is validated on construction,
/// a successful ping yields an ok result, a failing transport is reported via the
/// result (not raised), and no credential is ever attached to the outgoing
/// request (SEC-008: secrets never reach the probe).
/// </summary>
public sealed class ResponsesPingProbeTests
{
    [Fact]
    public void Constructor_Rejects_NonHttpsEndpoint()
    {
        using var client = new HttpClient(new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));

        Assert.Throws<InvalidEndpointException>(
            () => new ResponsesPingProbe(client, "http://example.com/probe"));
    }

    [Fact]
    public void Constructor_Rejects_HostMismatch()
    {
        using var client = new HttpClient(new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));

        Assert.Throws<InvalidEndpointException>(
            () => new ResponsesPingProbe(client, "https://evil.example.net/probe", expectedHost: "example.com"));
    }

    [Fact]
    public async Task ProbeAsync_ReturnsOk_OnSuccessfulPing()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHandler((request, _) =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = new HttpClient(handler);
        var probe = new ResponsesPingProbe(client, "https://example.com/probe");

        var result = await probe.ProbeAsync("agent-1", "sess-1");

        Assert.True(result.Ok);
        Assert.Null(result.Error);
        Assert.NotNull(captured);
        // SEC-008/SEC-010: the probe never attaches a credential to the request.
        Assert.Null(captured!.Headers.Authorization);
    }

    [Fact]
    public async Task ProbeAsync_ReturnsNotOk_OnTransportFailure()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("boom"));
        using var client = new HttpClient(handler);
        var probe = new ResponsesPingProbe(client, "https://example.com/probe");

        var result = await probe.ProbeAsync("agent-1", "sess-1");

        Assert.False(result.Ok);
        Assert.Equal(nameof(HttpRequestException), result.Error);
    }

    [Fact]
    public async Task ProbeAsync_ReturnsNotOk_OnErrorStatusCode()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var client = new HttpClient(handler);
        var probe = new ResponsesPingProbe(client, "https://example.com/probe");

        var result = await probe.ProbeAsync("agent-1", "sess-1");

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_respond(request, cancellationToken));
    }
}
