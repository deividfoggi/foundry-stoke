using System.Diagnostics;

namespace Foundry.Stoke.Observability;

/// <summary>Internal session activities with pre-emission redaction (T025, ADR 0006).</summary>
internal static class SessionTracing
{
    private static readonly ActivitySource Source = new("Foundry.Stoke");

    internal static Activity? Start(string name, string? sessionId = null)
    {
        if (!Source.HasListeners())
        {
            return null;
        }

        var attributes = new Dictionary<string, object?>();
        if (sessionId is not null)
        {
            attributes[Redaction.SensitiveSessionIdAttribute] = sessionId;
        }

        return StartWithAttributes(name, attributes);
    }

    internal static Activity? StartWithAttributes(string name, IReadOnlyDictionary<string, object?> attributes)
    {
        return Source.StartActivity(name, ActivityKind.Internal, default(ActivityContext),
            Redaction.RedactAttributes(attributes, level: "info"));
    }
}
