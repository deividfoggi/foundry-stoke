"""Clock abstraction (ADR 0003, contracts/clock-scheduler.md).

Hard constraint (non-blocking): ``delay`` MUST be awaitable and never block a
thread. The production clock is built on ``asyncio.sleep`` and a monotonic clock.
The virtual clock resolves scheduled delays when test code advances virtual
time, exercising idle windows (minutes) instantaneously and deterministically.
"""

from __future__ import annotations

import asyncio
import time
from typing import Protocol, runtime_checkable


@runtime_checkable
class Clock(Protocol):
    """Injectable time source used by the warm-up schedulers."""

    def now(self) -> float:
        """Return a monotonic timestamp in seconds."""
        ...

    async def delay(self, seconds: float) -> None:
        """Wait ``seconds`` without ever blocking a thread."""
        ...


class SystemClock:
    """Real clock: monotonic ``now`` and an async ``delay`` (``asyncio.sleep``)."""

    def now(self) -> float:
        return time.monotonic()

    async def delay(self, seconds: float) -> None:
        await asyncio.sleep(max(0.0, seconds))


class VirtualClock:
    """Deterministic clock for tests.

    With ``auto_advance=False`` (default), ``delay`` blocks until test code calls
    :meth:`advance`, which makes it possible to assert exactly how many probes
    fire within an idle window. With ``auto_advance=True``, ``delay`` advances
    virtual time immediately and returns, which is convenient when driving a
    single ``reconcile`` cycle whose internal backoff waits should not require an
    external driver. Neither mode performs a real sleep.
    """

    def __init__(self, start: float = 0.0, *, auto_advance: bool = False) -> None:
        self._now = start
        self._auto_advance = auto_advance
        self._waiters: list[tuple[float, asyncio.Future[None]]] = []
        self.total_delay = 0.0

    def now(self) -> float:
        return self._now

    async def delay(self, seconds: float) -> None:
        if seconds <= 0:
            await asyncio.sleep(0)
            return
        self.total_delay += seconds
        if self._auto_advance:
            self._now += seconds
            await asyncio.sleep(0)
            return
        loop = asyncio.get_running_loop()
        future: asyncio.Future[None] = loop.create_future()
        self._waiters.append((self._now + seconds, future))
        await future

    async def advance(self, seconds: float) -> None:
        """Move virtual time forward, resolving every delay that comes due."""
        target = self._now + seconds
        while True:
            due = [(t, f) for (t, f) in self._waiters if not f.done() and t <= target]
            if not due:
                break
            earliest = min(t for t, _ in due)
            self._now = earliest
            for scheduled_at, future in self._waiters:
                if not future.done() and scheduled_at <= earliest:
                    future.set_result(None)
            self._waiters = [(t, f) for (t, f) in self._waiters if not f.done()]
            # Yield so coroutines whose delay just resolved can run and schedule
            # their next delay before we continue advancing.
            await asyncio.sleep(0)
        self._now = target
