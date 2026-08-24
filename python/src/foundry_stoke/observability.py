"""Telemetry redaction layer (ADR 0006, SEC-003/SEC-009).

OpenTelemetry-friendly instrumentation over the ``stoke.*`` namespace with a
fail-safe, allowlist-based redaction policy:

- Only allowlisted attributes are ever emitted; anything else (connection
  strings, API keys, tokens, endpoints-with-keys, payload/session content) is
  dropped by construction, not by pattern matching.
- ``stoke.agent_session_id`` is a capability handle: it is hashed at info level
  and retained in plaintext only for error-level events used in troubleshooting.
- Exception messages are sanitized before being attached to spans.

The layer is dependency-free: a ``sink`` callback receives the redacted events,
so an application can bridge them to a real OpenTelemetry exporter without the
core taking a hard dependency on the OpenTelemetry SDK.
"""

from __future__ import annotations

import hashlib
import re
from collections.abc import Callable, Mapping
from dataclasses import dataclass
from typing import Any

# Canonical allowlist of emittable attributes (plan.md, Observabilidade). Any new
# attribute requires a conscious addition here (barrier against accidental leaks).
ALLOWED_ATTRIBUTES: frozenset[str] = frozenset(
    {
        "stoke.agent_definition_id",
        "stoke.session.state",
        "stoke.store.provider",
        "stoke.store.operation",
        "stoke.warmup.strategy",
        "stoke.warmup.target_size",
        "stoke.warmup.ready",
        "stoke.warmup.created",
        "stoke.warmup.failures",
        "stoke.probe.ok",
    }
)

SENSITIVE_SESSION_ID_ATTRIBUTE = "stoke.agent_session_id"

# Denylist used only to sanitize free-form exception messages (never as the
# primary guarantee, which is the attribute allowlist above).
_SECRET_PATTERNS: tuple[re.Pattern[str], ...] = (
    re.compile(r"(AccountKey|SharedAccessKey|AccessKey|Password|Pwd|Key)=([^;\s]+)", re.IGNORECASE),
    re.compile(r"(sig|signature|token|api[-_]?key|access[-_]?token)=([^&;\s]+)", re.IGNORECASE),
    re.compile(r"Bearer\s+[A-Za-z0-9._~+/=-]+", re.IGNORECASE),
    re.compile(r"https?://[^/\s]*:[^/\s]*@[^\s]+"),
)

_REDACTED = "[redacted]"


def hash_session_id(agent_session_id: str) -> str:
    """Return a short, stable, non-reversible token for an agent session id."""
    digest = hashlib.sha256(agent_session_id.encode("utf-8")).hexdigest()
    return f"sha256:{digest[:12]}"


def sanitize_exception_message(message: str) -> str:
    """Redact secret-shaped substrings from a free-form exception message."""
    redacted = message
    for pattern in _SECRET_PATTERNS:
        redacted = pattern.sub(_REDACTED, redacted)
    return redacted


def redact_attributes(attributes: Mapping[str, Any], *, level: str = "info") -> dict[str, Any]:
    """Return only allowlisted attributes, applying the session-id handle rule.

    ``level`` controls the treatment of ``stoke.agent_session_id``: it is hashed
    at ``info`` level and retained in plaintext at ``error`` level.
    """
    result: dict[str, Any] = {}
    for key, value in attributes.items():
        if key == SENSITIVE_SESSION_ID_ATTRIBUTE:
            result[key] = value if level == "error" else hash_session_id(str(value))
            continue
        if key in ALLOWED_ATTRIBUTES:
            result[key] = value
        # Anything else is dropped (fail-safe).
    return result


@dataclass
class TelemetryEvent:
    """A redacted telemetry event ready to be exported."""

    name: str
    attributes: dict[str, Any]


class Telemetry:
    """Thin instrumentation facade that redacts before emitting.

    Pass a ``sink`` to receive :class:`TelemetryEvent` instances (e.g. to bridge
    to an OpenTelemetry exporter). Without a sink the layer is a safe no-op.
    """

    def __init__(
        self,
        sink: Callable[[TelemetryEvent], None] | None = None,
        *,
        level: str = "info",
    ) -> None:
        self._sink = sink
        self._level = level

    def emit(
        self,
        name: str,
        attributes: Mapping[str, Any],
        *,
        level: str | None = None,
    ) -> TelemetryEvent:
        event = TelemetryEvent(name, redact_attributes(attributes, level=level or self._level))
        if self._sink is not None:
            self._sink(event)
        return event

    def record_exception(
        self,
        name: str,
        exc: BaseException,
        *,
        agent_definition_id: str | None = None,
        level: str = "error",
    ) -> TelemetryEvent:
        base = {"stoke.agent_definition_id": agent_definition_id} if agent_definition_id else {}
        attributes = redact_attributes(base, level=level)
        # Attach the sanitized exception context outside the allowlist path; the
        # message is scrubbed of secret-shaped substrings first.
        attributes["exception.type"] = type(exc).__name__
        attributes["exception.message"] = sanitize_exception_message(str(exc))
        event = TelemetryEvent(name, attributes)
        if self._sink is not None:
            self._sink(event)
        return event
