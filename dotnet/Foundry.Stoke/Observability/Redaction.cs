using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Foundry.Stoke.Observability;

/// <summary>
/// Telemetry redaction policy (ADR 0006, SEC-003/SEC-009). Mirror of the Python
/// <c>observability</c> module: an allowlist decides which attributes may ever be
/// emitted, the agent session id is a capability handle (hashed at info level,
/// plaintext only at error level), and free-form exception messages are scrubbed
/// of secret-shaped substrings.
///
/// The guarantee is fail-safe by construction: anything outside
/// <see cref="AllowedAttributes"/> is dropped, not pattern-matched. The denylist
/// regexes are used only to sanitize exception messages, never as the primary
/// barrier. Hashing uses <see cref="SHA256"/> from the BCL so the core stays
/// dependency-free (CC-004).
/// </summary>
public static class Redaction
{
    /// <summary>
    /// Canonical allowlist of emittable attributes (plan.md, Observabilidade).
    /// Mirrors the Python ALLOWED_ATTRIBUTES set. Adding an attribute here is a
    /// conscious act (barrier against accidental leaks).
    /// </summary>
    public static readonly FrozenSet<string> AllowedAttributes = new[]
    {
        "stoke.agent_definition_id",
        "stoke.session.state",
        "stoke.store.provider",
        "stoke.store.operation",
        "stoke.warmup.strategy",
        "stoke.warmup.target_size",
        "stoke.warmup.ready",
        "stoke.warmup.created",
        "stoke.warmup.failures",
        "stoke.probe.ok",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The sensitive session-id attribute governed by the handle rule.</summary>
    public const string SensitiveSessionIdAttribute = "stoke.agent_session_id";

    private const string Redacted = "[redacted]";

    // Denylist used only to sanitize free-form exception messages. Mirrors the
    // Python _SECRET_PATTERNS tuple (same order, same semantics).
    private static readonly Regex[] SecretPatterns =
    {
        new(@"(AccountKey|SharedAccessKey|AccessKey|Password|Pwd|Key)=([^;\s]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"(sig|signature|token|api[-_]?key|access[-_]?token)=([^&;\s]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"Bearer\s+[A-Za-z0-9._~+/=-]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"https?://[^/\s]*:[^/\s]*@[^\s]+", RegexOptions.CultureInvariant),
    };

    /// <summary>
    /// Return a short, stable, non-reversible token for an agent session id.
    /// Byte-identical to the Python <c>hash_session_id</c>: <c>sha256:</c> plus
    /// the first 12 hex characters of the SHA-256 digest of the UTF-8 value.
    /// </summary>
    public static string HashSessionId(string agentSessionId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(agentSessionId));
        var hex = Convert.ToHexString(digest).ToLowerInvariant();
        return $"sha256:{hex[..12]}";
    }

    /// <summary>Redact secret-shaped substrings from a free-form exception message.</summary>
    public static string SanitizeExceptionMessage(string message)
    {
        var redacted = message;
        foreach (var pattern in SecretPatterns)
        {
            redacted = pattern.Replace(redacted, Redacted);
        }

        return redacted;
    }

    /// <summary>
    /// Return only allowlisted attributes, applying the session-id handle rule.
    /// <paramref name="level"/> controls the treatment of the session id: hashed
    /// at <c>info</c> level, retained in plaintext at <c>error</c> level.
    /// </summary>
    public static Dictionary<string, object?> RedactAttributes(
        IReadOnlyDictionary<string, object?> attributes,
        string level = "info")
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in attributes)
        {
            if (key == SensitiveSessionIdAttribute)
            {
                result[key] = level == "error"
                    ? value
                    : HashSessionId(value?.ToString() ?? string.Empty);
                continue;
            }

            if (AllowedAttributes.Contains(key))
            {
                result[key] = value;
            }

            // Anything else is dropped (fail-safe).
        }

        return result;
    }
}
