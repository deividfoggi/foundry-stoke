"""Auth fallback precedence tests (US4, T052/T054, SEC-004/SEC-005, ADR 0005).

Precedence (contracts/credential-provider.md, CC-005/FR-020):

1. Injected credential (SEC-004) wins.
2. Primary (Entra ID) via the credential factory, if it resolves. The primary is
   *unavailable* when the factory raises (missing package, construction failure,
   or an app-provided validating factory that failed) or when the optional
   ``token_probe`` rejects the constructed credential.
3. API-key / connection-string fallback, if configured.
4. Otherwise ``NoCredentialAvailable``.

Secrets are never exposed via repr/str and never persisted (SEC-005).
"""

from __future__ import annotations

import sys

import pytest

from foundry_stoke import CredentialProvider, NoCredentialAvailable
from foundry_stoke.auth.credential_provider import ApiKeyCredential, ConnectionStringCredential


class _FakeCredential:
    def __repr__(self) -> str:
        return "_FakeCredential(secret=SUPER_SECRET_VALUE)"


class _FakePrimaryCredential:
    """Stands in for the Entra ID primary path when the factory succeeds."""


def _raising_factory() -> object:
    """Model the primary path being unavailable (factory raises)."""
    raise RuntimeError("primary unavailable")


def _primary_factory() -> _FakePrimaryCredential:
    return _FakePrimaryCredential()


# --- precedence: injected credential (SEC-004) -------------------------------


def test_injected_credential_is_returned():
    credential = _FakeCredential()
    provider = CredentialProvider(credential=credential)
    assert provider.resolve_credential() is credential


def test_injected_credential_takes_precedence_over_primary():
    injected = _FakeCredential()
    provider = CredentialProvider(credential=injected, entra_credential_factory=_primary_factory)
    assert provider.resolve_credential() is injected


def test_injected_credential_takes_precedence_over_fallback():
    injected = _FakeCredential()
    provider = CredentialProvider(
        credential=injected,
        entra_credential_factory=_raising_factory,
        environ={"FOUNDRY_API_KEY": "sk"},
    )
    assert provider.resolve_credential() is injected


def test_repr_never_leaks_credential_material():
    provider = CredentialProvider(credential=_FakeCredential())
    text = repr(provider)
    assert "SUPER_SECRET_VALUE" not in text
    assert "has_injected_credential=True" in text


# --- precedence: primary (Entra ID) ------------------------------------------


def test_primary_returned_when_factory_succeeds():
    provider = CredentialProvider(
        entra_credential_factory=_primary_factory,
        environ={"FOUNDRY_API_KEY": "sk-should-not-be-used"},
    )
    assert isinstance(provider.resolve_credential(), _FakePrimaryCredential)


# --- precedence: fallback on real primary unavailability ---------------------


def test_fallback_used_when_factory_raises():
    provider = CredentialProvider(
        entra_credential_factory=_raising_factory,
        environ={"FOUNDRY_API_KEY": "sk-fallback"},
    )
    credential = provider.resolve_credential()

    assert isinstance(credential, ApiKeyCredential)
    assert credential.get_api_key() == "sk-fallback"
    # SEC-005: the secret never appears in repr/str.
    assert "sk-fallback" not in repr(credential)
    assert "sk-fallback" not in str(credential)


def test_connection_string_fallback():
    provider = CredentialProvider(
        entra_credential_factory=_raising_factory,
        environ={"FOUNDRY_CONNECTION_STRING": "Endpoint=https://x;Key=secret-value"},
    )
    credential = provider.resolve_credential()
    assert isinstance(credential, ConnectionStringCredential)
    assert "secret-value" not in repr(credential)
    assert "secret-value" not in str(credential)


def test_api_key_precedes_connection_string():
    provider = CredentialProvider(
        entra_credential_factory=_raising_factory,
        environ={
            "FOUNDRY_API_KEY": "sk-fallback",
            "FOUNDRY_CONNECTION_STRING": "Endpoint=https://x;Key=secret-value",
        },
    )
    assert isinstance(provider.resolve_credential(), ApiKeyCredential)


def test_default_factory_missing_package_falls_back(monkeypatch):
    # A missing azure-identity is just one way the default factory can fail;
    # it must be treated as primary-unavailable, not a special case.
    monkeypatch.setitem(sys.modules, "azure.identity", None)
    provider = CredentialProvider(environ={"FOUNDRY_API_KEY": "sk-fallback"})
    assert isinstance(provider.resolve_credential(), ApiKeyCredential)


def test_no_credential_when_primary_and_fallback_absent():
    provider = CredentialProvider(entra_credential_factory=_raising_factory, environ={})
    with pytest.raises(NoCredentialAvailable):
        provider.resolve_credential()


# --- optional runtime failover via token_probe -------------------------------


def test_token_probe_success_returns_primary():
    probed: list[object] = []

    def probe(credential: object) -> None:
        probed.append(credential)

    provider = CredentialProvider(
        entra_credential_factory=_primary_factory,
        token_probe=probe,
        environ={"FOUNDRY_API_KEY": "sk-should-not-be-used"},
    )
    result = provider.resolve_credential()
    assert isinstance(result, _FakePrimaryCredential)
    assert probed == [result]


def test_token_probe_failure_triggers_fallback():
    def probe(credential: object) -> None:
        raise RuntimeError("token acquisition failed")

    provider = CredentialProvider(
        entra_credential_factory=_primary_factory,
        token_probe=probe,
        environ={"FOUNDRY_API_KEY": "sk-fallback"},
    )
    credential = provider.resolve_credential()
    assert isinstance(credential, ApiKeyCredential)
    assert credential.get_api_key() == "sk-fallback"


def test_token_probe_failure_without_fallback_raises():
    def probe(credential: object) -> None:
        raise RuntimeError("token acquisition failed")

    provider = CredentialProvider(
        entra_credential_factory=_primary_factory, token_probe=probe, environ={}
    )
    with pytest.raises(NoCredentialAvailable):
        provider.resolve_credential()
