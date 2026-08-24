"""Real Foundry control-plane adapter for :class:`SessionOperations`.

Implements the confirmed ``azure-ai-projects`` control-plane operations
(research.md: create/get/list/stop/delete are confirmed for Python). The SDK is
imported lazily so the core package and its tests do not require
``azure-ai-projects`` to be installed. This adapter is the real seam behind
:class:`~foundry_stoke.session.controller.SessionController`; it is exercised
against a live project, not in unit tests.

Research gaps (research.md), isolated here rather than invented:
- The exact response attribute names beyond ``agent_session_id`` and ``status``
  are not documented; access is defensive.
- The status enum strings are unconfirmed and handled by the controller's
  injectable translator, not here.
"""

from __future__ import annotations

from typing import TYPE_CHECKING, Any

from foundry_stoke.session.controller import RawSession

if TYPE_CHECKING:  # pragma: no cover - typing only
    pass


def _extract_raw(session: Any) -> RawSession:
    return RawSession(
        agent_session_id=str(session.agent_session_id),
        status=str(session.status),
    )


class FoundrySessionOperations:
    """Adapter over ``AIProjectClient.agents`` session operations.

    ``project_client`` is an ``azure.ai.projects.aio.AIProjectClient`` (or a
    compatible object exposing ``agents``). It is injected so the credential and
    endpoint wiring stay in the caller's control (ADR 0005: the same credential
    resolved by the CredentialProvider is reused here).
    """

    def __init__(self, project_client: Any) -> None:
        self._client = project_client

    async def create_session(
        self, agent_definition_id: str, idle_timeout_seconds: int
    ) -> RawSession:
        # idle_timeout is configured at agent-version creation time, not per
        # create_session (research.md); it is validated by the controller and
        # carried on the TrackedSession, not passed to this call.
        session = await self._client.agents.create_session(agent_definition_id)
        return _extract_raw(session)

    async def get_session(self, agent_definition_id: str, agent_session_id: str) -> RawSession:
        session = await self._client.agents.get_session(agent_definition_id, agent_session_id)
        return _extract_raw(session)

    async def list_sessions(self, agent_definition_id: str) -> list[RawSession]:
        sessions = await self._client.agents.list_sessions(agent_definition_id)
        return [_extract_raw(s) for s in sessions]

    async def stop_session(self, agent_definition_id: str, agent_session_id: str) -> None:
        await self._client.agents.stop_session(agent_definition_id, agent_session_id)

    async def delete_session(self, agent_definition_id: str, agent_session_id: str) -> None:
        await self._client.agents.delete_session(agent_definition_id, agent_session_id)
