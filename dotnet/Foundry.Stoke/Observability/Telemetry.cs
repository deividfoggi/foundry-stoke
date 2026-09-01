namespace Foundry.Stoke.Observability;

/// <summary>
/// Thin instrumentation facade that redacts before emitting. Mirror of the
/// Python <c>Telemetry</c>. Pass a <paramref name="sink"/> to receive
/// <see cref="TelemetryEvent"/> instances (e.g. to bridge to an OpenTelemetry
/// exporter). Without a sink the layer is a safe no-op, which is the default the
/// warm-up strategies use. Redaction (SEC-003/SEC-009) is always applied, so an
/// event that reaches a sink never carries non-allowlisted attributes, an
/// unhashed session id at info level, or an unscrubbed exception message.
/// </summary>
public sealed class Telemetry : ITelemetry
{
    private readonly Action<TelemetryEvent>? _sink;
    private readonly string _level;

    public Telemetry(Action<TelemetryEvent>? sink = null, string level = "info")
    {
        _sink = sink;
        _level = level;
    }

    public void Emit(string name, IReadOnlyDictionary<string, object?> attributes, string? level = null)
    {
        var redacted = Redaction.RedactAttributes(attributes, level ?? _level);
        _sink?.Invoke(new TelemetryEvent(name, redacted));
    }

    public void RecordException(string name, Exception exception, string? agentDefinitionId = null)
    {
        const string level = "error";
        var attributes = new Dictionary<string, object?>();
        if (agentDefinitionId is not null)
        {
            attributes["stoke.agent_definition_id"] = agentDefinitionId;
        }

        // Run the allowlist path first, then attach the sanitized exception
        // context outside it (mirrors the Python record_exception).
        var redacted = Redaction.RedactAttributes(attributes, level);
        redacted["exception.type"] = exception.GetType().Name;
        redacted["exception.message"] = Redaction.SanitizeExceptionMessage(exception.Message);
        _sink?.Invoke(new TelemetryEvent(name, redacted));
    }
}
