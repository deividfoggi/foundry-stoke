"""Behavior tests for the session lifecycle controller (US1, CC-001/CC-002, FR-005)."""

from __future__ import annotations

import pytest

from foundry_stoke import (
    InvalidIdleTimeout,
    RawSession,
    SessionClosed,
    SessionController,
    SessionState,
)


class FakeSessionOperations:
    """In-memory fake of the confirmed Foundry control-plane operations."""

    def __init__(self) -> None:
        self.status = "active"
        self.created: list[str] = []
        self.stopped: list[str] = []
        self.deleted: list[str] = []
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
        self.stopped.append(agent_session_id)

    async def delete_session(self, agent_definition_id: str, agent_session_id: str) -> None:
        self.deleted.append(agent_session_id)


async def test_happy_lifecycle_active_idle_resumed():
    # CC-001: open a session (Active), observe Idle, reference again (Resumed).
    ops = FakeSessionOperations()
    controller = SessionController(ops)

    created = await controller.create_session("agent-a")
    assert created.agent_session_id == "sess-1"
    assert created.state is SessionState.ACTIVE
    assert created.idle_timeout_seconds == 900

    ops.status = "idle"
    idle = await controller.get_session("agent-a", "sess-1")
    assert idle.state is SessionState.IDLE

    ops.status = "resumed"
    resumed = await controller.get_session("agent-a", "sess-1")
    assert resumed.state is SessionState.RESUMED
    assert resumed.resumed_at is not None


async def test_invalid_idle_timeout_rejected():
    # CC-002: 120 minutes is outside the supported 5-60 minute range.
    controller = SessionController(FakeSessionOperations())
    with pytest.raises(InvalidIdleTimeout):
        await controller.create_session("agent-a", idle_timeout_seconds=7200)
    with pytest.raises(InvalidIdleTimeout):
        await controller.create_session("agent-a", idle_timeout_seconds=60)


async def test_idle_timeout_boundaries_accepted():
    controller = SessionController(FakeSessionOperations())
    low = await controller.create_session("agent-a", idle_timeout_seconds=300)
    high = await controller.create_session("agent-a", idle_timeout_seconds=3600)
    assert low.idle_timeout_seconds == 300
    assert high.idle_timeout_seconds == 3600


async def test_stop_is_delegated():
    ops = FakeSessionOperations()
    controller = SessionController(ops)
    await controller.create_session("agent-a")
    await controller.stop_session("agent-a", "sess-1")
    assert ops.stopped == ["sess-1"]


async def test_deleted_session_rejects_further_operations():
    # FR-005: operations on a deleted session raise a deterministic error.
    ops = FakeSessionOperations()
    controller = SessionController(ops)
    await controller.create_session("agent-a")
    await controller.delete_session("agent-a", "sess-1")

    with pytest.raises(SessionClosed):
        await controller.get_session("agent-a", "sess-1")
    with pytest.raises(SessionClosed):
        await controller.stop_session("agent-a", "sess-1")
    with pytest.raises(SessionClosed):
        await controller.delete_session("agent-a", "sess-1")


async def test_custom_status_translator_overrides_default():
    ops = FakeSessionOperations()
    ops.status = "RUNNING"
    controller = SessionController(
        ops,
        status_translator=lambda s: SessionState.ACTIVE if s == "RUNNING" else SessionState.IDLE,
    )
    created = await controller.create_session("agent-a")
    assert created.state is SessionState.ACTIVE


async def test_list_sessions_maps_state():
    ops = FakeSessionOperations()
    controller = SessionController(ops)
    await controller.create_session("agent-a")
    await controller.create_session("agent-a")
    sessions = await controller.list_sessions("agent-a")
    assert {s.agent_session_id for s in sessions} == {"sess-1", "sess-2"}
    assert all(s.state is SessionState.ACTIVE for s in sessions)
