"""Endpoint validation shared by the config facade and the warm-up probe.

SEC-010 (ADR 0007): endpoints used to reach Foundry (the project endpoint and
the probe target) come exclusively from trusted configuration and are validated
before use (https scheme and, when known, the expected host). This limits the
SSRF surface: a keepalive ping carrying a token must never be redirected to an
arbitrary endpoint.
"""

from __future__ import annotations

from urllib.parse import urlsplit

from foundry_stoke.errors import InvalidEndpoint


def validate_endpoint(endpoint: str, *, expected_host: str | None = None) -> str:
    """Return ``endpoint`` unchanged if it is a trusted https URL.

    Raises :class:`~foundry_stoke.errors.InvalidEndpoint` when the scheme is not
    https, the host is missing, or (when provided) the host differs from
    ``expected_host``.
    """
    parsed = urlsplit(endpoint)
    if parsed.scheme != "https":
        raise InvalidEndpoint(f"endpoint must use https (got scheme {parsed.scheme!r})")
    if not parsed.hostname:
        raise InvalidEndpoint("endpoint must include a host")
    if expected_host is not None and parsed.hostname != expected_host:
        raise InvalidEndpoint(
            f"endpoint host {parsed.hostname!r} does not match expected host {expected_host!r}"
        )
    return endpoint
