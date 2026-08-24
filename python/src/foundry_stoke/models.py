"""Language-agnostic domain models persisted by Stoke (data-model.md).

Only the types required by the P1 slice (US1 session lifecycle, US2 durable
store) are defined here. Field names follow Python snake_case; the concepts map
one-to-one to the equivalent .NET types (FR-022).
"""

from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime, timezone
from enum import Enum
from typing import Any


def _utcnow() -> datetime:
    return datetime.now(timezone.utc)


class SessionState(str, Enum):
    """Compute state of a tracked session, reflected by Stoke.

    The eight official ``AgentSessionStatus`` values (lowercase) plus an
    ``UNKNOWN`` fallback. A translator maps the raw platform status to this enum
    case-insensitively at runtime; any unrecognized or future value maps to
    ``UNKNOWN``, never coerced to another state (FR-002, CC-008). "Resumed" is
    not a status: it is the derived ``idle`` -> ``active`` transition surfaced
    via ``TrackedSession.resumed_at`` (FR-003).

    Source: https://learn.microsoft.com/en-us/javascript/api/@azure/ai-projects/agentsessionstatus
    """

    CREATING = "creating"
    ACTIVE = "active"
    IDLE = "idle"
    UPDATING = "updating"
    FAILED = "failed"
    DELETING = "deleting"
    DELETED = "deleted"
    EXPIRED = "expired"
    UNKNOWN = "unknown"


# Terminal states: a session in one of these is gone for good and MUST be
# evicted from the warm pool (never counted as ready). UNKNOWN is not terminal
# but is also not treated as ready by the warm pool (conservative).
TERMINAL_SESSION_STATES: frozenset[SessionState] = frozenset(
    {
        SessionState.FAILED,
        SessionState.DELETING,
        SessionState.DELETED,
        SessionState.EXPIRED,
    }
)


class SessionOrigin(str, Enum):
    """Whether a session was born from the warm pool or created on demand."""

    POOL = "pool"
    ON_DEMAND = "on-demand"


class WarmupStrategyKind(str, Enum):
    """Warm-up strategy associated with a pool registry (US3, defined for the
    record model only; the strategies themselves are out of the P1 slice)."""

    PRE_PROVISION_POOL = "pre-provision-pool"
    KEEPALIVE = "keepalive"


@dataclass
class StoreRecord:
    """Generic unit persisted by any durable store provider (ADR 0001).

    The (``id``, ``partition_key``) pair uniquely identifies a record. ``etag``
    is an opaque optimistic-concurrency token assigned by the provider on each
    successful write; callers treat it as opaque.
    """

    id: str
    partition_key: str
    type: str
    payload: dict[str, Any]
    etag: str = ""
    created_at: datetime = field(default_factory=_utcnow)
    updated_at: datetime = field(default_factory=_utcnow)

    def to_dict(self) -> dict[str, Any]:
        """Serialize to a JSON-safe dict (timestamps as ISO-8601 strings)."""
        return {
            "id": self.id,
            "partition_key": self.partition_key,
            "type": self.type,
            "payload": self.payload,
            "etag": self.etag,
            "created_at": self.created_at.isoformat(),
            "updated_at": self.updated_at.isoformat(),
        }

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> StoreRecord:
        """Reconstruct a record from a JSON-safe dict.

        Raises :class:`KeyError`/:class:`ValueError` on missing or malformed
        fields; callers in the FileSystem provider translate these into a typed
        :class:`~foundry_stoke.errors.CorruptedRecord` (SEC-002).
        """
        return cls(
            id=str(data["id"]),
            partition_key=str(data["partition_key"]),
            type=str(data["type"]),
            payload=dict(data["payload"]),
            etag=str(data["etag"]),
            created_at=datetime.fromisoformat(data["created_at"]),
            updated_at=datetime.fromisoformat(data["updated_at"]),
        )


# Allowlist of record type discriminators recognized by Stoke (SEC-002). The
# FileSystem provider rejects any persisted record whose type is not listed.
TRACKED_SESSION_TYPE = "tracked-session"
WARM_POOL_REGISTRY_TYPE = "warm-pool-registry"
KNOWN_RECORD_TYPES: frozenset[str] = frozenset({TRACKED_SESSION_TYPE, WARM_POOL_REGISTRY_TYPE})


@dataclass
class TrackedSession:
    """State of an agent session tracked by Stoke (US1).

    A closed (deleted) session never accepts further operations without a
    deterministic error (FR-005); that invariant is enforced by the
    SessionController, not by this data holder.
    """

    agent_session_id: str
    agent_definition_id: str
    state: SessionState
    idle_timeout_seconds: int
    last_activity_at: datetime = field(default_factory=_utcnow)
    created_at: datetime = field(default_factory=_utcnow)
    origin: SessionOrigin = SessionOrigin.ON_DEMAND
    resumed_at: datetime | None = None


@dataclass
class WarmPoolRegistry:
    """Warm-pool state per agent definition (US3).

    Defined here as a foundational record type so the store allowlist (SEC-002)
    is complete; the warm-up strategies that populate it are out of the P1 slice.
    """

    agent_definition_id: str
    target_size: int
    strategy: WarmupStrategyKind
    tracked_session_ids: list[str] = field(default_factory=list)
    last_reconciled_at: datetime = field(default_factory=_utcnow)
