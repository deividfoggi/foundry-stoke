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
