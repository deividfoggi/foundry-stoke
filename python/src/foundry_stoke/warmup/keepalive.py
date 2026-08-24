"""Keepalive warm-up strategy (US3, T043, ADR 0003).

Keeps referenced sessions from going Idle by invoking a :class:`WarmupProbe`
before the idle timeout. The loop is non-blocking and driven by an injected
:class:`~foundry_stoke.scheduling.Clock`; timing tests use a VirtualClock.
"""

from __future__ import annotations

import asyncio
import contextlib
from collections.abc import Sequence

from foundry_stoke.observability import Telemetry
from foundry_stoke.scheduling import Clock
from foundry_stoke.warmup.probe import WarmupProbe
from foundry_stoke.warmup.strategy import WarmupReport


class KeepaliveStrategy:
    """Probe referenced sessions periodically, within their idle window."""

    def __init__(
        self,
        *,
        probe: WarmupProbe,
        clock: Clock,
        interval_seconds: float,
        agent_definition_id: str,
        session_ids: Sequence[str],
        telemetry: Telemetry | None = None,
    ) -> None:
        self._probe = probe
        self._clock = clock
        self._interval = interval_seconds
        self._agent_definition_id = agent_definition_id
        self._session_ids = list(session_ids)
        self._telemetry = telemetry or Telemetry()
        self._running = False
        self._task: asyncio.Task[None] | None = None

    async def reconcile(self) -> WarmupReport:
        probed = 0
        failures = 0
        for session_id in list(self._session_ids):
            try:
                result = await self._probe.probe(self._agent_definition_id, session_id)
            except Exception as exc:  # noqa: BLE001 - a failing probe must not stop the loop
                failures += 1
                self._telemetry.record_exception(
                    "stoke.warmup.probe", exc, agent_definition_id=self._agent_definition_id
                )
                continue
            probed += 1
            if not result.ok:
                failures += 1
            self._telemetry.emit(
                "stoke.warmup.probe",
                {
                    "stoke.agent_definition_id": self._agent_definition_id,
                    "stoke.agent_session_id": session_id,
                    "stoke.warmup.strategy": "keepalive",
                    "stoke.probe.ok": result.ok,
                },
            )
        return WarmupReport(
            strategy="keepalive",
            reconciled_at=self._clock.now(),
            probed=probed,
            failures=failures,
        )

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
