"""Auth fallback precedence tests (US4, T052/T054, SEC-004/SEC-005, ADR 0005).

Precedence: injected credential > DefaultAzureCredential (primary) > API
key/connection-string fallback > NoCredentialAvailable. Secrets are never
exposed via repr/str and never persisted.
"""

from __future__ import annotations

import sys

import pytest

from foundry_stoke import CredentialProvider, NoCredentialAvailable
from foundry_stoke.auth.credential_provider import ApiKeyCredential, ConnectionStringCredential


class _FakeCredential:
    def __repr__(self) -> str:
        return "_FakeCredential(secret=SUPER_SECRET_VALUE)"


def test_injected_credential_is_returned():
    credential = _FakeCredential()
    provider = CredentialProvider(credential=credential)
    assert provider.resolve_credential() is credential


def test_repr_never_leaks_credential_material():
    provider = CredentialProvider(credential=_FakeCredential())
    text = repr(provider)
    assert "SUPER_SECRET_VALUE" not in text
    assert "has_injected_credential=True" in text


def test_fallback_used_when_primary_unavailable(monkeypatch):
    # Simulate azure-identity being unavailable so the primary path fails.
    monkeypatch.setitem(sys.modules, "azure.identity", None)
    provider = CredentialProvider(environ={"FOUNDRY_API_KEY": "sk-fallback"})
    credential = provider.resolve_credential()

    assert isinstance(credential, ApiKeyCredential)
    assert credential.get_api_key() == "sk-fallback"
    # SEC-005: the secret never appears in repr/str.
    assert "sk-fallback" not in repr(credential)
    assert "sk-fallback" not in str(credential)


def test_connection_string_fallback(monkeypatch):
    monkeypatch.setitem(sys.modules, "azure.identity", None)
    provider = CredentialProvider(
        environ={"FOUNDRY_CONNECTION_STRING": "Endpoint=https://x;Key=secret-value"}
    )
    credential = provider.resolve_credential()
    assert isinstance(credential, ConnectionStringCredential)
    assert "secret-value" not in repr(credential)


def test_injected_credential_takes_precedence_over_fallback(monkeypatch):
    monkeypatch.setitem(sys.modules, "azure.identity", None)
    injected = _FakeCredential()
    provider = CredentialProvider(credential=injected, environ={"FOUNDRY_API_KEY": "sk"})
    assert provider.resolve_credential() is injected


def test_no_credential_when_primary_and_fallback_absent(monkeypatch):
    monkeypatch.setitem(sys.modules, "azure.identity", None)
    provider = CredentialProvider(environ={})
    with pytest.raises(NoCredentialAvailable):
        provider.resolve_credential()
