"""Session lifecycle controller (US1, contracts/session-controller.md).

Encapsulates the Foundry ``/sessions`` control-plane operations behind a
protocol-agnostic surface (ADR 0002). The confirmed operations (create, get,
list, stop, delete) are reached through a :class:`SessionOperations` port so the
real ``azure-ai-projects`` adapter and a test fake are interchangeable.

The status enum is the official ``AgentSessionStatus`` taxonomy (eight lowercase
values) plus an ``UNKNOWN`` fallback, mapped case-insensitively.

``Resumed`` is not a status nor an explicit operation: it is the effect of
referencing an idle session again. The controller derives it in ``get_session``
by remembering the last state it observed per session and setting ``resumed_at``
when a session previously seen ``idle`` is now observed ``active`` (FR-003).
"""

from __future__ import annotations

from collections.abc import Callable
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Protocol, runtime_checkable

from foundry_stoke.errors import InvalidIdleTimeout, SessionClosed
from foundry_stoke.models import SessionOrigin, SessionState, TrackedSession

MIN_IDLE_TIMEOUT_SECONDS = 300
MAX_IDLE_TIMEOUT_SECONDS = 3600
DEFAULT_IDLE_TIMEOUT_SECONDS = 900


def _utcnow() -> datetime:
    return datetime.now(timezone.utc)


@dataclass
class RawSession:
    """Untranslated session view returned by the control-plane adapter.

    ``status`` is the raw string as returned by the platform; it is mapped to
    :class:`SessionState` by an injectable translator (case-insensitive over the
    official ``AgentSessionStatus`` taxonomy).
    """

    agent_session_id: str
    status: str


@runtime_checkable
class SessionOperations(Protocol):
    """Port for the confirmed Foundry ``/sessions`` control-plane operations."""

    async def create_session(
        self, agent_definition_id: str, idle_timeout_seconds: int
    ) -> RawSession: ...

    async def get_session(self, agent_definition_id: str, agent_session_id: str) -> RawSession: ...

    async def list_sessions(self, agent_definition_id: str) -> list[RawSession]: ...

    async def stop_session(self, agent_definition_id: str, agent_session_id: str) -> None: ...

    async def delete_session(self, agent_definition_id: str, agent_session_id: str) -> None: ...


# Maps the official ``AgentSessionStatus`` values case-insensitively. Any
# unrecognized or future value maps to SessionState.UNKNOWN, never coerced to
# another status (FR-002, CC-008); inject a custom translator to override.
StatusTranslator = Callable[[str], SessionState]

_OFFICIAL_STATUS_BY_VALUE: dict[str, SessionState] = {
    state.value: state for state in SessionState if state is not SessionState.UNKNOWN
}


def default_status_translator(status: str) -> SessionState:
    return _OFFICIAL_STATUS_BY_VALUE.get(status.strip().lower(), SessionState.UNKNOWN)


class SessionController:
    """Control-plane session lifecycle over a :class:`SessionOperations` port."""

    def __init__(
        self,
        operations: SessionOperations,
        *,
        status_translator: StatusTranslator = default_status_translator,
    ) -> None:
        self._ops = operations
        self._translate = status_translator
        # Sessions deleted in this controller's lifetime; subsequent operations
        # on them return SessionClosed deterministically (FR-005).
        self._closed: set[tuple[str, str]] = set()
        # Last state observed per session; used to derive the resume marker
        # (idle -> active) since "resumed" is not a first-class status (FR-003).
        self._last_state: dict[tuple[str, str], SessionState] = {}

    async def create_session(
        self,
        agent_definition_id: str,
        idle_timeout_seconds: int = DEFAULT_IDLE_TIMEOUT_SECONDS,
    ) -> TrackedSession:
        if not (MIN_IDLE_TIMEOUT_SECONDS <= idle_timeout_seconds <= MAX_IDLE_TIMEOUT_SECONDS):
            raise InvalidIdleTimeout(
                "idle_timeout_seconds must be within "
                f"{MIN_IDLE_TIMEOUT_SECONDS}..{MAX_IDLE_TIMEOUT_SECONDS} "
                f"(got {idle_timeout_seconds})"
            )
        raw = await self._ops.create_session(agent_definition_id, idle_timeout_seconds)
        now = _utcnow()
        state = self._translate(raw.status)
        self._last_state[(agent_definition_id, raw.agent_session_id)] = state
        return TrackedSession(
            agent_session_id=raw.agent_session_id,
            agent_definition_id=agent_definition_id,
            state=state,
            idle_timeout_seconds=idle_timeout_seconds,
            last_activity_at=now,
            created_at=now,
            origin=SessionOrigin.ON_DEMAND,
        )

    async def get_session(
        self,
        agent_definition_id: str,
        agent_session_id: str,
        idle_timeout_seconds: int = DEFAULT_IDLE_TIMEOUT_SECONDS,
    ) -> TrackedSession:
        self._ensure_open(agent_definition_id, agent_session_id)
        raw = await self._ops.get_session(agent_definition_id, agent_session_id)
        state = self._translate(raw.status)
        now = _utcnow()
        key = (agent_definition_id, agent_session_id)
        # Derived resume: a session previously seen idle, now active again.
        resumed = self._last_state.get(key) is SessionState.IDLE and state is SessionState.ACTIVE
        self._last_state[key] = state
        return TrackedSession(
            agent_session_id=raw.agent_session_id,
            agent_definition_id=agent_definition_id,
            state=state,
            idle_timeout_seconds=idle_timeout_seconds,
            last_activity_at=now,
            resumed_at=now if resumed else None,
        )

    async def list_sessions(self, agent_definition_id: str) -> list[TrackedSession]:
        raws = await self._ops.list_sessions(agent_definition_id)
        now = _utcnow()
        return [
            TrackedSession(
                agent_session_id=raw.agent_session_id,
                agent_definition_id=agent_definition_id,
                state=self._translate(raw.status),
                idle_timeout_seconds=DEFAULT_IDLE_TIMEOUT_SECONDS,
                last_activity_at=now,
            )
            for raw in raws
        ]

    async def stop_session(self, agent_definition_id: str, agent_session_id: str) -> None:
        self._ensure_open(agent_definition_id, agent_session_id)
        await self._ops.stop_session(agent_definition_id, agent_session_id)

    async def delete_session(self, agent_definition_id: str, agent_session_id: str) -> None:
        self._ensure_open(agent_definition_id, agent_session_id)
        await self._ops.delete_session(agent_definition_id, agent_session_id)
        self._closed.add((agent_definition_id, agent_session_id))

    def _ensure_open(self, agent_definition_id: str, agent_session_id: str) -> None:
        if (agent_definition_id, agent_session_id) in self._closed:
            raise SessionClosed("session has been deleted and no longer accepts operations")
