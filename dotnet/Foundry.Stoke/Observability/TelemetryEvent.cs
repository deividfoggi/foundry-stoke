namespace Foundry.Stoke.Observability;

/// <summary>
/// A telemetry event ready to be exported. Mirror of the Python
/// <c>TelemetryEvent</c>. Attribute values are carried as-is in this slice;
/// allowlist redaction (SEC-003/SEC-009) is deferred to the telemetry slice.
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
