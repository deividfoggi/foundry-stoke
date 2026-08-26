namespace Foundry.Stoke.Observability;

/// <summary>
/// Thin instrumentation facade. Mirror of the Python <c>Telemetry</c>. Pass a
/// <paramref name="sink"/> to receive <see cref="TelemetryEvent"/> instances
/// (e.g. to bridge to an OpenTelemetry exporter). Without a sink the layer is a
/// safe no-op, which is the default the warm-up strategies use.
///
/// TODO(SEC-003): this slice does not redact attributes or scrub exception
/// messages; that lands with the telemetry slice. Do not wire a live sink until
/// redaction is in place.
/// </summary>
public sealed class Telemetry : ITelemetry
{
    private readonly Action<TelemetryEvent>? _sink;

    public Telemetry(Action<TelemetryEvent>? sink = null)
    {
        _sink = sink;
    }

    public void Emit(string name, IReadOnlyDictionary<string, object?> attributes)
    {
        _sink?.Invoke(new TelemetryEvent(name, attributes));
    }

    public void RecordException(string name, Exception exception, string? agentDefinitionId = null)
    {
        var attributes = new Dictionary<string, object?>();
        if (agentDefinitionId is not null)
        {
            attributes["stoke.agent_definition_id"] = agentDefinitionId;
        }

        attributes["exception.type"] = exception.GetType().Name;

        // TODO(SEC-009): scrub secret-shaped substrings from the message in the
        // telemetry slice; emitted verbatim here (no-op default keeps it safe).
        attributes["exception.message"] = exception.Message;
        _sink?.Invoke(new TelemetryEvent(name, attributes));
    }
}
