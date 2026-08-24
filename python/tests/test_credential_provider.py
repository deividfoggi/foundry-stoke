"""Tests for the credential provider primary path (US4, SEC-004/005)."""

from __future__ import annotations

import sys

import pytest

from foundry_stoke import CredentialProvider, NoCredentialAvailable


class _FakeCredential:
    """Stand-in TokenCredential whose repr embeds a secret-looking string."""

    def __repr__(self) -> str:
        return "_FakeCredential(secret=SUPER_SECRET_VALUE)"


def test_injected_credential_is_returned():
    # SEC-004: an explicitly injected credential gives deterministic behavior.
    credential = _FakeCredential()
    provider = CredentialProvider(credential=credential)
    assert provider.resolve_credential() is credential


def test_repr_never_leaks_credential_material():
    # SEC-005: repr/str must not expose credential material.
    provider = CredentialProvider(credential=_FakeCredential())
    text = repr(provider)
    assert "SUPER_SECRET_VALUE" not in text
    assert "has_injected_credential=True" in text


def test_no_credential_available_when_identity_missing(monkeypatch):
    # CC-005: with no injected credential and azure-identity unavailable, the
    # provider fails with a clear, deterministic error.
    monkeypatch.setitem(sys.modules, "azure.identity", None)
    provider = CredentialProvider()
    with pytest.raises(NoCredentialAvailable):
        provider.resolve_credential()
