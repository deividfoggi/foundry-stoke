# Foundry.Stoke (.NET)

.NET implementation of the Stoke control-plane library, published to NuGet.
Part of the cross-language monorepo; the public behavior mirrors the Python
package `foundry-stoke`, with idiomatic .NET APIs (ADR 0004).

- Library target: `net8.0` (LTS, matches the official `Azure.AI.Projects` SDK).
- Test target: `net10.0` (xUnit).

## Layout

| Path | Purpose |
| --- | --- |
| `Foundry.Stoke/` | Library (`Foundry.Stoke.csproj`). |
| `Foundry.Stoke.Tests/` | xUnit tests, including the cross-language conformance harness. |
| `Foundry.Stoke.sln` | Solution. |

## Build and test

```bash
dotnet build dotnet/Foundry.Stoke.sln
dotnet test dotnet/Foundry.Stoke.sln
dotnet format dotnet/Foundry.Stoke.sln --verify-no-changes
```

## Conformance

`Foundry.Stoke.Tests/Conformance/` reads the language-neutral fixtures under
`conformance/fixtures/` (the same files the Python harness consumes) and asserts
behavioral equivalence. Fixtures are the single source of truth for semantic
parity across languages.

## Session tracing

Subscribe to the `Foundry.Stoke` activity source through an application-owned
`ActivityListener` or OpenTelemetry provider. The library uses the framework's
`System.Diagnostics.ActivitySource` without additional production dependencies.
Providers, listeners, exporters, sampling, and Application Insights configuration belong
to the application. Without an enabled listener, tracing is a no-op.

Session operations emit `stoke.session.create`, `stoke.session.get`,
`stoke.session.stop`, and `stoke.session.delete` across their complete asynchronous
lifetime. Get, stop, and delete attach a hashed session handle. Create omits the handle;
all four omit free-form identifiers and exception text. Errors and cancellation set error
status without events, exception messages, stack traces, or status descriptions.
Existing `Telemetry` callbacks retain their behavior. Store and warmup tracing are outside
this slice.
