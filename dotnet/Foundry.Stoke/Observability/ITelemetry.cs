namespace Foundry.Stoke.Observability;

/// <summary>
/// Minimal instrumentation seam used by the warm-up strategies (ADR 0006).
/// Mirror of the Python <c>Telemetry</c> surface used here: emit a named metric
/// event with attributes, and record an exception as an event. Attributes are
/// redacted through <see cref="Redaction"/> before an event reaches any sink;
/// exception messages are scrubbed of secret-shaped substrings (SEC-003/SEC-009).
/// </summary>
public interface ITelemetry
{
    /// <summary>
    /// Emit a named metric/event with the given attributes. Only allowlisted
    /// attributes are emitted; the session id is hashed unless
    /// <paramref name="level"/> is <c>error</c>. When <paramref name="level"/>
    /// is null the implementation's default level is used.
    /// </summary>
    void Emit(string name, IReadOnlyDictionary<string, object?> attributes, string? level = null);

    /// <summary>Record an exception as a telemetry event with a sanitized message.</summary>
    void RecordException(string name, Exception exception, string? agentDefinitionId = null);
}
