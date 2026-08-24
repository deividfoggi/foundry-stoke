"""foundry_stoke: control-plane library for Foundry hosted agent instances.

Public surface for the P1 slice: durable store providers (US2), the session
lifecycle controller (US1), and the credential provider (US4, primary path).
"""

from __future__ import annotations

from foundry_stoke.auth import (
    ApiKeyCredential,
    ConnectionStringCredential,
    CredentialProvider,
)
from foundry_stoke.config import Stoke, StokeOptions
from foundry_stoke.endpoints import validate_endpoint
from foundry_stoke.errors import (
    AlreadyExists,
    AuthenticationFailed,
    AuthError,
    ConcurrencyConflict,
    ConfigurationError,
    CorruptedRecord,
    FoundryUnavailable,
    InvalidEndpoint,
    InvalidIdleTimeout,
    InvalidRecordKey,
    LockTimeout,
    NoCredentialAvailable,
    NotFound,
    SessionClosed,
    SessionError,
    StokeError,
    StoreError,
    TargetSizeExceeded,
    UnknownRecordType,
    WarmupError,
)
from foundry_stoke.models import (
    KNOWN_RECORD_TYPES,
    SessionOrigin,
    SessionState,
    StoreRecord,
    TrackedSession,
    WarmPoolRegistry,
    WarmupStrategyKind,
)
from foundry_stoke.observability import Telemetry, TelemetryEvent
from foundry_stoke.scheduling import Clock, SystemClock, VirtualClock
from foundry_stoke.session import RawSession, SessionController, SessionOperations
from foundry_stoke.store import (
    DurableStoreProvider,
    FileSystemStore,
    InMemoryStore,
    validate_record_invariants,
)
from foundry_stoke.warmup import (
    CallableProbe,
    KeepaliveStrategy,
    PreProvisionPoolStrategy,
    ProbeResult,
    ResponsesPingProbe,
    WarmupProbe,
    WarmupReport,
    WarmupStrategy,
)

__all__ = [
    # models
    "StoreRecord",
    "TrackedSession",
    "WarmPoolRegistry",
    "SessionState",
    "SessionOrigin",
    "WarmupStrategyKind",
    "KNOWN_RECORD_TYPES",
    # store
    "DurableStoreProvider",
    "InMemoryStore",
    "FileSystemStore",
    "validate_record_invariants",
    # session
    "SessionController",
    "SessionOperations",
    "RawSession",
    # scheduling
    "Clock",
    "SystemClock",
    "VirtualClock",
    # warm-up
    "WarmupStrategy",
    "WarmupReport",
    "PreProvisionPoolStrategy",
    "KeepaliveStrategy",
    "WarmupProbe",
    "ProbeResult",
    "ResponsesPingProbe",
    "CallableProbe",
    # observability
    "Telemetry",
    "TelemetryEvent",
    # config facade
    "Stoke",
    "StokeOptions",
    "validate_endpoint",
    # auth
    "CredentialProvider",
    "ApiKeyCredential",
    "ConnectionStringCredential",
    # errors
    "StokeError",
    "StoreError",
    "AlreadyExists",
    "NotFound",
    "ConcurrencyConflict",
    "InvalidRecordKey",
    "CorruptedRecord",
    "UnknownRecordType",
    "LockTimeout",
    "SessionError",
    "InvalidIdleTimeout",
    "SessionClosed",
    "FoundryUnavailable",
    "AuthError",
    "NoCredentialAvailable",
    "AuthenticationFailed",
    "ConfigurationError",
    "InvalidEndpoint",
    "WarmupError",
    "TargetSizeExceeded",
]
