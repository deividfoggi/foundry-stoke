"""In-memory durable store provider (US2 tracer bullet).

For dev and tests. Does not persist across processes. Implements optimistic
concurrency by etag over an in-memory index.
"""

from __future__ import annotations

import asyncio
import copy
import uuid
from datetime import datetime, timezone

from foundry_stoke.errors import (
    AlreadyExists,
    ConcurrencyConflict,
    NotFound,
)
from foundry_stoke.models import StoreRecord


def _new_etag() -> str:
    return uuid.uuid4().hex


def _utcnow() -> datetime:
    return datetime.now(timezone.utc)


class InMemoryStore:
    """Reference :class:`DurableStoreProvider` backed by a dict."""

    def __init__(self) -> None:
        # Keyed by (partition_key, id) -> stored copy of the record.
        self._records: dict[tuple[str, str], StoreRecord] = {}
        self._lock = asyncio.Lock()

    async def create(self, record: StoreRecord) -> StoreRecord:
        key = (record.partition_key, record.id)
        async with self._lock:
            if key in self._records:
                raise AlreadyExists(f"record already exists for id={record.id!r} in partition")
            stored = copy.deepcopy(record)
            stored.etag = _new_etag()
            now = _utcnow()
            stored.created_at = now
            stored.updated_at = now
            self._records[key] = stored
            return copy.deepcopy(stored)

    async def read(self, id: str, partition_key: str) -> StoreRecord:
        async with self._lock:
            existing = self._records.get((partition_key, id))
            if existing is None:
                raise NotFound(f"no record for id={id!r} in partition")
            return copy.deepcopy(existing)

    async def upsert(self, record: StoreRecord, expected_etag: str | None) -> StoreRecord:
        key = (record.partition_key, record.id)
        async with self._lock:
            existing = self._records.get(key)
            if existing is not None and expected_etag != existing.etag:
                raise ConcurrencyConflict(
                    f"etag mismatch for id={record.id!r}: record was modified"
                )
            stored = copy.deepcopy(record)
            stored.etag = _new_etag()
            now = _utcnow()
            stored.created_at = existing.created_at if existing is not None else now
            stored.updated_at = now
            self._records[key] = stored
            return copy.deepcopy(stored)

    async def delete(self, id: str, partition_key: str, expected_etag: str | None = None) -> None:
        key = (partition_key, id)
        async with self._lock:
            existing = self._records.get(key)
            if existing is None:
                raise NotFound(f"no record for id={id!r} in partition")
            if expected_etag is not None and expected_etag != existing.etag:
                raise ConcurrencyConflict(f"etag mismatch for id={id!r}: record was modified")
            del self._records[key]

    async def query_by_partition(
        self, partition_key: str, type_filter: str | None = None
    ) -> list[StoreRecord]:
        async with self._lock:
            return [
                copy.deepcopy(record)
                for (pk, _id), record in self._records.items()
                if pk == partition_key and (type_filter is None or record.type == type_filter)
            ]
