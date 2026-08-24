"""Non-blocking time abstraction for warm-up scheduling (ADR 0003).

Exposes a :class:`Clock` port whose ``delay`` is always awaitable (never blocks a
thread) plus two implementations: :class:`SystemClock` for production and
:class:`VirtualClock` for deterministic tests.
"""

from __future__ import annotations

from foundry_stoke.scheduling.clock import Clock, SystemClock, VirtualClock

__all__ = ["Clock", "SystemClock", "VirtualClock"]
