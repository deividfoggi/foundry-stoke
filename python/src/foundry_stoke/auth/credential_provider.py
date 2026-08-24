"""Credential provider (US4, contracts/credential-provider.md).

``DefaultAzureCredential`` (Entra ID) is the primary path. When it is
unavailable, an API-key / connection-string fallback is used, read from trusted
configuration (environment/vault), never hardcoded.

Resolution precedence:

1. An explicitly injected ``TokenCredential`` (SEC-004: deterministic prod).
2. The primary (Entra ID) credential produced by ``entra_credential_factory``,
   unless it is unavailable (see below).
3. API key or connection-string fallback, if configured.
4. Otherwise :class:`NoCredentialAvailable` (CC-005).

"Primary unavailable" means the factory raised (``azure-identity`` missing,
construction failure, or an app-provided validating factory that failed) or the
optional ``token_probe`` rejected the constructed credential. Package presence
alone is not availability: ``DefaultAzureCredential`` constructs lazily and only
fails on the first token acquisition. Entra ID and API-key are different
mechanisms, so ``resolve_credential`` cannot silently fail over from one to the
other by construction; deterministic runtime failover is opted into via
``token_probe`` (which runs a caller-supplied token acquisition against the
primary and falls back when it raises).

Security controls (security-review-architecture.md):

- SEC-004: a deterministic credential can be forced in production by injecting an
  explicit ``TokenCredential`` (constructor argument) or by setting
  ``AZURE_TOKEN_CREDENTIALS`` for ``DefaultAzureCredential`` to honor.
- SEC-005: fallback secrets are read at resolve time (minimized in-memory
  lifetime), never persisted to the store, and never exposed via ``repr``/``str``.
"""

from __future__ import annotations

import os
from collections.abc import Callable, Mapping
from typing import Any

from foundry_stoke.errors import NoCredentialAvailable

API_KEY_ENV = "FOUNDRY_API_KEY"
CONNECTION_STRING_ENV = "FOUNDRY_CONNECTION_STRING"

# Seams kept as ``Any`` so importing the core never requires ``azure-identity``
# (CC-004, ADR 0005). ``CredentialFactory`` returns a ``TokenCredential``;
# ``TokenProbe`` runs a token acquisition against it and raises on failure.
CredentialFactory = Callable[[], Any]
TokenProbe = Callable[[Any], None]


def _default_entra_credential_factory() -> Any:
    """Construct ``DefaultAzureCredential`` lazily (Entra ID primary path).

    Raising (``ImportError`` when ``azure-identity`` is absent, or any
    construction failure) signals that the primary path is unavailable.
    """
    from azure.identity import DefaultAzureCredential

    return DefaultAzureCredential()


class ApiKeyCredential:
    """Fallback credential wrapping an API key (SEC-005).

    The secret is held in a single slot, never rendered by ``repr``/``str``, and
    can be cleared to minimize its in-memory lifetime.
    """

    __slots__ = ("_api_key",)

    def __init__(self, api_key: str) -> None:
        self._api_key = api_key

    def get_api_key(self) -> str:
        return self._api_key

    def clear(self) -> None:
        self._api_key = ""

    def __repr__(self) -> str:
        return "ApiKeyCredential(***)"

    __str__ = __repr__


class ConnectionStringCredential:
    """Fallback credential wrapping a connection string (SEC-005)."""

    __slots__ = ("_connection_string",)

    def __init__(self, connection_string: str) -> None:
        self._connection_string = connection_string

    def get_connection_string(self) -> str:
        return self._connection_string

    def clear(self) -> None:
        self._connection_string = ""

    def __repr__(self) -> str:
        return "ConnectionStringCredential(***)"

    __str__ = __repr__


class CredentialProvider:
    """Resolves the control-plane credential following the documented precedence.

    Pass ``credential`` to inject an explicit ``TokenCredential`` (deterministic
    production behavior, or a fake in tests). ``entra_credential_factory``
    supplies the primary (Entra ID) credential; the default imports
    ``azure-identity`` and constructs ``DefaultAzureCredential`` lazily. A factory
    that raises marks the primary as unavailable, so the fallback is reached on
    real unavailability, not only on a missing package.

    ``token_probe`` is an optional runtime-failover hook: when provided, it runs
    against the constructed primary and, if it raises, the primary is treated as
    unavailable and resolution falls through to the fallback. It defaults to
    ``None`` so ``resolve_credential`` stays non-blocking (no network) unless the
    caller opts in.

    ``environ`` overrides the source of fallback configuration for testability; it
    defaults to ``os.environ`` read at resolve time so no secret is retained on
    this object (SEC-005).
    """

    def __init__(
        self,
        credential: Any | None = None,
        *,
        environ: Mapping[str, str] | None = None,
        entra_credential_factory: CredentialFactory | None = None,
        token_probe: TokenProbe | None = None,
    ) -> None:
        self._injected_credential = credential
        self._environ = environ
        self._entra_credential_factory = (
            entra_credential_factory or _default_entra_credential_factory
        )
        self._token_probe = token_probe

    def resolve_credential(self) -> Any:
        """Return a usable credential following the resolution precedence.

        1. An explicitly injected credential (SEC-004).
        2. The primary (Entra ID) credential, if available.
        3. API key / connection-string fallback, if configured.
        4. Otherwise raise :class:`NoCredentialAvailable` (CC-005).
        """
        if self._injected_credential is not None:
            return self._injected_credential

        primary = self._resolve_primary()
        if primary is not None:
            return primary

        fallback = self._resolve_fallback()
        if fallback is not None:
            return fallback

        raise NoCredentialAvailable(
            "no credential available: install the 'azure' extra for "
            "DefaultAzureCredential, inject an explicit TokenCredential, or "
            f"configure {API_KEY_ENV}/{CONNECTION_STRING_ENV}"
        )

    def _resolve_primary(self) -> Any | None:
        """Return the primary credential, or ``None`` if it is unavailable.

        The primary is unavailable when the factory raises (missing package,
        construction failure, or an app-provided validating factory that failed)
        or when the optional ``token_probe`` rejects the constructed credential.
        Any such exception is treated as unavailability and never propagated, so
        resolution can fall through to the fallback.
        """
        try:
            credential = self._entra_credential_factory()
        except Exception:
            return None
        if self._token_probe is not None:
            try:
                self._token_probe(credential)
            except Exception:
                return None
        return credential

    def _resolve_fallback(self) -> ApiKeyCredential | ConnectionStringCredential | None:
        # SEC-005: read secrets at resolve time; never stored on the provider.
        env = self._environ if self._environ is not None else os.environ
        api_key = env.get(API_KEY_ENV)
        if api_key:
            return ApiKeyCredential(api_key)
        connection_string = env.get(CONNECTION_STRING_ENV)
        if connection_string:
            return ConnectionStringCredential(connection_string)
        return None

    def __repr__(self) -> str:
        # SEC-005: never expose credential material in repr/str.
        return (
            f"CredentialProvider(has_injected_credential={self._injected_credential is not None})"
        )
