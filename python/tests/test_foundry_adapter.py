"""Contract tests for the real Foundry session adapter.

These lock the exact ``azure-ai-projects`` control-plane contract discovered
against the live service (azure-ai-projects 2.5.0): ``create_session`` requires a
``VersionRefIndicator`` passed as ``version_indicator``; ``list_sessions``
returns an async pager; read operations forward ids positionally. A fake client
records the calls so a future SDK drift or a regression of the original bug
(create_session called without a version) fails loudly here, without a live
project. Skipped when the ``azure`` extra is not installed (core-only CI job).
"""

from __future__ import annotations

from typing import Any

import pytest

pytest.importorskip("azure.ai.projects")

from azure.ai.projects.models import PageOrder, VersionRefIndicator  # noqa: E402

from foundry_stoke.errors import NoAgentVersionAvailable  # noqa: E402
from foundry_stoke.session.foundry_adapter import FoundrySessionOperations  # noqa: E402


class _Session:
    def __init__(self, agent_session_id: str, status: str) -> None:
        self.agent_session_id = agent_session_id
        self.status = status


class _Version:
    def __init__(self, version: str) -> None:
        self.version = version


class _AsyncPager:
    def __init__(self, items: list[Any]) -> None:
        self._items = items

    def __aiter__(self) -> _AsyncPager:
        self._it = iter(self._items)
        return self

    async def __anext__(self) -> Any:
        try:
            return next(self._it)
        except StopIteration:
            raise StopAsyncIteration from None


class _FakeAgents:
    def __init__(
        self, *, versions: list[_Version], created: _Session, sessions: list[_Session]
    ) -> None:
        self._versions = versions
        self._created = created
        self._sessions = sessions
        self.create_calls: list[tuple[str, VersionRefIndicator]] = []
        self.list_versions_calls: list[tuple[str, Any, int | None]] = []
        self.read_calls: list[tuple[str, str, str]] = []

    async def create_session(
        self, agent_name: str, *, version_indicator: VersionRefIndicator
    ) -> _Session:
        self.create_calls.append((agent_name, version_indicator))
        return self._created

    def list_versions(self, agent_name: str, *, order: Any, limit: int | None) -> _AsyncPager:
        self.list_versions_calls.append((agent_name, order, limit))
        return _AsyncPager(list(self._versions))

    def list_sessions(self, agent_name: str) -> _AsyncPager:
        return _AsyncPager(list(self._sessions))

    async def get_session(self, agent_name: str, session_id: str) -> _Session:
        self.read_calls.append(("get", agent_name, session_id))
        return self._sessions[0]

    async def stop_session(self, agent_name: str, session_id: str) -> None:
        self.read_calls.append(("stop", agent_name, session_id))

    async def delete_session(self, agent_name: str, session_id: str) -> None:
        self.read_calls.append(("delete", agent_name, session_id))


class _FakeClient:
    def __init__(self, agents: _FakeAgents) -> None:
        self.agents = agents


def _client(**kwargs: Any) -> tuple[_FakeClient, _FakeAgents]:
    agents = _FakeAgents(**kwargs)
    return _FakeClient(agents), agents


async def test_create_session_resolves_latest_version_and_passes_indicator() -> None:
    client, agents = _client(
        versions=[_Version("7")],
        created=_Session("sess-1", "ACTIVE"),
        sessions=[],
    )
    ops = FoundrySessionOperations(client)

    raw = await ops.create_session("agent-a", 900)

    assert agents.list_versions_calls == [("agent-a", PageOrder.DESC, 1)]
    assert len(agents.create_calls) == 1
    agent_name, indicator = agents.create_calls[0]
    assert agent_name == "agent-a"
    assert isinstance(indicator, VersionRefIndicator)
    assert indicator.agent_version == "7"
    assert raw.agent_session_id == "sess-1"
    assert raw.status == "ACTIVE"


async def test_create_session_uses_explicit_version_without_listing() -> None:
    client, agents = _client(
        versions=[_Version("999")],
        created=_Session("sess-2", "CREATING"),
        sessions=[],
    )
    ops = FoundrySessionOperations(client, agent_version="3")

    await ops.create_session("agent-b", 900)

    assert agents.list_versions_calls == []
    _, indicator = agents.create_calls[0]
    assert indicator.agent_version == "3"


async def test_create_session_without_any_version_raises() -> None:
    client, _ = _client(versions=[], created=_Session("x", "ACTIVE"), sessions=[])
    ops = FoundrySessionOperations(client)

    with pytest.raises(NoAgentVersionAvailable):
        await ops.create_session("agent-c", 900)


async def test_list_sessions_consumes_async_pager() -> None:
    client, _ = _client(
        versions=[],
        created=_Session("x", "ACTIVE"),
        sessions=[_Session("s1", "IDLE"), _Session("s2", "ACTIVE")],
    )
    ops = FoundrySessionOperations(client)

    raws = await ops.list_sessions("agent-d")

    assert [(r.agent_session_id, r.status) for r in raws] == [("s1", "IDLE"), ("s2", "ACTIVE")]


async def test_read_ops_forward_ids_positionally() -> None:
    client, agents = _client(
        versions=[],
        created=_Session("x", "ACTIVE"),
        sessions=[_Session("s1", "ACTIVE")],
    )
    ops = FoundrySessionOperations(client)

    await ops.get_session("agent-e", "s1")
    await ops.stop_session("agent-e", "s1")
    await ops.delete_session("agent-e", "s1")

    assert agents.read_calls == [
        ("get", "agent-e", "s1"),
        ("stop", "agent-e", "s1"),
        ("delete", "agent-e", "s1"),
    ]
