"""Telemetry redaction tests (T012/T064/T066, SEC-003/SEC-009, ADR 0006).

The guarantee is fail-safe: only allowlisted attributes are ever emitted, and no
secret-shaped value crosses the telemetry boundary.
"""

from __future__ import annotations

from foundry_stoke.observability import (
    Telemetry,
    redact_attributes,
    sanitize_exception_message,
)


def test_allowlist_drops_non_allowlisted_and_secret_shaped_attributes():
    attributes = {
        "stoke.agent_definition_id": "agent-a",
        "stoke.connection_string": "AccountKey=abc123==;",
        "api_key": "sk-secret",
        "authorization": "Bearer token-value",
    }
    out = redact_attributes(attributes)
    assert out == {"stoke.agent_definition_id": "agent-a"}


def test_session_id_hashed_at_info_and_retained_at_error():
    info = redact_attributes({"stoke.agent_session_id": "sess-123"}, level="info")
    assert info["stoke.agent_session_id"] != "sess-123"
    assert "sess-123" not in info["stoke.agent_session_id"]

    error = redact_attributes({"stoke.agent_session_id": "sess-123"}, level="error")
    assert error["stoke.agent_session_id"] == "sess-123"


def test_sanitize_exception_message_removes_secret_patterns():
    message = "connect failed: Endpoint=https://x;AccountKey=SECRETKEY==; token=abcd1234"
    cleaned = sanitize_exception_message(message)
    assert "SECRETKEY" not in cleaned
    assert "abcd1234" not in cleaned


def test_no_secret_patterns_in_emitted_attributes():
    events: list = []
    telemetry = Telemetry(sink=events.append)
    telemetry.emit(
        "stoke.warmup.refill",
        {
            "stoke.agent_definition_id": "agent-a",
            "stoke.agent_session_id": "sess-xyz",
            "stoke.connection_string": "AccountKey=leak==;",
            "api_key": "sk-leak",
        },
    )
    assert events
    for event in events:
        for value in event.attributes.values():
            text = str(value)
            assert "AccountKey" not in text
            assert "sk-leak" not in text
            assert "sess-xyz" not in text  # handle hashed at info level


def test_record_exception_message_is_sanitized():
    events: list = []
    telemetry = Telemetry(sink=events.append)
    telemetry.record_exception(
        "stoke.warmup.refill",
        RuntimeError("boom AccountKey=SECRET=="),
        agent_definition_id="agent-a",
    )
    assert events
    message = events[0].attributes["exception.message"]
    assert "SECRET" not in message
