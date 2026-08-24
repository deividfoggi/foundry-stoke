"""Durable store provider abstraction (ADR 0001, contracts/durable-store-provider.md).

Technology-agnostic interface for persisting :class:`StoreRecord`. The core must
not depend on any concrete store SDK (FR-011, CC-004). Reference providers
(InMemory, FileSystem) live alongside this module; production stores (e.g.
Cosmos) implement the same protocol without changing callers (FR-009).

Semantics: minimal CRUD plus query-by-partition, with optimistic concurrency by
etag. Methods are async so I/O-backed providers stay non-blocking; the in-memory
provider satisfies the same async surface trivially.
"""

from __future__ import annotations

from typing import Protocol, runtime_checkable

from foundry_stoke.errors import InvalidRecordKey, UnknownRecordType
from foundry_stoke.models import KNOWN_RECORD_TYPES, StoreRecord


def validate_record_invariants(record: StoreRecord) -> StoreRecord:
    """Validate basic invariants of a record returned by a pluggable provider.

    SEC-008 (ADR 0007): store providers run in-process with full trust and are
    not sandboxed, but Stoke does not trust returned records blindly. It rejects
    records with an empty ``id``/``partition_key`` or a ``type`` outside the
    allowlist, surfacing a typed error instead of accepting malformed state. The
    optimistic-concurrency guarantee still depends on the provider honoring the
    etag; that responsibility is documented in the provider contract.
    """
    if not record.id or not record.partition_key:
        raise InvalidRecordKey("record id and partition_key must be non-empty")
    if record.type not in KNOWN_RECORD_TYPES:
        raise UnknownRecordType(f"record type {record.type!r} is not in the allowlist")
    return record


@runtime_checkable
class DurableStoreProvider(Protocol):
    """Pluggable persistence port for Stoke's own state."""

    async def create(self, record: StoreRecord) -> StoreRecord:
        """Create a new record.

        Raises :class:`AlreadyExists` if (id, partition_key) already exists.
        Returns the record with its initial etag assigned.
        """
        ...

    async def read(self, id: str, partition_key: str) -> StoreRecord:
        """Read by composite key. Raises :class:`NotFound` if absent."""
        ...

    async def upsert(self, record: StoreRecord, expected_etag: str | None) -> StoreRecord:
        """Create or update a record.

        If the record exists, ``expected_etag`` must match the current etag or a
        :class:`ConcurrencyConflict` is raised (CC-003). ``expected_etag`` is
        only valid as ``None`` when creating a new record. Returns the record
        with a new etag.
        """
        ...

    async def delete(self, id: str, partition_key: str, expected_etag: str | None = None) -> None:
        """Delete by composite key.

        Raises :class:`NotFound` if absent, or :class:`ConcurrencyConflict` if
        ``expected_etag`` is provided and stale.
        """
        ...

    async def query_by_partition(
        self, partition_key: str, type_filter: str | None = None
    ) -> list[StoreRecord]:
        """List records in a partition, optionally filtered by ``type``.

        This is deliberately not an arbitrary query language, to avoid coupling
        to a backend (ADR 0001).
        """
        ...
