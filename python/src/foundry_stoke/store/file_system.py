"""FileSystem (JSON) durable store provider (US2).

Persists each :class:`StoreRecord` as a JSON file on disk, one directory per
partition. Intended for local development, not production: the cross-process
advisory lock is not guaranteed on network filesystems (NFS/SMB) (ADR 0001).

Security controls (security-review-architecture.md):

- SEC-001 path sanitization: file and directory names are SHA-256 hex digests of
  the id/partition key, confined to the base directory via canonical-path
  validation; empty or oversized keys are rejected.
- SEC-002 schema-safe deserialization: JSON only (never pickle/eval); the record
  ``type`` must be in an allowlist; corrupted/partial/oversized files raise a
  typed error.
- SEC-006 concurrency: the full read-check-etag-write cycle runs under a
  cross-process advisory file lock (``fcntl.flock``) with an acquisition timeout.

All blocking file I/O runs in a worker thread so the async surface stays
non-blocking.
"""

from __future__ import annotations

import asyncio
import contextlib
import hashlib
import json
import os
import time
import uuid
from collections.abc import Iterator
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from foundry_stoke.errors import (
    AlreadyExists,
    ConcurrencyConflict,
    CorruptedRecord,
    InvalidRecordKey,
    LockTimeout,
    NotFound,
    UnknownRecordType,
)
from foundry_stoke.models import KNOWN_RECORD_TYPES, StoreRecord

try:  # POSIX advisory locks; the FileSystem provider targets local dev.
    import fcntl

    _HAS_FCNTL = True
except ImportError:  # pragma: no cover - exercised only on non-POSIX platforms
    _HAS_FCNTL = False

MAX_KEY_LENGTH = 512
MAX_FILE_BYTES = 1 * 1024 * 1024  # 1 MiB per record file (SEC-002)
DEFAULT_LOCK_TIMEOUT_SECONDS = 10.0
_LOCK_POLL_SECONDS = 0.05


def _new_etag() -> str:
    return uuid.uuid4().hex


def _utcnow() -> datetime:
    return datetime.now(timezone.utc)


def _hash(value: str) -> str:
    return hashlib.sha256(value.encode("utf-8")).hexdigest()


class FileSystemStore:
    """Reference :class:`DurableStoreProvider` backed by JSON files."""

    def __init__(
        self,
        base_dir: str | os.PathLike[str],
        *,
        allowed_types: frozenset[str] = KNOWN_RECORD_TYPES,
        lock_timeout_seconds: float = DEFAULT_LOCK_TIMEOUT_SECONDS,
        max_file_bytes: int = MAX_FILE_BYTES,
    ) -> None:
        self._base = Path(base_dir).resolve()
        self._base.mkdir(parents=True, exist_ok=True)
        self._allowed_types = allowed_types
        self._lock_timeout = lock_timeout_seconds
        self._max_file_bytes = max_file_bytes

    # --- path sanitization (SEC-001) ---

    def _validate_key(self, value: str, name: str) -> None:
        if not isinstance(value, str) or value == "":
            raise InvalidRecordKey(f"{name} must be a non-empty string")
        if len(value) > MAX_KEY_LENGTH:
            raise InvalidRecordKey(
                f"{name} exceeds the maximum length of {MAX_KEY_LENGTH} characters"
            )

    def _record_path(self, id: str, partition_key: str) -> Path:
        self._validate_key(partition_key, "partition_key")
        self._validate_key(id, "id")
        partition_dir = self._base / _hash(partition_key)
        record_path = (partition_dir / f"{_hash(id)}.json").resolve()
        # Defense in depth: the hashed names are hex-only so traversal is already
        # impossible, but confirm the resolved path stays under the base dir.
        if self._base != record_path.parent.parent:
            raise InvalidRecordKey("resolved record path escapes the base directory")
        return record_path

    # --- lock (SEC-006) ---

    @contextlib.contextmanager
    def _record_lock(self, record_path: Path) -> Iterator[None]:
        record_path.parent.mkdir(parents=True, exist_ok=True)
        lock_path = record_path.with_suffix(".json.lock")
        if not _HAS_FCNTL:  # pragma: no cover - non-POSIX fallback
            # Best-effort exclusive-create lock where flock is unavailable.
            deadline = time.monotonic() + self._lock_timeout
            while True:
                try:
                    fd = os.open(lock_path, os.O_CREAT | os.O_EXCL | os.O_RDWR, 0o600)
                    break
                except FileExistsError as exc:
                    if time.monotonic() >= deadline:
                        raise LockTimeout(f"could not acquire lock for {record_path.name}") from exc
                    time.sleep(_LOCK_POLL_SECONDS)
            try:
                yield
            finally:
                os.close(fd)
                with contextlib.suppress(OSError):
                    os.unlink(lock_path)
            return

        fd = os.open(lock_path, os.O_CREAT | os.O_RDWR, 0o600)
        deadline = time.monotonic() + self._lock_timeout
        try:
            while True:
                try:
                    fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
                    break
                except OSError as exc:
                    if time.monotonic() >= deadline:
                        raise LockTimeout(
                            f"could not acquire lock for {record_path.name} "
                            f"within {self._lock_timeout}s"
                        ) from exc
                    time.sleep(_LOCK_POLL_SECONDS)
            yield
        finally:
            with contextlib.suppress(OSError):
                fcntl.flock(fd, fcntl.LOCK_UN)
            os.close(fd)

    # --- safe (de)serialization (SEC-002) ---

    def _read_file(self, record_path: Path) -> StoreRecord:
        try:
            raw = record_path.read_bytes()
        except FileNotFoundError as exc:
            raise NotFound(f"no record file at {record_path.name}") from exc
        if len(raw) > self._max_file_bytes:
            raise CorruptedRecord(f"record file {record_path.name} exceeds size limit")
        try:
            data: Any = json.loads(raw.decode("utf-8"))
        except (json.JSONDecodeError, UnicodeDecodeError) as exc:
            raise CorruptedRecord(f"record file {record_path.name} is not valid JSON") from exc
        if not isinstance(data, dict):
            raise CorruptedRecord(f"record file {record_path.name} is not a JSON object")
        record_type = data.get("type")
        if record_type not in self._allowed_types:
            raise UnknownRecordType(f"record type {record_type!r} is not in the allowlist")
        try:
            return StoreRecord.from_dict(data)
        except (KeyError, ValueError, TypeError) as exc:
            raise CorruptedRecord(
                f"record file {record_path.name} has missing or malformed fields"
            ) from exc

    def _write_file(self, record_path: Path, record: StoreRecord) -> None:
        if record.type not in self._allowed_types:
            raise UnknownRecordType(f"record type {record.type!r} is not in the allowlist")
        payload = json.dumps(record.to_dict()).encode("utf-8")
        if len(payload) > self._max_file_bytes:
            raise CorruptedRecord("serialized record exceeds size limit")
        tmp_path = record_path.with_suffix(f".json.{uuid.uuid4().hex}.tmp")
        tmp_path.write_bytes(payload)
        os.replace(tmp_path, record_path)  # atomic rename within the same dir

    # --- sync CRUD under lock (run in a worker thread) ---

    def _create_sync(self, record: StoreRecord) -> StoreRecord:
        record_path = self._record_path(record.id, record.partition_key)
        with self._record_lock(record_path):
            if record_path.exists():
                raise AlreadyExists(f"record already exists for id={record.id!r}")
            stored = StoreRecord(
                id=record.id,
                partition_key=record.partition_key,
                type=record.type,
                payload=record.payload,
                etag=_new_etag(),
                created_at=_utcnow(),
                updated_at=_utcnow(),
            )
            self._write_file(record_path, stored)
            return stored

    def _upsert_sync(self, record: StoreRecord, expected_etag: str | None) -> StoreRecord:
        record_path = self._record_path(record.id, record.partition_key)
        with self._record_lock(record_path):
            existing: StoreRecord | None
            try:
                existing = self._read_file(record_path)
            except NotFound:
                existing = None
            if existing is not None and expected_etag != existing.etag:
                raise ConcurrencyConflict(
                    f"etag mismatch for id={record.id!r}: record was modified"
                )
            stored = StoreRecord(
                id=record.id,
                partition_key=record.partition_key,
                type=record.type,
                payload=record.payload,
                etag=_new_etag(),
                created_at=existing.created_at if existing is not None else _utcnow(),
                updated_at=_utcnow(),
            )
            self._write_file(record_path, stored)
            return stored

    def _delete_sync(self, id: str, partition_key: str, expected_etag: str | None) -> None:
        record_path = self._record_path(id, partition_key)
        with self._record_lock(record_path):
            try:
                existing = self._read_file(record_path)
            except NotFound:
                raise NotFound(f"no record for id={id!r} in partition") from None
            if expected_etag is not None and expected_etag != existing.etag:
                raise ConcurrencyConflict(f"etag mismatch for id={id!r}: record was modified")
            record_path.unlink()

    def _query_sync(self, partition_key: str, type_filter: str | None) -> list[StoreRecord]:
        self._validate_key(partition_key, "partition_key")
        partition_dir = self._base / _hash(partition_key)
        if not partition_dir.is_dir():
            return []
        records: list[StoreRecord] = []
        for path in sorted(partition_dir.glob("*.json")):
            record = self._read_file(path)
            if type_filter is None or record.type == type_filter:
                records.append(record)
        return records

    # --- async surface (non-blocking) ---

    async def create(self, record: StoreRecord) -> StoreRecord:
        return await asyncio.to_thread(self._create_sync, record)

    async def read(self, id: str, partition_key: str) -> StoreRecord:
        record_path = self._record_path(id, partition_key)
        return await asyncio.to_thread(self._read_file, record_path)

    async def upsert(self, record: StoreRecord, expected_etag: str | None) -> StoreRecord:
        return await asyncio.to_thread(self._upsert_sync, record, expected_etag)

    async def delete(self, id: str, partition_key: str, expected_etag: str | None = None) -> None:
        await asyncio.to_thread(self._delete_sync, id, partition_key, expected_etag)

    async def query_by_partition(
        self, partition_key: str, type_filter: str | None = None
    ) -> list[StoreRecord]:
        return await asyncio.to_thread(self._query_sync, partition_key, type_filter)
