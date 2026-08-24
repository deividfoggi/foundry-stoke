"""Behavior tests for warm-up strategies (US3, T042-T045, SEC-007, ADR 0003).

All timing is deterministic via VirtualClock; no real sleeps.
"""

from __future__ import annotations

import asyncio
import random

import pytest

from foundry_stoke import (
    FoundryUnavailable,
    InMemoryStore,
    RawSession,
    SessionController,
    TargetSizeExceeded,
)
from foundry_stoke.observability import Telemetry
from foundry_stoke.scheduling import VirtualClock
from foundry_stoke.warmup import (
    CallableProbe,
    KeepaliveStrategy,
    PreProvisionPoolStrategy,
    ProbeResult,
)


class FakeSessionOperations:
    def __init__(self) -> None:
        self.status = "active"
        self.created: list[str] = []
        self._counter = 0

    async def create_session(self, agent_definition_id: str, idle_timeout_seconds: int):
        self._counter += 1
        session_id = f"sess-{self._counter}"
        self.created.append(session_id)
        return RawSession(agent_session_id=session_id, status=self.status)

    async def get_session(self, agent_definition_id: str, agent_session_id: str):
        return RawSession(agent_session_id=agent_session_id, status=self.status)

    async def list_sessions(self, agent_definition_id: str):
        return [RawSession(agent_session_id=s, status=self.status) for s in self.created]

    async def stop_session(self, agent_definition_id: str, agent_session_id: str) -> None:
        return None

    async def delete_session(self, agent_definition_id: str, agent_session_id: str) -> None:
        return None


class FailingSessionOperations(FakeSessionOperations):
    async def create_session(self, agent_definition_id: str, idle_timeout_seconds: int):
        raise FoundryUnavailable("control plane unavailable")


def _pool(controller, store, clock, **kwargs) -> PreProvisionPoolStrategy:
    return PreProvisionPoolStrategy(
        controller=controller,
        store=store,
        agent_definition_id="agent-a",
        clock=clock,
        **kwargs,
    )


async def test_pool_reconciles_to_target_and_persists_registry():
    controller = SessionController(FakeSessionOperations())
    store = InMemoryStore()
    clock = VirtualClock(auto_advance=True)
    pool = _pool(controller, store, clock, target_size=3)

    report = await pool.reconcile()
    assert report.created == 3
    assert report.ready == 3

    record = await store.read(pool.registry_id, "agent-a")
    assert record.type == "warm-pool-registry"
    assert len(record.payload["tracked_session_ids"]) == 3


async def test_pool_refills_after_consumption():
    controller = SessionController(FakeSessionOperations())
    store = InMemoryStore()
    clock = VirtualClock(auto_advance=True)
    pool = _pool(controller, store, clock, target_size=3)

    await pool.reconcile()
    consumed = await pool.acquire()
    assert consumed is not None

    report = await pool.reconcile()
    assert report.created == 1
    assert report.ready == 3


async def test_pool_target_size_ceiling_enforced():
    controller = SessionController(FakeSessionOperations())
    with pytest.raises(TargetSizeExceeded):
        _pool(controller, InMemoryStore(), VirtualClock(), target_size=1000, max_target_size=10)


async def test_pool_backoff_and_retry_ceiling_on_failure():
    controller = SessionController(FailingSessionOperations())
    clock = VirtualClock(auto_advance=True)
    pool = _pool(
        controller,
        InMemoryStore(),
        clock,
        target_size=3,
        max_retries=4,
        base_backoff_seconds=1.0,
        max_backoff_seconds=30.0,
        rng=random.Random(1234),
    )

    report = await pool.reconcile()
    assert report.created == 0
    assert report.failures == 5  # max_retries + 1, then it stops (no tight loop)
    assert clock.total_delay > 0  # backoff applied via the clock, not real sleep


async def test_pool_emits_refill_metric_with_allowlisted_attributes():
    controller = SessionController(FakeSessionOperations())
    events: list = []
    pool = _pool(
        controller,
        InMemoryStore(),
        VirtualClock(auto_advance=True),
        target_size=2,
        telemetry=Telemetry(sink=events.append),
    )
    await pool.reconcile()

    refill = next(e for e in events if e.name == "stoke.warmup.refill")
    assert refill.attributes["stoke.agent_definition_id"] == "agent-a"
    assert refill.attributes["stoke.warmup.strategy"] == "pre-provision-pool"


async def test_keepalive_fires_probe_before_idle_timeout():
    probed: list[tuple[str, str]] = []

    class RecordingProbe:
        async def probe(self, agent_definition_id: str, agent_session_id: str) -> ProbeResult:
            probed.append((agent_definition_id, agent_session_id))
            return ProbeResult(ok=True, latency_seconds=0.01)

    clock = VirtualClock()
    strategy = KeepaliveStrategy(
        probe=RecordingProbe(),
        clock=clock,
        interval_seconds=300,
        agent_definition_id="agent-a",
        session_ids=["sess-1"],
    )
    await strategy.start()
    await asyncio.sleep(0)
    assert probed == []  # nothing before the first interval

    await clock.advance(300)
    await asyncio.sleep(0)
    assert len(probed) == 1  # fired well before the 900s idle timeout

    await clock.advance(300)
    await asyncio.sleep(0)
    assert len(probed) == 2

    await strategy.stop()


async def test_keepalive_invokes_user_supplied_probe_for_each_session():
    calls: list[tuple[str, str]] = []

    async def user_probe(agent_definition_id: str, agent_session_id: str) -> ProbeResult:
        calls.append((agent_definition_id, agent_session_id))
        return ProbeResult(ok=True, latency_seconds=0.0)

    strategy = KeepaliveStrategy(
        probe=CallableProbe(user_probe),
        clock=VirtualClock(auto_advance=True),
        interval_seconds=60,
        agent_definition_id="agent-a",
        session_ids=["s1", "s2"],
    )
    report = await strategy.reconcile()
    assert set(calls) == {("agent-a", "s1"), ("agent-a", "s2")}
    assert report.probed == 2
