"""Pre-provision pool warm-up strategy (US3, T044/T045, SEC-007, ADR 0003).

Keeps N ready sessions per agent definition, reconciling to the target size via
the :class:`~foundry_stoke.session.SessionController` (create/query) with no
data-plane protocol. Warm-pool state is persisted through the durable store as a
``warm-pool-registry`` record.

SEC-007: the target size has a configurable, validated ceiling; reconciliation
failures use exponential backoff with jitter and a retry ceiling to avoid tight
loops when the service is unavailable; a ``stoke.warmup.refill`` metric is
emitted each cycle.
"""

from __future__ import annotations

import asyncio
import contextlib
import random
from datetime import datetime, timezone
from typing import Any

from foundry_stoke.errors import AlreadyExists, NotFound, TargetSizeExceeded
from foundry_stoke.models import (
    TERMINAL_SESSION_STATES,
    WARM_POOL_REGISTRY_TYPE,
    SessionState,
    StoreRecord,
    WarmPoolRegistry,
    WarmupStrategyKind,
)
from foundry_stoke.observability import Telemetry
from foundry_stoke.scheduling import Clock
from foundry_stoke.session import SessionController
from foundry_stoke.store.provider import DurableStoreProvider, validate_record_invariants
from foundry_stoke.warmup.strategy import WarmupReport

DEFAULT_MAX_TARGET_SIZE = 100


def _utcnow() -> datetime:
    return datetime.now(timezone.utc)


def _registry_to_payload(registry: WarmPoolRegistry) -> dict[str, Any]:
    return {
        "agent_definition_id": registry.agent_definition_id,
        "target_size": registry.target_size,
        "strategy": registry.strategy.value,
        "tracked_session_ids": list(registry.tracked_session_ids),
        "last_reconciled_at": registry.last_reconciled_at.isoformat(),
    }


def _registry_from_payload(payload: dict[str, Any]) -> WarmPoolRegistry:
    return WarmPoolRegistry(
        agent_definition_id=str(payload["agent_definition_id"]),
        target_size=int(payload["target_size"]),
        strategy=WarmupStrategyKind(payload["strategy"]),
        tracked_session_ids=list(payload["tracked_session_ids"]),
        last_reconciled_at=datetime.fromisoformat(payload["last_reconciled_at"]),
    )


class PreProvisionPoolStrategy:
    """Maintain a pool of ready sessions per agent definition."""

    def __init__(
        self,
        *,
        controller: SessionController,
        store: DurableStoreProvider,
        agent_definition_id: str,
        target_size: int,
        clock: Clock,
        refill_interval_seconds: float = 60.0,
        max_target_size: int = DEFAULT_MAX_TARGET_SIZE,
        max_retries: int = 5,
        base_backoff_seconds: float = 1.0,
        max_backoff_seconds: float = 60.0,
        telemetry: Telemetry | None = None,
        rng: random.Random | None = None,
    ) -> None:
        if target_size < 0:
            raise ValueError("target_size must be >= 0")
        if target_size > max_target_size:
            raise TargetSizeExceeded(
                f"target_size {target_size} exceeds the maximum {max_target_size} (SEC-007)"
            )
        self._controller = controller
        self._store = store
        self._agent_definition_id = agent_definition_id
        self._target_size = target_size
        self._clock = clock
        self._interval = refill_interval_seconds
        self._max_retries = max_retries
        self._base_backoff = base_backoff_seconds
        self._max_backoff = max_backoff_seconds
        self._telemetry = telemetry or Telemetry()
        self._rng = rng or random.Random()
        self._registry_id = f"warm-pool:{agent_definition_id}"
        self._running = False
        self._task: asyncio.Task[None] | None = None

    @property
    def registry_id(self) -> str:
        return self._registry_id

    async def reconcile(self) -> WarmupReport:
        etag, registry = await self._load_registry()
        ready, evicted = await self._filter_ready(registry.tracked_session_ids)
        created = 0
        failures = 0
        attempt = 0
        while len(ready) < self._target_size:
            try:
                session = await self._controller.create_session(self._agent_definition_id)
            except Exception as exc:  # noqa: BLE001 - transient unavailability, retried with backoff
                failures += 1
                attempt += 1
                self._telemetry.record_exception(
                    "stoke.warmup.refill", exc, agent_definition_id=self._agent_definition_id
                )
                if attempt > self._max_retries:
                    break
                await self._clock.delay(self._backoff(attempt))
                continue
            ready.append(session.agent_session_id)
            created += 1
            attempt = 0

        registry.tracked_session_ids = ready
        registry.target_size = self._target_size
        registry.last_reconciled_at = _utcnow()
        await self._save_registry(etag, registry)

        self._telemetry.emit(
            "stoke.warmup.refill",
            {
                "stoke.agent_definition_id": self._agent_definition_id,
                "stoke.warmup.strategy": "pre-provision-pool",
                "stoke.warmup.target_size": self._target_size,
                "stoke.warmup.ready": len(ready),
                "stoke.warmup.created": created,
                "stoke.warmup.evicted": evicted,
                "stoke.warmup.failures": failures,
            },
        )
        return WarmupReport(
            strategy="pre-provision-pool",
            reconciled_at=self._clock.now(),
            ready=len(ready),
            created=created,
            failures=failures,
            evicted=evicted,
        )

    async def _filter_ready(self, session_ids: list[str]) -> tuple[list[str], int]:
        """Keep only sessions still ready; evict terminal/unknown ones (data-model).

        Terminal states (FAILED, EXPIRED, DELETED, DELETING) and UNKNOWN are
        never counted toward the target: they are dropped from the pool so the
        refill loop replaces them. IDLE stays (a reprovision/keepalive candidate).
        A session that can no longer be queried is treated as not ready.
        """
        ready: list[str] = []
        evicted = 0
        for session_id in session_ids:
            try:
                session = await self._controller.get_session(
                    self._agent_definition_id, session_id
                )
            except Exception:  # noqa: BLE001 - an unqueryable session is not ready
                evicted += 1
                continue
            if session.state in TERMINAL_SESSION_STATES or session.state is SessionState.UNKNOWN:
                evicted += 1
                continue
            ready.append(session_id)
        return ready, evicted

    async def acquire(self) -> str | None:
        """Take a ready session from the pool, persisting the reduced registry."""
        etag, registry = await self._load_registry()
        if not registry.tracked_session_ids:
            return None
        session_id = registry.tracked_session_ids.pop(0)
        await self._save_registry(etag, registry)
        return session_id

    def _backoff(self, attempt: int) -> float:
        raw = min(self._max_backoff, self._base_backoff * (2 ** (attempt - 1)))
        return self._rng.uniform(0.0, raw)  # full jitter

    async def _load_registry(self) -> tuple[str | None, WarmPoolRegistry]:
        try:
            record = await self._store.read(self._registry_id, self._agent_definition_id)
        except NotFound:
            return None, WarmPoolRegistry(
                agent_definition_id=self._agent_definition_id,
                target_size=self._target_size,
                strategy=WarmupStrategyKind.PRE_PROVISION_POOL,
            )
        validate_record_invariants(record)  # SEC-008: never trust returned records blindly
        return record.etag, _registry_from_payload(record.payload)

    async def _save_registry(self, etag: str | None, registry: WarmPoolRegistry) -> None:
        record = StoreRecord(
            id=self._registry_id,
            partition_key=self._agent_definition_id,
            type=WARM_POOL_REGISTRY_TYPE,
            payload=_registry_to_payload(registry),
        )
        if etag is None:
            try:
                await self._store.create(record)
                return
            except AlreadyExists:
                etag = (await self._store.read(self._registry_id, self._agent_definition_id)).etag
        await self._store.upsert(record, expected_etag=etag)

    async def start(self) -> None:
        if self._running:
            return
        self._running = True
        self._task = asyncio.create_task(self._loop())

    async def _loop(self) -> None:
        while self._running:
            await self._clock.delay(self._interval)
            if not self._running:
                break
            await self.reconcile()

    async def stop(self) -> None:
        self._running = False
        if self._task is not None:
            self._task.cancel()
            with contextlib.suppress(asyncio.CancelledError):
                await self._task
            self._task = None
