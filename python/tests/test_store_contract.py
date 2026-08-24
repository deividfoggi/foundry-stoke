"""Behavior tests for the durable store contract, run against both reference
providers (US2, CC-003). Focus is on the public interface, not internals."""

from __future__ import annotations

import pytest

from foundry_stoke import (
    AlreadyExists,
    ConcurrencyConflict,
    FileSystemStore,
    InMemoryStore,
    NotFound,
    StoreRecord,
)


@pytest.fixture(params=["in_memory", "file_system"])
def store(request: pytest.FixtureRequest, tmp_path):
    if request.param == "in_memory":
        return InMemoryStore()
    return FileSystemStore(tmp_path / "store")


def _record(id: str = "s1", partition: str = "agent-a", type: str = "tracked-session"):
    return StoreRecord(id=id, partition_key=partition, type=type, payload={"n": 1})


async def test_create_then_read_roundtrip(store):
    created = await store.create(_record())
    assert created.etag != ""

    read = await store.read("s1", "agent-a")
    assert read.id == "s1"
    assert read.partition_key == "agent-a"
    assert read.payload == {"n": 1}
    assert read.etag == created.etag


async def test_create_duplicate_raises_already_exists(store):
    await store.create(_record())
    with pytest.raises(AlreadyExists):
        await store.create(_record())


async def test_read_missing_raises_not_found(store):
    with pytest.raises(NotFound):
        await store.read("missing", "agent-a")


async def test_upsert_updates_and_rotates_etag(store):
    created = await store.create(_record())
    updated = await store.upsert(
        StoreRecord(id="s1", partition_key="agent-a", type="tracked-session", payload={"n": 2}),
        expected_etag=created.etag,
    )
    assert updated.payload == {"n": 2}
    assert updated.etag != created.etag
    assert updated.created_at == created.created_at


async def test_optimistic_concurrency_conflict(store):
    # CC-003: two writes with the same (stale) etag; first wins, second conflicts.
    created = await store.create(_record())
    await store.upsert(
        StoreRecord(id="s1", partition_key="agent-a", type="tracked-session", payload={"n": 2}),
        expected_etag=created.etag,
    )
    with pytest.raises(ConcurrencyConflict):
        await store.upsert(
            StoreRecord(id="s1", partition_key="agent-a", type="tracked-session", payload={"n": 3}),
            expected_etag=created.etag,
        )


async def test_upsert_creates_when_absent(store):
    created = await store.upsert(_record(), expected_etag=None)
    assert created.etag != ""
    assert (await store.read("s1", "agent-a")).payload == {"n": 1}


async def test_delete_removes_record(store):
    created = await store.create(_record())
    await store.delete("s1", "agent-a", expected_etag=created.etag)
    with pytest.raises(NotFound):
        await store.read("s1", "agent-a")


async def test_delete_missing_raises_not_found(store):
    with pytest.raises(NotFound):
        await store.delete("missing", "agent-a")


async def test_delete_with_stale_etag_conflicts(store):
    created = await store.create(_record())
    await store.upsert(
        StoreRecord(id="s1", partition_key="agent-a", type="tracked-session", payload={"n": 2}),
        expected_etag=created.etag,
    )
    with pytest.raises(ConcurrencyConflict):
        await store.delete("s1", "agent-a", expected_etag=created.etag)


async def test_query_by_partition_filters_by_type(store):
    await store.create(_record(id="s1", partition="agent-a", type="tracked-session"))
    await store.create(_record(id="s2", partition="agent-a", type="warm-pool-registry"))
    await store.create(_record(id="s3", partition="agent-b", type="tracked-session"))

    all_a = await store.query_by_partition("agent-a")
    assert {r.id for r in all_a} == {"s1", "s2"}

    sessions_a = await store.query_by_partition("agent-a", type_filter="tracked-session")
    assert {r.id for r in sessions_a} == {"s1"}

    assert await store.query_by_partition("missing") == []
