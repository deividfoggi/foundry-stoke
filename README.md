# Stoke

Control-plane library for Foundry hosted agent instances: instance warm-up, session lifecycle, minimal state, and a pluggable durable store.

[![CI](https://github.com/deividfoggi/foundry-stoke/actions/workflows/ci.yml/badge.svg)](https://github.com/deividfoggi/foundry-stoke/actions/workflows/ci.yml)
[![Release](https://github.com/deividfoggi/foundry-stoke/actions/workflows/release.yml/badge.svg)](https://github.com/deividfoggi/foundry-stoke/actions/workflows/release.yml)
[![PyPI version](https://img.shields.io/pypi/v/foundry-stoke)](https://pypi.org/project/foundry-stoke/)
[![Python versions](https://img.shields.io/pypi/pyversions/foundry-stoke)](https://pypi.org/project/foundry-stoke/)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue)](LICENSE)

## What Stoke is

Stoke is a lightweight control plane for [hosted agents in Foundry Agent Service](https://learn.microsoft.com/azure/foundry/agents/concepts/hosted-agents). It manages the lifecycle of agent sessions (create, reference, resume, query), keeps instances warm to reduce cold starts, and tracks that control-plane state in a durable store you choose.

Stoke stays on the control plane. The real conversation and business traffic (the Responses and Invocations protocols) remains the responsibility of the official Foundry SDK. Stoke never ships a data-plane traffic client, which keeps it small and avoids duplicating the SDK.

```text
Your app ── real traffic (Responses / Invocations) ──> Official Foundry SDK  (data plane)
   │
   └──────── lifecycle + warm-up ─────────────────────> Stoke               (control plane)
                                                          │  session lifecycle (/sessions)
                                                          │  warm-up strategies + probe
                                                          └  durable store (pluggable provider)
```

## Status

Beta (`0.1.0b1`). The public API and behavior may still change. The durable-store core, session lifecycle, warm-up, configuration facade, authentication, and telemetry redaction are implemented and covered by tests and a cross-language conformance suite. The real Foundry adapter has not yet been validated against a live service. Do not rely on it for production workloads at this stage.

## Capabilities

| Capability | Summary |
|---|---|
| Session lifecycle | Create, reference, resume, and query sessions. Reflects the official session status taxonomy. |
| Warm-up | Two pluggable strategies: a pre-provisioned pool of ready sessions, and a keepalive probe that prevents idle expiry. |
| Durable store | A storage-agnostic provider interface. Reference providers for development, and a data model designed so a third-party provider (for example Cosmos DB or Redis) plugs in without changing the core. |
| Authentication | Entra ID via `DefaultAzureCredential` as the primary path, with an optional API-key / connection-string fallback. |
| Observability | OpenTelemetry-friendly instrumentation under the `stoke.*` namespace, with allowlist-based redaction so secrets and session identifiers are never leaked. |

## Supported languages

| Language | Package | Status |
|---|---|---|
| Python | `foundry-stoke` (PyPI), import `foundry_stoke` | Beta |
| C# / .NET | `Foundry.Stoke` (NuGet) | Planned |

The public API is designed to be semantically equivalent across languages, validated by a shared conformance suite in [conformance/](conformance/).

## Installation

```bash
# Core (dependency-free; useful for tests and custom providers)
pip install foundry-stoke

# With the real Foundry control-plane adapter and Entra ID auth
pip install "foundry-stoke[azure]"
```

While the package is in beta on TestPyPI, install with:

```bash
pip install -i https://test.pypi.org/simple/ \
  --extra-index-url https://pypi.org/simple \
  "foundry-stoke==0.1.0b1"
```

Requires Python 3.10 or later.

## Quickstart

Using the configuration facade. It reads `FOUNDRY_PROJECT_ENDPOINT` from the environment (Foundry injects it at runtime), validates it, wires the credential and store, and exposes a ready session controller.

```python
import asyncio
from foundry_stoke import Stoke

async def main() -> None:
    stoke = Stoke.from_env()  # requires FOUNDRY_PROJECT_ENDPOINT and the [azure] extra
    session = await stoke.sessions.create_session("my-agent")
    print(session.agent_session_id, session.state)

asyncio.run(main())
```

Using pure dependency injection when you want full control over the client and the store.

```python
from azure.ai.projects.aio import AIProjectClient
from foundry_stoke import CredentialProvider, SessionController, FileSystemStore
from foundry_stoke.session.foundry_adapter import FoundrySessionOperations

credential = CredentialProvider().resolve_credential()  # DefaultAzureCredential by default
client = AIProjectClient(
    endpoint="https://<project>.services.ai.azure.com/api/projects/<project>",
    credential=credential,
)
sessions = SessionController(FoundrySessionOperations(client))
store = FileSystemStore("./.stoke")  # development store; swap for a production provider
```

## Core concepts

### Session state

Stoke reflects the official [`AgentSessionStatus`](https://learn.microsoft.com/en-us/javascript/api/@azure/ai-projects/agentsessionstatus) values: `creating`, `active`, `idle`, `updating`, `failed`, `deleting`, `deleted`, `expired`. Any unrecognized or future value maps to `UNKNOWN` rather than being coerced, so state is never silently misreported. A resume is a derived transition (an idle session observed active again), not a status.

### Durable store provider

The store persists Stoke's own control-plane state (tracked sessions, warm-pool registry). The interface is minimal: create, read, upsert, delete, and query-by-partition, with optimistic concurrency by `etag`. Records carry a stable `id`, a `partition_key`, an `etag`, and a JSON payload. Reference providers `InMemoryStore` and `FileSystemStore` ship for development.

Bringing your own provider (for example Cosmos DB or Redis) requires implementing the same interface. See [contracts/durable-store-provider.md](docs/features/stoke-beta/contracts/durable-store-provider.md) for the provider author responsibilities (compare-and-set for `etag`, a partition index for queries, and durability expectations).

### Warm-up

Two strategies, selected per agent definition:

- Pre-provision pool: keep a target number of ready sessions per agent, refilled as they are consumed.
- Keepalive: invoke a pluggable probe before the idle timeout so a session does not expire. A generic built-in probe uses the Responses endpoint; for custom or Invocations containers you supply your own probe callable.

The scheduler is fully non-blocking and uses an injectable clock, so warm-up timing is testable without real waits.

### Authentication

`DefaultAzureCredential` (Entra ID) is the primary path. In production, prefer a deterministic credential (for example by setting `AZURE_TOKEN_CREDENTIALS` or injecting an explicit `TokenCredential`). An API-key or connection-string fallback is available when the primary credential is unavailable. Secrets are read from environment, configuration, or a vault, and are never persisted or exposed in string representations.

## Repository layout

```text
python/                     Python package (foundry-stoke)
dotnet/                     .NET package (planned)
conformance/                Language-agnostic conformance fixtures (single source of truth)
docs/features/stoke-beta/   Spec, plan, research, data model, ADRs, contracts, security review
docs/architecture/          Architecture decision records
.github/workflows/          CI and release pipelines
```

## Foundry documentation

- [What are hosted agents?](https://learn.microsoft.com/azure/foundry/agents/concepts/hosted-agents)
- [Manage hosted agent sessions](https://learn.microsoft.com/azure/foundry/agents/how-to/manage-hosted-sessions)
- [AgentSessionStatus](https://learn.microsoft.com/en-us/javascript/api/@azure/ai-projects/agentsessionstatus)
- [Foundry hosted agents with the Agent Framework](https://learn.microsoft.com/agent-framework/hosting/foundry-hosted-agent)

## Development

```bash
cd python
python -m venv .venv && source .venv/bin/activate
pip install -e ".[dev,azure]"
ruff check . && ruff format --check .
mypy --strict src
pytest
```

## Releasing

The release pipeline publishes to TestPyPI and then, after manual approval, to PyPI, using PyPI Trusted Publishing (OIDC). See the Releasing section in [CONTRIBUTING.md](CONTRIBUTING.md).

## License

Apache-2.0. See [LICENSE](LICENSE) and [NOTICE](NOTICE). Derivative works must reference this project as the original.
