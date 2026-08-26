namespace Foundry.Stoke.Warmup;

/// <summary>
/// Warm-up strategy associated with a pool registry (US3). Mirror of the Python
/// <c>WarmupStrategyKind</c>; the wire values match the Python enum values
/// one-to-one for cross-language parity (FR-022).
/// </summary>
public enum WarmupStrategyKind
{
    PreProvisionPool,
    Keepalive,
}

/// <summary>Wire-value mapping for <see cref="WarmupStrategyKind"/>.</summary>
public static class WarmupStrategyKinds
{
    private static readonly IReadOnlyDictionary<WarmupStrategyKind, string> WireValues =
        new Dictionary<WarmupStrategyKind, string>
        {
            [WarmupStrategyKind.PreProvisionPool] = "pre-provision-pool",
            [WarmupStrategyKind.Keepalive] = "keepalive",
        };

    private static readonly IReadOnlyDictionary<string, WarmupStrategyKind> ByWireValue =
        WireValues.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

    /// <summary>The lowercase wire value for this kind.</summary>
    public static string ToWireValue(this WarmupStrategyKind kind) => WireValues[kind];

    /// <summary>Parse a wire value into a <see cref="WarmupStrategyKind"/>.</summary>
    public static WarmupStrategyKind FromWireValue(string value) => ByWireValue[value];
}
