"""Warm-up strategy port and report (US3, ADR 0003, contracts/warmup-strategy.md).

A strategy runs a non-blocking reconciliation loop driven by an injected
:class:`~foundry_stoke.scheduling.Clock`. ``reconcile`` performs a single cycle
and is directly testable; ``start``/``stop`` manage the background loop.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Protocol, runtime_checkable


@dataclass
class WarmupReport:
    """Outcome of a single reconciliation cycle.

    Fields not relevant to a given strategy stay at zero (e.g. a keepalive cycle
    reports ``probed`` but not ``created``).
    """

    strategy: str
    reconciled_at: float
    ready: int = 0
    created: int = 0
    probed: int = 0
    failures: int = 0
    evicted: int = 0


@runtime_checkable
class WarmupStrategy(Protocol):
    """User-selectable warm-up strategy."""

    async def reconcile(self) -> WarmupReport:
        """Run one reconciliation cycle and return its report."""
        ...

    async def start(self) -> None:
        """Start the non-blocking reconciliation loop."""
        ...

    async def stop(self) -> None:
        """Stop the loop cooperatively."""
        ...
