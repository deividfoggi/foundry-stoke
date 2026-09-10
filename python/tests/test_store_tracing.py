"""T040: spans at the public reference-store boundary."""

import asyncio
import subprocess
import sys
from pathlib import Path

import pytest
from opentelemetry import trace
from opentelemetry.sdk.trace import TracerProvider
from opentelemetry.sdk.trace.export import SimpleSpanProcessor
from opentelemetry.sdk.trace.export.in_memory_span_exporter import InMemorySpanExporter
from opentelemetry.sdk.trace.sampling import ALWAYS_ON, Sampler
from opentelemetry.trace import StatusCode

from foundry_stoke.errors import AlreadyExists, ConcurrencyConflict, CorruptedRecord, NotFound
from foundry_stoke.models import StoreRecord
from foundry_stoke.store.file_system import FileSystemStore
from foundry_stoke.store.in_memory import InMemoryStore


class SafeSampler(Sampler):
    def should_sample(
        self,
        parent_context,
        trace_id,
        name,
        kind=None,
        attributes=None,
        links=None,
        trace_state=None,
    ):
        if name.startswith("stoke.store."):
            assert dict(attributes) in (
                {"stoke.store.provider": "in_memory"},
                {"stoke.store.provider": "file_system"},
            )
        return ALWAYS_ON.should_sample(
            parent_context, trace_id, name, kind, attributes, links, trace_state
        )

    def get_description(self):
        return "SafeSampler"


@pytest.fixture
def tracing(monkeypatch):
    provider = TracerProvider(sampler=SafeSampler())
    exporter = InMemorySpanExporter()
    provider.add_span_processor(SimpleSpanProcessor(exporter))
    monkeypatch.setattr(trace, "get_tracer_provider", lambda: provider)
    yield provider.get_tracer("test.store"), exporter
    provider.shutdown()


@pytest.fixture(params=["in_memory", "file_system"])
def store(request, tmp_path):
    return (
        InMemoryStore() if request.param == "in_memory" else FileSystemStore(tmp_path),
        request.param,
    )


async def test_public_crud_and_query_emit_one_safe_span_each(store, tracing):
    provider, label = store
    tracer, exporter = tracing
    record = StoreRecord(
        id="private-id",
        partition_key="private-partition",
        type="tracked-session",
        payload={"secret": "AccountKey=private-key"},
    )
    with tracer.start_as_current_span("parent") as parent:
        created = await provider.create(record)
        assert (await provider.read(record.id, record.partition_key)).etag == created.etag
        assert len(await provider.query_by_partition(record.partition_key)) == 1
        updated = await provider.upsert(record, created.etag)
        assert updated.etag != created.etag
        assert await provider.delete(record.id, record.partition_key, updated.etag) is None
        assert await provider.query_by_partition(record.partition_key) == []
        assert trace.get_current_span() is parent
        spans = exporter.get_finished_spans()
        assert [span.name for span in spans] == [
            "stoke.store.write",
            "stoke.store.read",
            "stoke.store.read",
            "stoke.store.write",
            "stoke.store.write",
            "stoke.store.read",
        ]
        for span in spans:
            assert span.parent == parent.get_span_context()
            assert span.end_time >= span.start_time
            assert span.status.status_code == StatusCode.OK
            assert span.status.description is None
            assert not span.events
            assert dict(span.attributes) == {"stoke.store.provider": label}


@pytest.mark.parametrize("operation", ["create", "read", "upsert", "delete", "missing_delete"])
async def test_typed_failures_preserve_contract_and_redact(store, tracing, operation):
    provider, label = store
    tracer, exporter = tracing
    secret = "AccountKey=private-key; token=private-token"
    record = StoreRecord(id=secret, partition_key=secret, type="tracked-session", payload={})
    created = await provider.create(record)
    exporter.clear()
    with tracer.start_as_current_span("parent") as parent:
        if operation == "create":
            pending, error = provider.create(record), AlreadyExists
        elif operation == "read":
            pending, error = provider.read("missing-" + secret, secret), NotFound
        elif operation == "upsert":
            pending, error = provider.upsert(record, secret), ConcurrencyConflict
        elif operation == "delete":
            pending, error = provider.delete(secret, secret, secret), ConcurrencyConflict
        else:
            pending, error = provider.delete("missing-" + secret, secret), NotFound
        with pytest.raises(error):
            await pending
        assert trace.get_current_span() is parent
        (span,) = exporter.get_finished_spans()
        assert span.name == ("stoke.store.read" if operation == "read" else "stoke.store.write")
        assert span.parent == parent.get_span_context()
        assert span.status.status_code == StatusCode.ERROR
        assert span.status.description is None
        assert not span.events
        assert dict(span.attributes) == {"stoke.store.provider": label}
        assert "private-key" not in span.to_json()
        assert "private-token" not in span.to_json()
        assert (await provider.read(secret, secret)).etag == created.etag


async def test_original_exception_is_preserved_without_recording(store, tracing):
    provider, label = store
    tracer, exporter = tracing
    failure = ValueError("private-id /private/path AccountKey=private-key")

    class FailingPayload(dict):
        def __deepcopy__(self, memo):
            raise failure

        def items(self):
            raise failure

    record = StoreRecord(
        id="private-id",
        partition_key="private-partition",
        type="tracked-session",
        payload=FailingPayload(secret="private-key"),
    )
    with tracer.start_as_current_span("parent") as parent:
        with pytest.raises(ValueError) as caught:
            await provider.create(record)
        assert caught.value is failure
        assert trace.get_current_span() is parent
        (span,) = exporter.get_finished_spans()
        assert span.status.status_code == StatusCode.ERROR
        assert span.status.description is None
        assert not span.events
        assert dict(span.attributes) == {"stoke.store.provider": label}


@pytest.mark.parametrize("operation", ["read", "query"])
async def test_corrupt_file_failure_emits_only_safe_read_span(tmp_path, tracing, operation):
    tracer, exporter = tracing
    provider = FileSystemStore(tmp_path)
    record = StoreRecord(
        id="private-id", partition_key="private-partition", type="tracked-session", payload={}
    )
    await provider.create(record)
    for file_path in tmp_path.glob("*/*.json"):
        file_path.write_text("AccountKey=private-key broken JSON")
    exporter.clear()
    with tracer.start_as_current_span("parent") as parent:
        with pytest.raises(CorruptedRecord):
            if operation == "read":
                await provider.read(record.id, record.partition_key)
            else:
                await provider.query_by_partition(record.partition_key)
        assert trace.get_current_span() is parent
        (span,) = exporter.get_finished_spans()
        assert span.name == "stoke.store.read"
        assert span.status.status_code == StatusCode.ERROR
        assert span.status.description is None
        assert not span.events
        assert dict(span.attributes) == {"stoke.store.provider": "file_system"}
        assert str(tmp_path) not in span.to_json()


@pytest.mark.parametrize("provider_label", ["in_memory", "file_system"])
@pytest.mark.parametrize("cancel", [False, True])
async def test_span_covers_suspension_and_restores_context(
    tmp_path, tracing, monkeypatch, provider_label, cancel
):
    tracer, exporter = tracing
    entered = asyncio.Event()
    release = asyncio.Event()
    observed = []
    restored = []

    async def wait():
        observed.append(trace.get_current_span())
        entered.set()
        await release.wait()
        assert trace.get_current_span() is observed[0]

    if provider_label == "in_memory":

        class WaitingLock(asyncio.Lock):
            async def acquire(self):
                await wait()
                return await super().acquire()

        monkeypatch.setattr(asyncio, "Lock", WaitingLock)
        provider = InMemoryStore()
    else:
        to_thread = asyncio.to_thread

        async def waiting_thread(function, *args):
            await wait()
            return await to_thread(function, *args)

        monkeypatch.setattr(asyncio, "to_thread", waiting_thread)
        provider = FileSystemStore(tmp_path)

    async def invoke():
        try:
            return await provider.query_by_partition("private-partition")
        except asyncio.CancelledError as error:
            assert error.args == ("AccountKey=private-key",)
            raise
        finally:
            restored.append(trace.get_current_span())

    with tracer.start_as_current_span("parent") as parent:
        task = asyncio.create_task(invoke())
        try:
            await asyncio.wait_for(entered.wait(), timeout=2)
            assert observed[0] is not parent
            assert observed[0].is_recording()
            assert exporter.get_finished_spans() == ()
        finally:
            if cancel:
                task.cancel("AccountKey=private-key")
            release.set()
            if cancel:
                with pytest.raises(asyncio.CancelledError):
                    await task
                assert task.cancelled()
            else:
                assert await task == []
        assert trace.get_current_span() is parent
        assert restored == [parent]
        assert not observed[0].is_recording()
        (span,) = exporter.get_finished_spans()
        assert span.name == "stoke.store.read"
        assert span.parent == parent.get_span_context()
        assert span.status.status_code == (StatusCode.ERROR if cancel else StatusCode.OK)
        assert span.status.description is None
        assert not span.events
        assert dict(span.attributes) == {"stoke.store.provider": provider_label}


async def test_file_worker_inherits_store_span(tmp_path, tracing, monkeypatch):
    tracer, exporter = tracing
    provider = FileSystemStore(tmp_path)
    record = StoreRecord(id="id", partition_key="partition", type="tracked-session", payload={})
    await provider.create(record)
    exporter.clear()
    read_bytes = Path.read_bytes
    observed = []

    def observe(file_path):
        observed.append(trace.get_current_span())
        return read_bytes(file_path)

    monkeypatch.setattr(Path, "read_bytes", observe)
    with tracer.start_as_current_span("parent") as parent:
        assert (await provider.read(record.id, record.partition_key)).id == record.id
        assert trace.get_current_span() is parent
        (span,) = exporter.get_finished_spans()
        assert observed[0].get_span_context() == span.context
        assert span.parent == parent.get_span_context()


@pytest.mark.parametrize("without_api", [True, False])
def test_noop_preserves_store_contract_in_isolated_process(without_api):
    source = Path(__file__).resolve().parents[1] / "src"
    script = f"""
import sys
sys.path.insert(0, {str(source)!r})
import asyncio
import tempfile
import importlib.util
from foundry_stoke.models import StoreRecord
from foundry_stoke.errors import NotFound, ConcurrencyConflict
from foundry_stoke.store.in_memory import InMemoryStore
from foundry_stoke.store.file_system import FileSystemStore

async def main():
    with tempfile.TemporaryDirectory() as directory:
        for store in (InMemoryStore(), FileSystemStore(directory)):
            record = StoreRecord('id', 'partition', 'tracked-session', {{'secret': 'private-key'}})
            created = await store.create(record)
            assert (await store.read('id', 'partition')).etag == created.etag
            assert len(await store.query_by_partition('partition')) == 1
            try:
                await store.upsert(record, 'wrong-etag')
            except ConcurrencyConflict:
                pass
            else:
                raise AssertionError('conflict lost')
            updated = await store.upsert(record, created.etag)
            await store.delete('id', 'partition', updated.etag)
            assert await store.query_by_partition('partition') == []
            try:
                await store.read('id', 'partition')
            except NotFound:
                pass
            else:
                raise AssertionError('missing record returned')

asyncio.run(main())
if {without_api!r}:
    assert importlib.util.find_spec('opentelemetry') is None
else:
    from opentelemetry import trace
    from opentelemetry.sdk.trace import TracerProvider
    assert not isinstance(trace.get_tracer_provider(), TracerProvider)
    assert not trace.get_current_span().is_recording()
"""
    completed = subprocess.run(
        [sys.executable, *(["-S"] if without_api else []), "-c", script],
        capture_output=True,
        text=True,
        timeout=10,
    )
    assert completed.returncode == 0, completed.stderr
