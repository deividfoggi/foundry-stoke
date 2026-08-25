"""Real Foundry control-plane adapter for :class:`SessionOperations`.

Implements the ``azure-ai-projects`` control-plane session operations verified
against the live ``AgentsOperations`` surface (azure-ai-projects 2.5.0):
``create_session`` (requires a ``version_indicator``), ``get_session``,
``list_sessions``, ``stop_session``, ``delete_session``. The SDK is imported
lazily so the core package and its tests do not require ``azure-ai-projects``.
This adapter is the real seam behind
:class:`~foundry_stoke.session.controller.SessionController`.

The read operations map the controller's ``agent_definition_id`` to the SDK's
``agent_name`` and ``agent_session_id`` to ``session_id`` positionally.
``AgentSessionResource`` exposes ``agent_session_id`` and ``status`` (the eight
official ``AgentSessionStatus`` values), consumed by ``_extract_raw`` and mapped
by the controller's injectable translator.
"""

from __future__ import annotations

from typing import Any

from foundry_stoke.errors import NoAgentVersionAvailable
from foundry_stoke.session.controller import RawSession


def _extract_raw(session: Any) -> RawSession:
    # AgentSessionStatus is an enum whose str() is "AgentSessionStatus.ACTIVE";
    # read its .value ("active") so the controller's translator recognizes it.
    status = getattr(session.status, "value", session.status)
    return RawSession(
        agent_session_id=str(session.agent_session_id),
        status=str(status),
    )


class FoundrySessionOperations:
    """Adapter over ``AIProjectClient.agents`` session operations.

    ``project_client`` is an ``azure.ai.projects.aio.AIProjectClient`` (or a
    compatible object exposing ``agents``). It is injected so the credential and
    endpoint wiring stay in the caller's control (ADR 0005: the same credential
    resolved by the CredentialProvider is reused here).

    ``agent_version`` pins the agent version that backs created sessions. When
    omitted, the latest published version is resolved per agent at create time,
    since Foundry requires a ``version_indicator`` and the SDK exposes no
    "latest" indicator.
    """

    def __init__(self, project_client: Any, *, agent_version: str | None = None) -> None:
        self._client = project_client
        self._agent_version = agent_version

    async def create_session(
        self, agent_definition_id: str, idle_timeout_seconds: int
    ) -> RawSession:
        # idle_timeout is configured at agent-version creation time, not per
        # create_session; it is validated by the controller and carried on the
        # TrackedSession, not passed to this call.
        from azure.ai.projects.models import VersionRefIndicator

        version = self._agent_version or await self._latest_version(agent_definition_id)
        indicator = VersionRefIndicator(agent_version=version)
        session = await self._client.agents.create_session(
            agent_definition_id, version_indicator=indicator
        )
        return _extract_raw(session)

    async def _latest_version(self, agent_name: str) -> str:
        from azure.ai.projects.models import PageOrder

        pager = self._client.agents.list_versions(agent_name, order=PageOrder.DESC, limit=1)
        async for version in pager:
            return str(version.version)
        raise NoAgentVersionAvailable(
            f"agent {agent_name!r} has no published version to create a session against"
        )

    async def get_session(self, agent_definition_id: str, agent_session_id: str) -> RawSession:
        session = await self._client.agents.get_session(agent_definition_id, agent_session_id)
        return _extract_raw(session)

    async def list_sessions(self, agent_definition_id: str) -> list[RawSession]:
        # list_sessions returns an AsyncItemPaged (not a coroutine); iterate it.
        pager = self._client.agents.list_sessions(agent_definition_id)
        return [_extract_raw(session) async for session in pager]

    async def stop_session(self, agent_definition_id: str, agent_session_id: str) -> None:
        await self._client.agents.stop_session(agent_definition_id, agent_session_id)

    async def delete_session(self, agent_definition_id: str, agent_session_id: str) -> None:
        await self._client.agents.delete_session(agent_definition_id, agent_session_id)
