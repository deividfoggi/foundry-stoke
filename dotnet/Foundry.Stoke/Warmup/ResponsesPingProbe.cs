using System.Text;
using System.Text.Json.Nodes;

namespace Foundry.Stoke.Warmup;

/// <summary>
/// Built-in generic Responses ping probe (optional; US4, ADR 0003/0007,
/// warmup-probe.md). Mirror of the Python <c>ResponsesPingProbe</c>.
///
/// Applicable only to Responses-compatible agents; Invocations/custom containers
/// require a user-supplied <see cref="CallableProbe"/>. The target endpoint is
/// validated (https + expected host) and taken only from trusted configuration
/// (SEC-010) before any use. The ping carries no credentials: the transport is a
/// plain BCL <see cref="HttpClient"/> whose authentication (if any) is the
/// caller's responsibility, never attached here (SEC-010, ADR 0007).
/// </summary>
public sealed class ResponsesPingProbe : IWarmupProbe
{
    private readonly HttpClient _httpClient;
    private readonly string _endpoint;

    /// <param name="httpClient">
    /// BCL transport used to reach the endpoint. Injected so tests can supply a
    /// fake handler; the core never references an Azure or OpenAI SDK (CC-004).
    /// </param>
    /// <param name="endpoint">The Responses endpoint, taken from trusted config.</param>
    /// <param name="expectedHost">When known, the host the endpoint must match.</param>
    public ResponsesPingProbe(HttpClient httpClient, string endpoint, string? expectedHost = null)
    {
        // SEC-010: reject non-https / unexpected-host endpoints before any use.
        Endpoints.ValidateEndpoint(endpoint, expectedHost);
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _endpoint = endpoint;
    }

    public async Task<ProbeResult> ProbeAsync(string agentDefinitionId, string agentSessionId)
    {
        // Research gap (research.md): the exact minimal Responses payload that
        // counts as keepalive activity and resets the idle timer is not
        // documented. The generic ping is the only built-in; its shape is
        // isolated here rather than invented across the code. Only the session id
        // is passed; no credential is ever attached (SEC-010, ADR 0007).
        try
        {
            var body = new JsonObject
            {
                ["agent_session_id"] = agentSessionId,
                ["input"] = "ping",
            };
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(_endpoint, content).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception exc)
        {
            // Reported via ProbeResult, not raised (mirror of the Python probe).
            return new ProbeResult(ok: false, latencySeconds: 0.0, error: exc.GetType().Name);
        }

        return new ProbeResult(ok: true, latencySeconds: 0.0);
    }
}
