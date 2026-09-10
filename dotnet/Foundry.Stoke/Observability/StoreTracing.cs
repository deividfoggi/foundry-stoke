using System.Diagnostics;

namespace Foundry.Stoke.Observability;

/// <summary>Internal T040 operation lifetime; only constant provider labels enter telemetry.</summary>
internal static class StoreTracing
{
    internal static async Task<TResult> RunAsync<TResult>(string name, string provider, Func<Task<TResult>> operation)
    {
        using var activity = Start(name, provider);
        try
        {
            var result = await operation().ConfigureAwait(false);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return result;
        }
        catch
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
    }

    internal static async Task RunAsync(string name, string provider, Func<Task> operation)
    {
        using var activity = Start(name, provider);
        try
        {
            await operation().ConfigureAwait(false);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
    }

    private static Activity? Start(string name, string provider) =>
        SessionTracing.StartWithAttributes(name, new Dictionary<string, object?>
        {
            ["stoke.store.provider"] = provider,
        });
}
