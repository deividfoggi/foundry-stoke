"""Configuration facade for wiring Stoke in one place.

Pure dependency injection remains the primary path; this facade is a convenience
layer on top, not a replacement. It answers "how does the developer supply
Foundry details" by validating the project endpoint (SEC-010), building the
credential provider, choosing a durable store, and exposing a ready session
controller. Secrets are only read from env/config/vault, never hardcoded and
never persisted.
"""

from __future__ import annotations

import os
from collections.abc import Mapping
from dataclasses import dataclass
from typing import Any

from foundry_stoke.auth import CredentialProvider
from foundry_stoke.endpoints import validate_endpoint
from foundry_stoke.errors import ConfigurationError
from foundry_stoke.session import DEFAULT_IDLE_TIMEOUT_SECONDS, SessionController, SessionOperations
from foundry_stoke.session.foundry_adapter import FoundrySessionOperations
from foundry_stoke.store import DurableStoreProvider, InMemoryStore

PROJECT_ENDPOINT_ENV = "FOUNDRY_PROJECT_ENDPOINT"
EXPECTED_HOST_ENV = "FOUNDRY_EXPECTED_HOST"


@dataclass
class StokeOptions:
    """Declarative configuration for the Stoke facade.

    ``credential`` and ``store`` allow full dependency injection; when omitted,
    the facade resolves a credential via :class:`CredentialProvider` and defaults
    to an in-memory store.
    """

    project_endpoint: str
    expected_host: str | None = None
    idle_timeout_seconds: int = DEFAULT_IDLE_TIMEOUT_SECONDS
    credential: Any | None = None
    store: DurableStoreProvider | None = None


class Stoke:
    """Convenience entry point that composes the Stoke components."""

    def __init__(
        self,
        *,
        options: StokeOptions,
        credential_provider: CredentialProvider,
        store: DurableStoreProvider,
        session_operations: SessionOperations | None = None,
        project_client: Any | None = None,
    ) -> None:
        self.options = options
        self.credential_provider = credential_provider
        self.store = store
        self._session_operations = session_operations
        self._project_client = project_client
        self._controller: SessionController | None = None

    @classmethod
    def build(
        cls,
        options: StokeOptions,
        *,
        session_operations: SessionOperations | None = None,
        project_client: Any | None = None,
    ) -> Stoke:
        # SEC-010: validate the endpoint from trusted config before any wiring.
        validate_endpoint(options.project_endpoint, expected_host=options.expected_host)
        credential_provider = CredentialProvider(credential=options.credential)
        store = options.store if options.store is not None else InMemoryStore()
        return cls(
            options=options,
            credential_provider=credential_provider,
            store=store,
            session_operations=session_operations,
            project_client=project_client,
        )

    @classmethod
    def from_env(
        cls,
        *,
        environ: Mapping[str, str] | None = None,
        session_operations: SessionOperations | None = None,
        project_client: Any | None = None,
    ) -> Stoke:
        env = environ if environ is not None else os.environ
        endpoint = env.get(PROJECT_ENDPOINT_ENV)
        if not endpoint:
            raise ConfigurationError(f"{PROJECT_ENDPOINT_ENV} must be set")
        options = StokeOptions(
            project_endpoint=endpoint,
            expected_host=env.get(EXPECTED_HOST_ENV),
        )
        return cls.build(
            options,
            session_operations=session_operations,
            project_client=project_client,
        )

    @property
    def sessions(self) -> SessionController:
        if self._controller is None:
            self._controller = SessionController(self._resolve_operations())
        return self._controller

    def _resolve_operations(self) -> SessionOperations:
        if self._session_operations is not None:
            return self._session_operations
        client = self._project_client if self._project_client is not None else self._build_client()
        return FoundrySessionOperations(client)

    def _build_client(self) -> Any:
        # Lazy azure import: the core stays dependency-free (research.md, ADR 0005).
        from azure.ai.projects.aio import AIProjectClient

        credential = self.credential_provider.resolve_credential()
        return AIProjectClient(
            endpoint=self.options.project_endpoint,
            credential=credential,
        )
