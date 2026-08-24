"""Probe endpoint validation tests (T056, SEC-010, ADR 0007)."""

from __future__ import annotations

import pytest

from foundry_stoke import InvalidEndpoint
from foundry_stoke.warmup import ResponsesPingProbe


def test_probe_endpoint_rejects_non_https():
    with pytest.raises(InvalidEndpoint):
        ResponsesPingProbe(object(), endpoint="http://foundry.example.com")


def test_probe_endpoint_rejects_unexpected_host():
    with pytest.raises(InvalidEndpoint):
        ResponsesPingProbe(
            object(),
            endpoint="https://evil.example.com",
            expected_host="foundry.example.com",
        )


def test_probe_endpoint_accepts_validated_config():
    probe = ResponsesPingProbe(
        object(),
        endpoint="https://foundry.example.com",
        expected_host="foundry.example.com",
    )
    assert probe is not None
