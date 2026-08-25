"""Typed errors for the Stoke control-plane library.

Expected failures are represented as typed exceptions so callers can branch on
intent rather than parsing messages (coding-guidelines: explicit result-based
error handling). All errors derive from :class:`StokeError`.
"""

from __future__ import annotations


class StokeError(Exception):
    """Base class for every error raised by Stoke."""


# --- Durable store errors (US2, contracts/durable-store-provider.md) ---


class StoreError(StokeError):
    """Base class for durable store failures."""


class AlreadyExists(StoreError):
    """Raised by ``create`` when (id, partition_key) already exists."""


class NotFound(StoreError):
    """Raised by ``read``/``delete`` when the record does not exist."""


class ConcurrencyConflict(StoreError):
    """Raised when a write is attempted with a stale etag (CC-003)."""


class InvalidRecordKey(StoreError):
    """Raised when an id/partition key is empty, oversized, or unsafe (SEC-001)."""


class CorruptedRecord(StoreError):
    """Raised when a persisted record is unreadable, partial, or oversized (SEC-002)."""


class UnknownRecordType(StoreError):
    """Raised when a record's ``type`` is not in the allowed discriminators (SEC-002)."""


class LockTimeout(StoreError):
    """Raised when the cross-process file lock cannot be acquired in time (SEC-006)."""


# --- Session lifecycle errors (US1, contracts/session-controller.md) ---


class SessionError(StokeError):
    """Base class for session lifecycle failures."""


class InvalidIdleTimeout(SessionError):
    """Raised when the idle timeout is outside the 300..3600 second range (CC-002)."""


class SessionClosed(SessionError):
    """Raised for any operation on a session that has been deleted (FR-005)."""


class FoundryUnavailable(SessionError):
    """Raised when the Foundry control plane is unavailable or times out."""


class NoAgentVersionAvailable(SessionError):
    """Raised when creating a session but the agent has no published version.

    Foundry requires a ``version_indicator`` to back a session; when no explicit
    version is configured and the agent exposes none, the session cannot be
    created.
    """


# --- Authentication errors (US4, contracts/credential-provider.md) ---


class AuthError(StokeError):
    """Base class for authentication failures."""


class NoCredentialAvailable(AuthError):
    """Raised when neither the primary credential nor a fallback is available (CC-005)."""


class AuthenticationFailed(AuthError):
    """Raised when a credential is present but rejected by the service."""


# --- Configuration / endpoint errors (SEC-010, config facade) ---


class ConfigurationError(StokeError):
    """Raised when required configuration is missing or invalid."""


class InvalidEndpoint(ConfigurationError):
    """Raised when an endpoint is not https or does not match the expected host (SEC-010)."""


# --- Warm-up errors (US3, contracts/warmup-strategy.md) ---


class WarmupError(StokeError):
    """Base class for warm-up failures."""


class TargetSizeExceeded(WarmupError):
    """Raised when a pool target size exceeds the configured maximum (SEC-007)."""
