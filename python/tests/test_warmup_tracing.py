"""T050: real spans at warm-up operation boundaries."""

import asyncio
import random
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

from foundry_stoke import InMemoryStore, SessionController
from foundry_stoke.observability import Telemetry
from foundry_stoke.scheduling import VirtualClock
from foundry_stoke.warmup import (
    CallableProbe,
    KeepaliveStrategy,
    PreProvisionPoolStrategy,
    ProbeResult,
)
from test_warmup import FakeSessionOperations


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
        if name.startswith("stoke.warmup."):
            strategy = "keepalive" if name == "stoke.warmup.probe" else "pre-provision-pool"
            assert dict(attributes) == {"stoke.warmup.strategy": strategy}
        return ALWAYS_ON.should_sample(
            parent_context, trace_id, name, kind, attributes, links, trace_state
        )

    def get_description(self):
        return "SafeWarmupSampler"


@pytest.fixture
def tracing(monkeypatch):
    provider = TracerProvider(sampler=SafeSampler())
    exporter = InMemorySpanExporter()
    provider.add_span_processor(SimpleSpanProcessor(exporter))
    monkeypatch.setattr(trace, "get_tracer_provider", lambda: provider)
    yield provider.get_tracer("test.warmup"), exporter
    provider.shutdown()


@pytest.mark.parametrize("ok", [True, False])
async def test_probe_spans_preserve_results_and_callbacks(tracing, ok):
    tracer, exporter = tracing
    calls = []
    events = []

    async def probe(agent_definition_id, agent_session_id):
        calls.append((agent_definition_id, agent_session_id))
        return ProbeResult(ok=ok, latency_seconds=0.1)

    strategy = KeepaliveStrategy(
        probe=CallableProbe(probe),
        clock=VirtualClock(),
        interval_seconds=300,
        agent_definition_id="private-agent",
        session_ids=["private-handle", "second-handle"],
        telemetry=Telemetry(sink=events.append),
    )
    with tracer.start_as_current_span("parent") as parent:
        report = await strategy.reconcile()
        assert trace.get_current_span() is parent
        spans = exporter.get_finished_spans()
        assert len(spans) == 2
        assert report.probed == 2
        assert report.failures == (0 if ok else 2)
        assert calls == [("private-agent", "private-handle"), ("private-agent", "second-handle")]
        assert len(events) == 2
        assert all(event.attributes["stoke.probe.ok"] is ok for event in events)
        for span in spans:
            assert span.name == "stoke.warmup.probe"
            assert span.parent == parent.get_span_context()
            assert span.end_time >= span.start_time
            assert span.status.status_code == (StatusCode.OK if ok else StatusCode.ERROR)
            assert span.status.description is None
            assert not span.events
            assert dict(span.attributes) == {"stoke.warmup.strategy": "keepalive"}


@pytest.mark.parametrize("target_size", [0, 2])
async def test_refill_span_contains_session_and_store_operations(tracing, target_size):
    tracer, exporter = tracing
    events = []
    pool = PreProvisionPoolStrategy(
        controller=SessionController(FakeSessionOperations()),
        store=InMemoryStore(),
        agent_definition_id="private-agent",
        target_size=target_size,
        clock=VirtualClock(auto_advance=True),
        telemetry=Telemetry(sink=events.append),
    )
    with tracer.start_as_current_span("parent") as parent:
        for expected_created in (target_size, 0):
            exporter.clear()
            report = await pool.reconcile()
            assert report.created == expected_created
            assert report.ready == target_size
            assert report.failures == 0
            assert trace.get_current_span() is parent
            spans = exporter.get_finished_spans()
            refill = [span for span in spans if span.name == "stoke.warmup.refill"]
            assert len(refill) == 1
            span = refill[0]
            assert span.parent == parent.get_span_context()
            assert span.status.status_code == StatusCode.OK
            assert dict(span.attributes) == {"stoke.warmup.strategy": "pre-provision-pool"}
            assert span.status.description is None
            assert not span.events
            children = [child for child in spans if child is not span]
            assert len(children) == 2 + target_size
            for child in children:
                assert child.parent == span.context
                assert span.start_time <= child.start_time <= child.end_time <= span.end_time
        assert len(events) == 2
        assert all(event.name == "stoke.warmup.refill" for event in events)


@pytest.mark.parametrize("kind", ["probe", "refill"])
@pytest.mark.parametrize("outcome", ["success", "error", "cancel"])
async def test_async_lifetime_context_and_failures(tracing, kind, outcome):
    tracer, exporter = tracing
    entered = asyncio.Event()
    release = asyncio.Event()
    observed = []
    restored = []
    events = []
    failure = ValueError("/private/path AccountKey=private-key token=private-token")

    async def wait():
        observed.append(trace.get_current_span())
        entered.set()
        await release.wait()
        assert trace.get_current_span() is observed[0]
        if outcome == "error":
            raise failure

    class Operations(FakeSessionOperations):
        async def create_session(self, agent_definition_id, idle_timeout_seconds):
            await wait()
            return await super().create_session(agent_definition_id, idle_timeout_seconds)

    async def probe(agent_definition_id, agent_session_id):
        await wait()
        await SessionController(FakeSessionOperations()).get_session(
            agent_definition_id, agent_session_id
        )
        return ProbeResult(ok=True, latency_seconds=0)

    if kind == "probe":
        strategy = KeepaliveStrategy(
            probe=CallableProbe(probe),
            clock=VirtualClock(),
            interval_seconds=300,
            agent_definition_id="AccountKey=private-key",
            session_ids=["private-handle"],
            telemetry=Telemetry(sink=events.append),
        )
    else:
        strategy = PreProvisionPoolStrategy(
            controller=SessionController(Operations()),
            store=InMemoryStore(),
            agent_definition_id="AccountKey=private-key",
            target_size=1,
            clock=VirtualClock(auto_advance=True),
            max_retries=0,
            telemetry=Telemetry(sink=events.append),
        )

    async def invoke():
        try:
            return await strategy.reconcile()
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
            assert not task.done()
            assert not any(
                span.name.startswith("stoke.warmup.") for span in exporter.get_finished_spans()
            )
        finally:
            if outcome == "cancel":
                task.cancel("AccountKey=private-key")
            release.set()
            if outcome == "cancel":
                with pytest.raises(asyncio.CancelledError):
                    await task
                assert task.cancelled()
            else:
                report = await task
                assert report.failures == (1 if outcome == "error" else 0)
                assert len(events) == (2 if kind == "refill" and outcome == "error" else 1)
        assert trace.get_current_span() is parent
        assert restored == [parent]
        assert not observed[0].is_recording()
        spans = exporter.get_finished_spans()
        warmup = [span for span in spans if span.name.startswith("stoke.warmup.")]
        assert len(warmup) == 1
        span = warmup[0]
        assert span.name == f"stoke.warmup.{kind}"
        assert span.parent == parent.get_span_context()
        assert span.status.status_code == (
            StatusCode.OK if outcome == "success" else StatusCode.ERROR
        )
        assert span.status.description is None
        assert not span.events
        for child in spans:
            assert "private-key" not in child.to_json()
            assert "private-handle" not in child.to_json()
            if child is not span:
                assert child.parent == span.context
                assert span.start_time <= child.start_time <= child.end_time <= span.end_time


@pytest.mark.parametrize("failure_at", ["read", "write", "query", "recover", "exhaust"])
async def test_refill_failures_are_errors_without_changing_reports_or_retries(tracing, failure_at):
    tracer, exporter = tracing
    failure = ValueError("/private/path AccountKey=private-key token=private-token")
    events = []
    attempts = 0

    class Operations(FakeSessionOperations):
        async def create_session(self, agent_definition_id, idle_timeout_seconds):
            nonlocal attempts
            attempts += 1
            if failure_at == "exhaust" or (failure_at == "recover" and attempts == 1):
                raise failure
            return await super().create_session(agent_definition_id, idle_timeout_seconds)

        async def get_session(self, agent_definition_id, agent_session_id):
            raise failure

    class Store(InMemoryStore):
        async def read(self, record_id, partition_key):
            if failure_at == "read":
                raise failure
            return await super().read(record_id, partition_key)

        async def create(self, record):
            if failure_at == "write":
                raise failure
            return await super().create(record)

    clock = VirtualClock(auto_advance=True)
    pool = PreProvisionPoolStrategy(
        controller=SessionController(Operations()),
        store=Store(),
        agent_definition_id="private-agent",
        target_size=1,
        clock=clock,
        max_retries=1,
        rng=random.Random(1234),
        telemetry=Telemetry(sink=events.append),
    )
    if failure_at == "query":
        await pool.reconcile()
    exporter.clear()
    events.clear()
    with tracer.start_as_current_span("parent") as parent:
        if failure_at in ("read", "write"):
            with pytest.raises(ValueError) as caught:
                await pool.reconcile()
            assert caught.value is failure
            assert events == []
        else:
            report = await pool.reconcile()
            assert report.ready == (0 if failure_at == "exhaust" else 1)
            assert report.failures == {"query": 0, "recover": 1, "exhaust": 2}[failure_at]
            assert report.evicted == (1 if failure_at == "query" else 0)
            assert len(events) == report.failures + 1
            assert attempts == 2
            assert clock.total_delay == (
                0 if failure_at == "query" else random.Random(1234).uniform(0, 1)
            )
        assert trace.get_current_span() is parent
        spans = [
            span for span in exporter.get_finished_spans() if span.name == "stoke.warmup.refill"
        ]
        assert len(spans) == 1
        span = spans[0]
        assert span.parent == parent.get_span_context()
        assert span.status.status_code == StatusCode.ERROR
        assert span.status.description is None
        assert not span.events
        assert dict(span.attributes) == {"stoke.warmup.strategy": "pre-provision-pool"}


async def test_empty_keepalive_emits_no_probe_span(tracing):
    tracer, exporter = tracing

    async def unexpected(*args):
        pytest.fail("empty keepalive invoked a probe")

    strategy = KeepaliveStrategy(
        probe=CallableProbe(unexpected),
        clock=VirtualClock(),
        interval_seconds=300,
        agent_definition_id="private-agent",
        session_ids=[],
    )
    with tracer.start_as_current_span("parent") as parent:
        report = await strategy.reconcile()
        assert report.probed == report.failures == 0
        assert exporter.get_finished_spans() == ()
        assert trace.get_current_span() is parent


@pytest.mark.parametrize("without_api", [True, False])
def test_noop_warmup_in_isolated_process(without_api):
    source = Path(__file__).resolve().parents[1] / "src"
    script = f"""
import sys
sys.path.insert(0, {str(source)!r})
import asyncio
import importlib.util
from foundry_stoke import InMemoryStore, RawSession, SessionController
from foundry_stoke.scheduling import VirtualClock
from foundry_stoke.warmup import (
    CallableProbe, KeepaliveStrategy, PreProvisionPoolStrategy, ProbeResult,
)

class Operations:
    async def create_session(self, agent_definition_id, idle_timeout_seconds):
        return RawSession('private-handle', 'active')
    async def get_session(self, agent_definition_id, agent_session_id):
        return RawSession(agent_session_id, 'active')

async def probe(*args):
    return ProbeResult(ok=False, latency_seconds=0)

async def main():
    keepalive = KeepaliveStrategy(probe=CallableProbe(probe), clock=VirtualClock(),
        interval_seconds=300, agent_definition_id='private-agent', session_ids=['private-handle'])
    report = await keepalive.reconcile()
    assert report.probed == report.failures == 1
    pool = PreProvisionPoolStrategy(controller=SessionController(Operations()),
        store=InMemoryStore(), agent_definition_id='private-agent', target_size=1,
        clock=VirtualClock(auto_advance=True))
    assert (await pool.reconcile()).created == 1
    assert (await pool.reconcile()).created == 0
    assert await pool.acquire() == 'private-handle'

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


@pytest.mark.parametrize("stage", ["read", "write", "backoff"])
@pytest.mark.parametrize("cancel", [False, True])
async def test_refill_span_covers_registry_and_backoff_awaits(tracing, stage, cancel):
    tracer, exporter = tracing
    entered = asyncio.Event()
    release = asyncio.Event()
    observed = []
    attempts = 0

    async def wait():
        observed.append(trace.get_current_span())
        entered.set()
        await release.wait()

    class Store(InMemoryStore):
        async def read(self, record_id, partition_key):
            if stage == "read":
                await wait()
            return await super().read(record_id, partition_key)

        async def create(self, record):
            if stage == "write":
                await wait()
            return await super().create(record)

    class Clock(VirtualClock):
        async def delay(self, seconds):
            await wait()
            await super().delay(seconds)

    class Operations(FakeSessionOperations):
        async def create_session(self, agent_definition_id, idle_timeout_seconds):
            nonlocal attempts
            attempts += 1
            if stage == "backoff" and attempts == 1:
                raise ValueError("AccountKey=private-key")
            return await super().create_session(agent_definition_id, idle_timeout_seconds)

    pool = PreProvisionPoolStrategy(
        controller=SessionController(Operations()),
        store=Store(),
        agent_definition_id="private-agent",
        target_size=1,
        clock=Clock(auto_advance=True),
        max_retries=1,
    )
    with tracer.start_as_current_span("parent") as parent:
        task = asyncio.create_task(pool.reconcile())
        try:
            await asyncio.wait_for(entered.wait(), timeout=2)
            assert observed[0].name == "stoke.warmup.refill"
            assert observed[0].is_recording()
            assert not task.done()
            assert not any(
                span.name == "stoke.warmup.refill" for span in exporter.get_finished_spans()
            )
        finally:
            if cancel:
                task.cancel("AccountKey=private-key")
            release.set()
            if cancel:
                with pytest.raises(asyncio.CancelledError):
                    await task
            else:
                report = await task
                assert report.ready == 1
                assert report.failures == (1 if stage == "backoff" else 0)
        assert trace.get_current_span() is parent
        assert not observed[0].is_recording()
        spans = [
            span for span in exporter.get_finished_spans() if span.name == "stoke.warmup.refill"
        ]
        assert len(spans) == 1
        span = spans[0]
        assert span.parent == parent.get_span_context()
        assert span.status.status_code == (
            StatusCode.ERROR if cancel or stage == "backoff" else StatusCode.OK
        )
        assert not span.events
        assert span.status.description is None


@pytest.mark.parametrize("kind", ["probe", "refill"])
async def test_callback_exceptions_remain_unmodified(tracing, kind):
    tracer, exporter = tracing
    failure = ValueError("AccountKey=private-key")
    events = []

    def sink(event):
        events.append(event)
        raise failure

    async def probe(*args):
        return ProbeResult(ok=True, latency_seconds=0)

    strategy = (
        KeepaliveStrategy(
            probe=CallableProbe(probe),
            clock=VirtualClock(),
            interval_seconds=300,
            agent_definition_id="private-agent",
            session_ids=["private-handle"],
            telemetry=Telemetry(sink=sink),
        )
        if kind == "probe"
        else PreProvisionPoolStrategy(
            controller=SessionController(FakeSessionOperations()),
            store=InMemoryStore(),
            agent_definition_id="private-agent",
            target_size=0,
            clock=VirtualClock(),
            telemetry=Telemetry(sink=sink),
        )
    )
    with tracer.start_as_current_span("parent") as parent:
        with pytest.raises(ValueError) as caught:
            await strategy.reconcile()
        assert caught.value is failure
        assert trace.get_current_span() is parent
        assert len(events) == 1
        spans = [
            span for span in exporter.get_finished_spans() if span.name.startswith("stoke.warmup.")
        ]
        assert len(spans) == 1
        assert spans[0].status.status_code == (
            StatusCode.OK if kind == "probe" else StatusCode.ERROR
        )
        assert not spans[0].events
        assert spans[0].status.description is None
