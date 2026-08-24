"""Behavior tests for the non-blocking Clock abstraction (T014, ADR 0003).

All warm-up timing is driven through this abstraction; these tests assert the
VirtualClock advances deterministically without any real sleep.
"""

from __future__ import annotations

import asyncio

from foundry_stoke.scheduling import SystemClock, VirtualClock


async def test_system_clock_now_is_monotonic_and_delay_nonblocking():
    clock = SystemClock()
    t0 = clock.now()
    await clock.delay(0)  # zero delay must return promptly, never block a thread
    assert clock.now() >= t0


async def test_virtual_clock_delay_resolves_only_on_advance():
    clock = VirtualClock()
    fired: list[float] = []

    async def waiter() -> None:
        await clock.delay(300)
        fired.append(clock.now())

    task = asyncio.create_task(waiter())
    await asyncio.sleep(0)
    assert fired == []  # not due yet

    await clock.advance(299)
    assert fired == []

    await clock.advance(1)
    await task
    assert fired == [300.0]


async def test_virtual_clock_auto_advance_tracks_virtual_time():
    clock = VirtualClock(auto_advance=True)
    await clock.delay(60)
    assert clock.now() == 60.0
    assert clock.total_delay == 60.0
