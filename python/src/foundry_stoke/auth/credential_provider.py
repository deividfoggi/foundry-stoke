"""Credential provider (US4, contracts/credential-provider.md).

``DefaultAzureCredential`` (Entra ID) is the primary path. When it is
unavailable, an API-key / connection-string fallback is used, read from trusted
configuration (environment/vault), never hardcoded.

Resolution precedence:

1. An explicitly injected ``TokenCredential`` (SEC-004: deterministic prod).
2. ``DefaultAzureCredential`` (Entra ID), if ``azure-identity`` is present.
3. API key or connection-string fallback, if configured.
4. Otherwise :class:`NoCredentialAvailable` (CC-005).

Security controls (security-review-architecture.md):

- SEC-004: a deterministic credential can be forced in production by injecting an
  explicit ``TokenCredential`` (constructor argument) or by setting
  ``AZURE_TOKEN_CREDENTIALS`` for ``DefaultAzureCredential`` to honor.
- SEC-005: fallback secrets are read at resolve time (minimized in-memory
  lifetime), never persisted to the store, and never exposed via ``repr``/``str``.
"""

from __future__ import annotations

import os
from collections.abc import Mapping
from typing import Any

from foundry_stoke.errors import NoCredentialAvailable

API_KEY_ENV = "FOUNDRY_API_KEY"
CONNECTION_STRING_ENV = "FOUNDRY_CONNECTION_STRING"


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
    production behavior, or a fake in tests). ``environ`` overrides the source of
    fallback configuration for testability; it defaults to ``os.environ`` read at
    resolve time so no secret is retained on this object.
    """

    def __init__(
        self,
        credential: Any | None = None,
        *,
        environ: Mapping[str, str] | None = None,
    ) -> None:
        self._injected_credential = credential
        self._environ = environ

    def resolve_credential(self) -> Any:
        """Return a usable credential following the resolution precedence.

        1. An explicitly injected credential (SEC-004).
        2. ``DefaultAzureCredential`` (Entra ID), if ``azure-identity`` is present.
        3. API key / connection-string fallback, if configured.
        4. Otherwise raise :class:`NoCredentialAvailable` (CC-005).
        """
        if self._injected_credential is not None:
            return self._injected_credential

        try:
            from azure.identity import DefaultAzureCredential
        except ImportError:
            fallback = self._resolve_fallback()
            if fallback is not None:
                return fallback
            raise NoCredentialAvailable(
                "no credential available: install the 'azure' extra for "
                "DefaultAzureCredential, inject an explicit TokenCredential, or "
                f"configure {API_KEY_ENV}/{CONNECTION_STRING_ENV}"
            ) from None

        return DefaultAzureCredential()

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
