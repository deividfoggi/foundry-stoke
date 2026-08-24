"""Warm-up probe port and implementations (US4, ADR 0003/0007, warmup-probe.md).

A probe generates minimal activity within the idle window to keep a session
usable, without Stoke embedding a general-purpose data-plane client (ADR 0002).
Two sources: the built-in generic Responses ping (optional) and a user-supplied
callable for Invocations/custom containers.
"""

from __future__ import annotations

from collections.abc import Awaitable, Callable
from dataclasses import dataclass
from typing import Any, Protocol, runtime_checkable

from foundry_stoke.endpoints import validate_endpoint


@dataclass
class ProbeResult:
    """Outcome of a single probe call."""

    ok: bool
    latency_seconds: float
    error: str | None = None


@runtime_checkable
class WarmupProbe(Protocol):
    """Port for keepalive probes."""

    async def probe(self, agent_definition_id: str, agent_session_id: str) -> ProbeResult:
        """Run a minimal activity against the session to renew its idle timer."""
        ...


class ResponsesPingProbe:
    """Built-in generic Responses ping probe (optional).

    Isolated behind an adapter like the session operations: the OpenAI client is
    injected, and the target endpoint is validated (https + expected host) and
    taken only from trusted config (SEC-010). Applicable only to Responses-
    compatible agents; Invocations/custom containers require a user probe.
    """

    def __init__(
        self,
        openai_client: Any,
        *,
        endpoint: str,
        expected_host: str | None = None,
    ) -> None:
        # SEC-010: reject non-https / unexpected-host endpoints before any use.
        validate_endpoint(endpoint, expected_host=expected_host)
        self._client = openai_client
        self._endpoint = endpoint

    async def probe(self, agent_definition_id: str, agent_session_id: str) -> ProbeResult:
        # Research gap (research.md): the exact minimal Responses payload that
        # counts as keepalive activity and resets the idle timer is not
        # documented. The generic ping is the only built-in; its shape is
        # isolated here behind the adapter rather than invented across the code.
        try:
            await self._client.responses.create(
                extra_body={"agent_session_id": agent_session_id},
                input="ping",
            )
        except Exception as exc:  # noqa: BLE001 - reported via ProbeResult, not raised
            return ProbeResult(ok=False, latency_seconds=0.0, error=type(exc).__name__)
        return ProbeResult(ok=True, latency_seconds=0.0)


class CallableProbe:
    """Adapter over a user-supplied probe callable.

    Used for Invocations/custom containers whose schema is defined by the user;
    Stoke never attaches credentials when invoking it and passes only the
    ``agent_definition_id``/``agent_session_id`` (SEC-010, ADR 0007).
    """

    def __init__(
        self,
        callback: Callable[[str, str], Awaitable[ProbeResult]],
    ) -> None:
        self._callback = callback

    async def probe(self, agent_definition_id: str, agent_session_id: str) -> ProbeResult:
        return await self._callback(agent_definition_id, agent_session_id)
