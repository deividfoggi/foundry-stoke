"""Security-control tests for the FileSystem store provider (SEC-001/002/006)."""

from __future__ import annotations

import json

import pytest

from foundry_stoke import (
    CorruptedRecord,
    FileSystemStore,
    InvalidRecordKey,
    LockTimeout,
    StoreRecord,
    UnknownRecordType,
)
from foundry_stoke.store.file_system import _HAS_FCNTL, MAX_KEY_LENGTH, _hash


def _record(id: str = "s1", partition: str = "agent-a", type: str = "tracked-session"):
    return StoreRecord(id=id, partition_key=partition, type=type, payload={"n": 1})


# --- SEC-001 path sanitization ---


async def test_empty_key_rejected(tmp_path):
    store = FileSystemStore(tmp_path)
    with pytest.raises(InvalidRecordKey):
        await store.create(_record(id=""))
    with pytest.raises(InvalidRecordKey):
        await store.create(_record(partition=""))


async def test_oversized_key_rejected(tmp_path):
    store = FileSystemStore(tmp_path)
    with pytest.raises(InvalidRecordKey):
        await store.create(_record(id="x" * (MAX_KEY_LENGTH + 1)))


async def test_path_traversal_keys_are_confined(tmp_path):
    # A traversal-looking key must not escape the base dir; it is hashed and
    # stored safely and reads back intact.
    store = FileSystemStore(tmp_path)
    await store.create(_record(id="../../evil", partition="../../secret"))
    read = await store.read("../../evil", "../../secret")
    assert read.payload == {"n": 1}

    files_outside_base = [
        p for p in tmp_path.parent.rglob("evil*") if tmp_path not in p.parents and p != tmp_path
    ]
    assert files_outside_base == []


# --- SEC-002 schema-safe deserialization ---


async def test_corrupted_file_raises_typed_error(tmp_path):
    store = FileSystemStore(tmp_path)
    await store.create(_record())
    record_path = store._record_path("s1", "agent-a")
    record_path.write_bytes(b"{ this is not valid json")
    with pytest.raises(CorruptedRecord):
        await store.read("s1", "agent-a")


async def test_partial_record_raises_typed_error(tmp_path):
    store = FileSystemStore(tmp_path)
    partition_dir = tmp_path / _hash("agent-a")
    partition_dir.mkdir(parents=True)
    # Valid JSON, allowed type, but missing required fields.
    (partition_dir / f"{_hash('s1')}.json").write_text(
        json.dumps({"type": "tracked-session", "id": "s1"})
    )
    with pytest.raises(CorruptedRecord):
        await store.read("s1", "agent-a")


async def test_unknown_type_on_write_rejected(tmp_path):
    store = FileSystemStore(tmp_path)
    with pytest.raises(UnknownRecordType):
        await store.create(_record(type="malicious-type"))


async def test_unknown_type_on_read_rejected(tmp_path):
    store = FileSystemStore(tmp_path)
    partition_dir = tmp_path / _hash("agent-a")
    partition_dir.mkdir(parents=True)
    (partition_dir / f"{_hash('s1')}.json").write_text(
        json.dumps(
            {
                "id": "s1",
                "partition_key": "agent-a",
                "type": "malicious-type",
                "payload": {},
                "etag": "e1",
                "created_at": "2026-08-21T00:00:00+00:00",
                "updated_at": "2026-08-21T00:00:00+00:00",
            }
        )
    )
    with pytest.raises(UnknownRecordType):
        await store.read("s1", "agent-a")


async def test_oversized_file_rejected(tmp_path):
    store = FileSystemStore(tmp_path, max_file_bytes=64)
    with pytest.raises(CorruptedRecord):
        await store.create(
            StoreRecord(
                id="s1",
                partition_key="agent-a",
                type="tracked-session",
                payload={"blob": "x" * 500},
            )
        )


# --- SEC-006 cross-process lock with acquisition timeout ---


@pytest.mark.skipif(not _HAS_FCNTL, reason="POSIX advisory locks required")
async def test_lock_acquisition_timeout(tmp_path):
    import fcntl
    import os

    store = FileSystemStore(tmp_path, lock_timeout_seconds=0.2)
    record_path = store._record_path("s1", "agent-a")
    record_path.parent.mkdir(parents=True, exist_ok=True)
    lock_path = record_path.with_suffix(".json.lock")

    holder = os.open(lock_path, os.O_CREAT | os.O_RDWR, 0o600)
    fcntl.flock(holder, fcntl.LOCK_EX)
    try:
        with pytest.raises(LockTimeout):
            await store.create(_record())
    finally:
        fcntl.flock(holder, fcntl.LOCK_UN)
        os.close(holder)
