using System.Diagnostics;

namespace Foundry.Stoke.Observability;

/// <summary>T050 warm-up lifetimes, including failures handled by the strategy.</summary>
internal static class WarmupTracing
{
    internal static async Task<TResult> RunAsync<TResult>(
        string name, string strategy, Func<Action, Task<TResult>> operation)
    {
        using var activity = SessionTracing.StartWithAttributes(name, new Dictionary<string, object?>
        {
            ["stoke.warmup.strategy"] = strategy,
        });
        var failed = false;
        try
        {
            var result = await operation(() => failed = true).ConfigureAwait(false);
            activity?.SetStatus(failed ? ActivityStatusCode.Error : ActivityStatusCode.Ok);
            return result;
        }
        catch
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
    }
}
