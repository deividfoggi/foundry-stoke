"""Pluggable-provider trust hardening tests (T057, SEC-008, ADR 0007).

Stoke validates basic invariants of records returned by a provider instead of
trusting them blindly.
"""

from __future__ import annotations

import pytest

from foundry_stoke import StoreRecord, UnknownRecordType
from foundry_stoke.errors import InvalidRecordKey
from foundry_stoke.store.provider import validate_record_invariants


def test_valid_record_passes():
    record = StoreRecord(id="s1", partition_key="agent-a", type="tracked-session", payload={"n": 1})
    assert validate_record_invariants(record) is record


def test_empty_key_rejected():
    record = StoreRecord(id="", partition_key="agent-a", type="tracked-session", payload={})
    with pytest.raises(InvalidRecordKey):
        validate_record_invariants(record)


def test_unknown_type_rejected():
    record = StoreRecord(id="s1", partition_key="agent-a", type="mystery", payload={})
    with pytest.raises(UnknownRecordType):
        validate_record_invariants(record)
