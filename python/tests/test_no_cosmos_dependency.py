"""Guard test: the core must not depend on a store SDK (CC-004, FR-011, T039).

Importing the whole package must not pull in any Cosmos/Table/Redis store SDK.
"""

from __future__ import annotations

import importlib
import sys


def test_core_import_does_not_load_store_sdk():
    for name in [
        "foundry_stoke",
        "foundry_stoke.store",
        "foundry_stoke.store.in_memory",
        "foundry_stoke.store.file_system",
        "foundry_stoke.session",
        "foundry_stoke.auth",
    ]:
        importlib.import_module(name)

    forbidden_roots = ("azure.cosmos", "azure.data.tables", "redis", "pymongo")
    loaded = [
        m
        for m in sys.modules
        if any(m == root or m.startswith(f"{root}.") for root in forbidden_roots)
    ]
    assert loaded == [], f"core unexpectedly loaded a store SDK: {loaded}"
