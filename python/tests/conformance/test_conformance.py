"""Cross-language conformance harness (US5, T068, FR-022, SC-001, ADR 0004).

Reads the language-neutral scenario fixtures under ``conformance/fixtures/`` and
drives the Python public surface (``foundry_stoke``) to assert each expected
observable outcome. The fixtures are the single source of truth shared with the
future .NET harness; this module only maps neutral fixture concepts to the
Python API. It stays thin on purpose: the scenarios live in the fixtures.
"""

from __future__ import annotations

import asyncio
import json
import sys
import types
from collections.abc import Awaitable, Callable, Mapping
from pathlib import Path
from typing import Any

import pytest

from foundry_stoke import (
    ApiKeyCredential,
    CallableProbe,
    ConnectionStringCredential,
    CredentialProvider,
    InMemoryStore,
    KeepaliveStrategy,
    NoCredentialAvailable,
    PreProvisionPoolStrategy,
    ProbeResult,
    RawSession,
    SessionController,
    StoreRecord,
    VirtualClock,
    validate_record_invariants,
)
from foundry_stoke.errors import (
    AlreadyExists,
    ConcurrencyConflict,
    InvalidIdleTimeout,
    InvalidRecordKey,
    NotFound,
    SessionClosed,
    TargetSizeExceeded,
    UnknownRecordType,
)
from foundry_stoke.observability import (
    SENSITIVE_SESSION_ID_ATTRIBUTE,
    redact_attributes,
    sanitize_exception_message,
)

# Repo root is three levels above this file: python/tests/conformance/<here>.
FIXTURES_DIR = Path(__file__).resolve().parents[3] / "conformance" / "fixtures"

# Neutral error identifiers used in fixtures -> concrete foundry_stoke types.
ERROR_TYPES: dict[str, type[Exception]] = {
    "AlreadyExists": AlreadyExists,
    "NotFound": NotFound,
    "ConcurrencyConflict": ConcurrencyConflict,
    "InvalidRecordKey": InvalidRecordKey,
    "UnknownRecordType": UnknownRecordType,
    "InvalidIdleTimeout": InvalidIdleTimeout,
    "SessionClosed": SessionClosed,
    "TargetSizeExceeded": TargetSizeExceeded,
    "NoCredentialAvailable": NoCredentialAvailable,
}


# --- Fixture loading ---------------------------------------------------------


def _load_cases() -> list[tuple[str, str, dict[str, Any]]]:
    if not FIXTURES_DIR.is_dir():
        return []
    entries: list[tuple[str, str, dict[str, Any]]] = []
    for path in sorted(FIXTURES_DIR.glob("*.json")):
        data = json.loads(path.read_text(encoding="utf-8"))
        for case in data["cases"]:
            entries.append((data["suite"], data["domain"], case))
    return entries


CASES = _load_cases()


# --- Shared helpers ----------------------------------------------------------


async def _expect(expect: Mapping[str, Any], awaitable: Awaitable[Any]) -> Any:
    """Await ``awaitable``; assert the typed error when ``expect`` names one."""
    error = expect.get("error")
    if error is not None:
        with pytest.raises(ERROR_TYPES[error]):
            await awaitable
        return None
    return await awaitable


def _to_record(data: Mapping[str, Any]) -> StoreRecord:
    return StoreRecord(
        id=data["id"],
        partition_key=data["partition_key"],
        type=data["type"],
        payload=dict(data["payload"]),
    )


# --- domain: store -----------------------------------------------------------


async def _handle_store(case: Mapping[str, Any]) -> None:
    store = InMemoryStore()
    etags: dict[str, str] = {}
    for step in case["steps"]:
        op = step["op"]
        expect = step.get("expect", {})
        if op == "create":
            result = await _expect(expect, store.create(_to_record(step["record"])))
            _bind_etag(result, step, expect, etags)
        elif op == "read":
            result = await _expect(expect, store.read(step["id"], step["partition_key"]))
            if result is not None and "payload" in expect:
                assert result.payload == expect["payload"]
        elif op == "upsert":
            etag = etags.get(step["expected_etag_from"]) if "expected_etag_from" in step else None
            result = await _expect(expect, store.upsert(_to_record(step["record"]), etag))
            _bind_etag(result, step, expect, etags)
        elif op == "query":
            result = await store.query_by_partition(step["partition_key"], step.get("type_filter"))
            assert len(result) == expect["count"]
        elif op == "validate_record":
            record = _to_record(step["record"])
            _expect_sync(expect, lambda record=record: validate_record_invariants(record))
        else:
            pytest.fail(f"unknown store op {op!r}")


def _bind_etag(
    result: StoreRecord | None,
    step: Mapping[str, Any],
    expect: Mapping[str, Any],
    etags: dict[str, str],
) -> None:
    if result is None:
        return
    if expect.get("etag_present"):
        assert result.etag
    if "bind" in step:
        etags[step["bind"]] = result.etag


def _expect_sync(expect: Mapping[str, Any], action: Callable[[], Any]) -> None:
    error = expect.get("error")
    if error is not None:
        with pytest.raises(ERROR_TYPES[error]):
            action()
    else:
        action()


# --- domain: session ---------------------------------------------------------


class _FakeSessionOperations:
    """Control-plane fake that returns scripted raw statuses per get call."""

    def __init__(self, get_status_script: list[str]) -> None:
        self._get_statuses = list(get_status_script)
        self._counter = 0

    async def create_session(
        self, agent_definition_id: str, idle_timeout_seconds: int
    ) -> RawSession:
        self._counter += 1
        return RawSession(
            agent_session_id=f"{agent_definition_id}-sess-{self._counter}", status="active"
        )

    async def get_session(self, agent_definition_id: str, agent_session_id: str) -> RawSession:
        status = self._get_statuses.pop(0) if self._get_statuses else "active"
        return RawSession(agent_session_id=agent_session_id, status=status)

    async def list_sessions(self, agent_definition_id: str) -> list[RawSession]:
        return []

    async def stop_session(self, agent_definition_id: str, agent_session_id: str) -> None:
        return None

    async def delete_session(self, agent_definition_id: str, agent_session_id: str) -> None:
        return None


async def _handle_session(case: Mapping[str, Any]) -> None:
    agent = case["agent_definition_id"]
    controller = SessionController(_FakeSessionOperations(case.get("get_status_script", [])))
    session_id: str | None = None
    for step in case["steps"]:
        op = step["op"]
        expect = step.get("expect", {})
        if op == "create":
            timeout = step["idle_timeout_seconds"]
            session = await _expect(expect, controller.create_session(agent, timeout))
            if session is not None:
                session_id = session.agent_session_id
                if expect.get("has_session_id"):
                    assert session_id
                _assert_state(session, expect)
        elif op == "get":
            assert session_id is not None
            session = await _expect(expect, controller.get_session(agent, session_id))
            if session is not None:
                _assert_state(session, expect)
        elif op == "stop":
            assert session_id is not None
            await _expect(expect, controller.stop_session(agent, session_id))
        elif op == "delete":
            assert session_id is not None
            await _expect(expect, controller.delete_session(agent, session_id))
        else:
            pytest.fail(f"unknown session op {op!r}")


def _assert_state(session: Any, expect: Mapping[str, Any]) -> None:
    if "state" in expect:
        assert session.state.value == expect["state"]


# --- domain: warmup ----------------------------------------------------------


async def _handle_warmup(case: Mapping[str, Any]) -> None:
    scenario = case["scenario"]
    handler = _WARMUP_SCENARIOS.get(scenario)
    if handler is None:
        pytest.fail(f"unknown warmup scenario {scenario!r}")
    await handler(case)


def _make_pool(
    store: InMemoryStore, agent_definition_id: str, target_size: int, **kwargs: Any
) -> PreProvisionPoolStrategy:
    return PreProvisionPoolStrategy(
        controller=SessionController(_FakeSessionOperations([])),
        store=store,
        agent_definition_id=agent_definition_id,
        target_size=target_size,
        clock=VirtualClock(auto_advance=True),
        **kwargs,
    )


async def _warmup_pool_reconcile_refill(case: Mapping[str, Any]) -> None:
    expect = case["expect"]
    store = InMemoryStore()
    agent = case["agent_definition_id"]
    pool = _make_pool(store, agent, case["target_size"])

    first = await pool.reconcile()
    assert first.created == expect["first_created"]
    assert first.ready == expect["first_ready"]

    for _ in range(case["consume"]):
        assert await pool.acquire() is not None

    record = await store.read(pool.registry_id, agent)
    assert len(record.payload["tracked_session_ids"]) == expect["after_consume_ready"]

    refill = await pool.reconcile()
    assert refill.created == expect["refill_created"]
    assert refill.ready == expect["refill_ready"]


async def _warmup_pool_two_definitions(case: Mapping[str, Any]) -> None:
    store = InMemoryStore()
    for definition, expected in zip(case["definitions"], case["expect"], strict=True):
        pool = _make_pool(store, definition["agent_definition_id"], definition["target_size"])
        report = await pool.reconcile()
        assert report.ready == expected["ready"]


async def _warmup_pool_target_ceiling(case: Mapping[str, Any]) -> None:
    with pytest.raises(ERROR_TYPES[case["expect"]["error"]]):
        _make_pool(
            InMemoryStore(),
            case["agent_definition_id"],
            case["target_size"],
            max_target_size=case["max_target_size"],
        )


async def _warmup_keepalive_fires(case: Mapping[str, Any]) -> None:
    expect = case["expect"]
    interval = case["interval_seconds"]
    # Semantic invariant: keepalive must fire strictly before the idle timeout.
    assert interval < case["idle_timeout_seconds"]

    probed: list[str] = []

    class _RecordingProbe:
        async def probe(self, agent_definition_id: str, agent_session_id: str) -> ProbeResult:
            probed.append(agent_session_id)
            return ProbeResult(ok=True, latency_seconds=0.0)

    clock = VirtualClock()
    strategy = KeepaliveStrategy(
        probe=_RecordingProbe(),
        clock=clock,
        interval_seconds=interval,
        agent_definition_id=case["agent_definition_id"],
        session_ids=case["session_ids"],
    )
    await strategy.start()
    await asyncio.sleep(0)
    assert len(probed) == expect["before_first_interval"]

    per_interval = expect["probed_per_interval"]
    for elapsed in range(1, case["advance_intervals"] + 1):
        await clock.advance(interval)
        await asyncio.sleep(0)
        assert len(probed) == per_interval * elapsed

    assert len(probed) == expect["total_after_advance"]
    await strategy.stop()


async def _warmup_keepalive_user_probe(case: Mapping[str, Any]) -> None:
    calls: list[tuple[str, str]] = []

    async def user_probe(agent_definition_id: str, agent_session_id: str) -> ProbeResult:
        calls.append((agent_definition_id, agent_session_id))
        return ProbeResult(ok=True, latency_seconds=0.0)

    strategy = KeepaliveStrategy(
        probe=CallableProbe(user_probe),
        clock=VirtualClock(auto_advance=True),
        interval_seconds=60,
        agent_definition_id=case["agent_definition_id"],
        session_ids=case["session_ids"],
    )
    report = await strategy.reconcile()
    assert report.probed == case["expect"]["probed"]
    assert len(calls) == case["expect"]["probed"]


_WARMUP_SCENARIOS: dict[str, Callable[[Mapping[str, Any]], Awaitable[None]]] = {
    "pool_reconcile_refill": _warmup_pool_reconcile_refill,
    "pool_two_definitions": _warmup_pool_two_definitions,
    "pool_target_ceiling": _warmup_pool_target_ceiling,
    "keepalive_fires": _warmup_keepalive_fires,
    "keepalive_user_probe": _warmup_keepalive_user_probe,
}


# --- domain: auth ------------------------------------------------------------


class _FakeInjectedCredential:
    def __repr__(self) -> str:
        return "_FakeInjectedCredential()"


class _FakeDefaultAzureCredential:
    """Stands in for the Entra ID primary path when it is available."""


async def _handle_auth(case: Mapping[str, Any]) -> None:
    assert case["scenario"] == "resolve"
    expect = case["expect"]
    env = case.get("env", {})
    had = "azure.identity" in sys.modules
    saved = sys.modules.get("azure.identity")
    try:
        _set_primary_availability(available=bool(case.get("primary_available")))
        injected = _FakeInjectedCredential() if case.get("injected") else None
        provider = CredentialProvider(credential=injected, environ=env)

        if "error" in expect:
            with pytest.raises(ERROR_TYPES[expect["error"]]):
                provider.resolve_credential()
            return

        credential = provider.resolve_credential()
        _assert_credential_kind(credential, expect["credential_kind"], injected, env)
    finally:
        if had:
            sys.modules["azure.identity"] = saved
        else:
            sys.modules.pop("azure.identity", None)


def _set_primary_availability(*, available: bool) -> None:
    if available:
        module = types.ModuleType("azure.identity")
        module.DefaultAzureCredential = _FakeDefaultAzureCredential  # type: ignore[attr-defined]
        sys.modules.setdefault("azure", types.ModuleType("azure"))
        sys.modules["azure.identity"] = module
    else:
        # A None entry makes ``from azure.identity import ...`` raise ImportError,
        # modeling the Entra ID primary path being unavailable.
        sys.modules["azure.identity"] = None  # type: ignore[assignment]


def _assert_credential_kind(
    credential: Any,
    kind: str,
    injected: _FakeInjectedCredential | None,
    env: Mapping[str, str],
) -> None:
    if kind == "injected":
        assert credential is injected
    elif kind == "primary":
        assert isinstance(credential, _FakeDefaultAzureCredential)
    elif kind == "api_key":
        assert isinstance(credential, ApiKeyCredential)
        _assert_no_secret_leak(credential, env)
    elif kind == "connection_string":
        assert isinstance(credential, ConnectionStringCredential)
        _assert_no_secret_leak(credential, env)
    else:
        pytest.fail(f"unknown credential_kind {kind!r}")


def _assert_no_secret_leak(credential: Any, env: Mapping[str, str]) -> None:
    for value in env.values():
        assert value not in repr(credential)
        assert value not in str(credential)


# --- domain: telemetry -------------------------------------------------------


async def _handle_telemetry(case: Mapping[str, Any]) -> None:
    scenario = case["scenario"]
    expect = case["expect"]
    if scenario == "redact":
        result = redact_attributes(case["attributes"], level=case["level"])
        for key in expect.get("present", []):
            assert key in result
        for key in expect.get("absent", []):
            assert key not in result
        if "session_id" in expect:
            emitted = result[SENSITIVE_SESSION_ID_ATTRIBUTE]
            original = case["attributes"][SENSITIVE_SESSION_ID_ATTRIBUTE]
            if expect["session_id"] == "hashed":
                assert emitted.startswith("sha256:")
                assert emitted != original
            else:
                assert emitted == original
    elif scenario == "sanitize_message":
        sanitized = sanitize_exception_message(case["message"])
        for substring in expect["absent_substrings"]:
            assert substring not in sanitized
    else:
        pytest.fail(f"unknown telemetry scenario {scenario!r}")


# --- Dispatch + parametrized entry point -------------------------------------


_HANDLERS: dict[str, Callable[[Mapping[str, Any]], Awaitable[None]]] = {
    "store": _handle_store,
    "session": _handle_session,
    "warmup": _handle_warmup,
    "auth": _handle_auth,
    "telemetry": _handle_telemetry,
}


def test_fixtures_present() -> None:
    assert FIXTURES_DIR.is_dir(), f"missing conformance fixtures dir: {FIXTURES_DIR}"
    assert CASES, "no conformance cases loaded from fixtures"


@pytest.mark.parametrize(
    "entry",
    CASES,
    ids=[case["id"] for _suite, _domain, case in CASES],
)
async def test_conformance(entry: tuple[str, str, dict[str, Any]]) -> None:
    suite, domain, case = entry
    handler = _HANDLERS.get(domain)
    if handler is None:
        pytest.fail(f"no conformance handler for domain {domain!r} (suite {suite})")
    await handler(case)
