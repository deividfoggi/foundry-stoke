"""Configuration facade tests (StokeOptions + Stoke.build/from_env).

The facade is a convenience layer over pure dependency injection: it validates
the Foundry endpoint (SEC-010), wires the credential provider, store and session
controller in one place, without ever hardcoding secrets.
"""

from __future__ import annotations

import pytest

from foundry_stoke import (
    ConfigurationError,
    InMemoryStore,
    InvalidEndpoint,
    RawSession,
    SessionController,
    Stoke,
    StokeOptions,
)


class FakeSessionOperations:
    def __init__(self) -> None:
        self._counter = 0

    async def create_session(self, agent_definition_id: str, idle_timeout_seconds: int):
        self._counter += 1
        return RawSession(agent_session_id=f"sess-{self._counter}", status="active")

    async def get_session(self, agent_definition_id: str, agent_session_id: str):
        return RawSession(agent_session_id=agent_session_id, status="active")

    async def list_sessions(self, agent_definition_id: str):
        return []

    async def stop_session(self, agent_definition_id: str, agent_session_id: str) -> None:
        return None

    async def delete_session(self, agent_definition_id: str, agent_session_id: str) -> None:
        return None


async def test_build_wires_components_with_injected_operations():
    options = StokeOptions(project_endpoint="https://proj.example.com")
    stoke = Stoke.build(options, session_operations=FakeSessionOperations())

    assert isinstance(stoke.sessions, SessionController)
    assert isinstance(stoke.store, InMemoryStore)

    session = await stoke.sessions.create_session("agent-a")
    assert session.agent_session_id == "sess-1"


def test_build_rejects_non_https_endpoint():
    with pytest.raises(InvalidEndpoint):
        Stoke.build(StokeOptions(project_endpoint="http://proj.example.com"))


def test_build_rejects_unexpected_host():
    options = StokeOptions(
        project_endpoint="https://evil.example.com",
        expected_host="proj.example.com",
    )
    with pytest.raises(InvalidEndpoint):
        Stoke.build(options)


def test_from_env_reads_endpoint():
    stoke = Stoke.from_env(
        environ={"FOUNDRY_PROJECT_ENDPOINT": "https://proj.example.com"},
        session_operations=FakeSessionOperations(),
    )
    assert stoke.options.project_endpoint == "https://proj.example.com"


def test_from_env_missing_endpoint_raises():
    with pytest.raises(ConfigurationError):
        Stoke.from_env(environ={})
