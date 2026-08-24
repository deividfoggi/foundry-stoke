"""Warm-up strategies and probes (US3/US4, ADR 0003)."""

from __future__ import annotations

from foundry_stoke.warmup.keepalive import KeepaliveStrategy
from foundry_stoke.warmup.pool import DEFAULT_MAX_TARGET_SIZE, PreProvisionPoolStrategy
from foundry_stoke.warmup.probe import (
    CallableProbe,
    ProbeResult,
    ResponsesPingProbe,
    WarmupProbe,
)
from foundry_stoke.warmup.strategy import WarmupReport, WarmupStrategy

__all__ = [
    "WarmupStrategy",
    "WarmupReport",
    "PreProvisionPoolStrategy",
    "KeepaliveStrategy",
    "DEFAULT_MAX_TARGET_SIZE",
    "WarmupProbe",
    "ProbeResult",
    "ResponsesPingProbe",
    "CallableProbe",
]
