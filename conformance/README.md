# Cross-language conformance suite

This directory holds the language-neutral scenario fixtures that define Stoke's
behavioral contract. They are the single source of truth for semantic
equivalence between language implementations (ADR 0004, FR-022, SC-001). Each
language ships a thin harness that reads these same fixtures and drives its own
public surface to assert every expected outcome.

- Python harness: `python/tests/conformance/` (runs under `pytest`).
- .NET harness: `dotnet/Foundry.Stoke.Tests/Conformance/` (ships with the .NET increment).

## Why JSON

Fixtures are JSON, not YAML, so every language parses them with its standard
library and no extra dependency (Python `json`, .NET `System.Text.Json`). This
keeps the harnesses dependency-free and avoids coupling the conformance suite to
a YAML parser in each ecosystem.

## Design principles

- **Behavioral, not snapshot.** Fixtures assert observable outcomes (result
  shape or typed error identifier), never byte-for-byte output. Idiomatic
  per-language differences are legitimate and must not fail conformance.
- **Neutral error identifiers.** Errors are named in a language-neutral way
  (for example `ConcurrencyConflict`, `SessionClosed`, `NoCredentialAvailable`).
  Each harness maps them to its concrete error type.
- **Deterministic.** Timing scenarios use a virtual clock. No real sleeps, no
  live network, no live Azure. Fakes and seams stand in for external systems.

## File layout

```
conformance/
  README.md
  fixtures/
    durable-store.json
    session-lifecycle.json
    warmup.json
    auth-probe.json
    telemetry.json
```

## Fixture file schema

Each file is one suite:

| Field | Type | Meaning |
|-------|------|---------|
| `suite` | string | Suite identifier. |
| `domain` | string | Dispatch key that selects the harness handler. One of `store`, `session`, `warmup`, `auth`, `telemetry`. |
| `description` | string | Human summary of the contract the suite encodes. |
| `cases` | array | The conformance cases (see below). |

Every case has an `id` (unique across all files), a `description`, and an
optional `conformance` reference to a spec Conformance Case or security control
(for example `CC-003`, `SEC-008`). The remaining fields depend on `domain`.

### domain: store

A case has `steps`, executed in order against a single store instance. Etags
produced by a step can be bound to a name and referenced by a later step.

| Step field | Meaning |
|------------|---------|
| `op` | `create`, `read`, `upsert`, `query`, or `validate_record`. |
| `record` | Record body (`id`, `partition_key`, `type`, `payload`) for `create`/`upsert`/`validate_record`. |
| `id`, `partition_key` | Composite key for `read`. |
| `partition_key`, `type_filter` | Arguments for `query` (`type_filter` may be `null`). |
| `expected_etag_from` | Name of a previously bound step whose etag is used as the optimistic-concurrency token for `upsert`. |
| `bind` | Name to store this step's resulting etag under. |
| `expect` | Expected outcome: `{ "ok": true, "etag_present": true }`, `{ "payload": {...} }`, `{ "count": N }`, or `{ "error": "<Identifier>" }`. |

### domain: session

A case has an `agent_definition_id`, an optional `get_status_script` (the raw
status the control-plane fake returns for each successive `get`), and `steps`.

| Step field | Meaning |
|------------|---------|
| `op` | `create`, `get`, `stop`, or `delete`. |
| `idle_timeout_seconds` | Argument for `create`. |
| `expect` | `{ "state": "Active" \| "Idle" \| "Resumed", "has_session_id": true }` or `{ "error": "<Identifier>" }`. |

`get`, `stop`, and `delete` operate on the session id returned by the preceding
`create`.

### domain: warmup

A case has a `scenario` discriminator and scenario-specific parameters. All
timing uses the virtual clock.

| Scenario | Parameters | Asserts |
|----------|-----------|---------|
| `pool_reconcile_refill` | `agent_definition_id`, `target_size`, `consume` | Reconcile reaches target; after consuming, a second reconcile refills to target. |
| `pool_two_definitions` | `definitions[]`, `expect[]` | Each definition reconciles to its own target independently. |
| `pool_target_ceiling` | `target_size`, `max_target_size` | Construction above the ceiling raises `TargetSizeExceeded`. |
| `keepalive_fires` | `interval_seconds`, `idle_timeout_seconds`, `session_ids`, `advance_intervals` | The probe fires once per session per interval, before the idle timeout. |
| `keepalive_user_probe` | `agent_definition_id`, `session_ids` | Keepalive uses exclusively the user-supplied probe. |

### domain: auth

A case has `scenario: "resolve"`, an optional `injected` flag, a
`primary_available` flag (whether the Entra ID primary path can resolve), an
`env` map of fallback configuration, and an `expect` of either
`{ "credential_kind": "injected" \| "primary" \| "api_key" \| "connection_string" }`
or `{ "error": "NoCredentialAvailable" }`. Harnesses additionally assert that no
`env` secret value appears in the resolved credential's string representation.

### domain: telemetry

A case has a `scenario` of `redact` or `sanitize_message`.

- `redact`: `level` (`info`/`error`), an `attributes` map, and an `expect` of
  `present`/`absent` attribute keys and/or a `session_id` treatment
  (`hashed`/`plaintext`).
- `sanitize_message`: a `message` and an `expect.absent_substrings` list that
  must not appear in the sanitized output.

## Adding a new language harness

1. Load every `*.json` file in `conformance/fixtures/`.
2. For each case, dispatch on `domain` to a handler.
3. In each handler, map the neutral fixture concepts to that language's public
   surface, using the in-memory store, the virtual clock, and fakes/seams for
   external systems. Keep the handler thin: the fixtures carry the scenarios.
4. Map neutral error identifiers to the language's concrete error types.
5. Wire the harness into the language's test runner so it runs with the rest of
   the suite (Python: `pytest`; .NET: a `Category=Conformance` test).

The Python harness in `python/tests/conformance/test_conformance.py` is the
reference implementation.
