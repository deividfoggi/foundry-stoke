namespace Foundry.Stoke.Observability;

/// <summary>
/// A telemetry event ready to be exported. Mirror of the Python
/// <c>TelemetryEvent</c>. Its attributes have already passed through
/// <see cref="Redaction"/> (SEC-003/SEC-009) by the time it reaches a sink.
/// </summary>
public sealed class TelemetryEvent
{
    public TelemetryEvent(string name, IReadOnlyDictionary<string, object?> attributes)
    {
        Name = name;
        Attributes = attributes;
    }

    public string Name { get; }

    public IReadOnlyDictionary<string, object?> Attributes { get; }
}
