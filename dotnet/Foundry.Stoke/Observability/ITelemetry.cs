namespace Foundry.Stoke.Observability;

/// <summary>
/// Minimal instrumentation seam used by the warm-up strategies (ADR 0006).
/// Mirror of the Python <c>Telemetry</c> surface used here: emit a named metric
/// event with attributes, and record an exception as an event.
///
/// TODO(SEC-003): allowlist redaction of attributes and exception-message
/// scrubbing (SEC-009) are deferred to the telemetry slice. This seam emits
/// attributes verbatim; the default implementation is a no-op sink, so nothing
/// leaves the process until a sink is wired.
/// </summary>
public interface ITelemetry
{
    /// <summary>Emit a named metric/event with the given attributes.</summary>
    void Emit(string name, IReadOnlyDictionary<string, object?> attributes);

    /// <summary>Record an exception as a telemetry event.</summary>
    void RecordException(string name, Exception exception, string? agentDefinitionId = null);
}
