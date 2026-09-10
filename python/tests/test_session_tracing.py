"""T025: real spans across the public asynchronous session lifecycle."""

import asyncio
import subprocess
import sys
from pathlib import Path

import pytest
from opentelemetry import trace
from opentelemetry.sdk.trace import SpanProcessor, TracerProvider
from opentelemetry.sdk.trace.export import SimpleSpanProcessor
from opentelemetry.sdk.trace.export.in_memory_span_exporter import InMemorySpanExporter
from opentelemetry.trace import StatusCode

from foundry_stoke import InvalidIdleTimeout, SessionClosed, SessionController
from foundry_stoke.observability import hash_session_id
from test_session_controller import FakeSessionOperations


class RedactionProcessor(SpanProcessor):
    def on_start(self, span, parent_context=None):
        if not span.name.startswith("stoke.session."):
            return
        assert set(span.attributes) <= {"stoke.agent_session_id"}
        for value in span.attributes.values():
            assert value.startswith("sha256:")
            assert HANDLE not in value
            assert "private-key" not in value
            assert "private-token" not in value
        assert not span.events
        assert span.status.description is None


@pytest.fixture(scope="module")
def tracing_provider():
    provider = TracerProvider()
    exporter = InMemorySpanExporter()
    provider.add_span_processor(RedactionProcessor())
    provider.add_span_processor(SimpleSpanProcessor(exporter))
    trace.set_tracer_provider(provider)
    yield provider, exporter
    provider.shutdown()


@pytest.fixture
def tracing(tracing_provider):
    provider, exporter = tracing_provider
    exporter.clear()
    return provider.get_tracer("test.session"), exporter


async def test_create_span_covers_await_and_restores_parent(tracing):
    tracer, exporter = tracing
    entered = asyncio.Event()
    release = asyncio.Event()
    observed = []

    class Operations(FakeSessionOperations):
        async def create_session(self, agent_definition_id, idle_timeout_seconds):
            observed.append(trace.get_current_span())
            entered.set()
            await release.wait()
            observed.append(trace.get_current_span())
            return await super().create_session(agent_definition_id, idle_timeout_seconds)

    with tracer.start_as_current_span("parent") as parent:
        task = asyncio.create_task(SessionController(Operations()).create_session("agent-a"))
        await asyncio.wait_for(entered.wait(), timeout=2)
        try:
            assert observed[0] is not parent
            assert observed[0].is_recording()
            assert exporter.get_finished_spans() == ()
        finally:
            release.set()
            result = await task
        assert trace.get_current_span() is parent
        assert result.agent_session_id == "sess-1"
        (span,) = exporter.get_finished_spans()
        assert span.name == "stoke.session.create"
        assert span.parent == parent.get_span_context()
        assert span.end_time >= span.start_time
        assert span.status.status_code == StatusCode.OK
        assert observed[0] is observed[1]
        assert not observed[0].is_recording()


HANDLE = "private-session-handle"
SECRET = "AccountKey=private-key; token=private-token Bearer private-bearer"


@pytest.mark.parametrize("operation", ["create", "get", "stop", "delete"])
@pytest.mark.parametrize("outcome", ["success", "error", "cancel"])
async def test_operation_span_lifetime_outcome_and_redaction(tracing, operation, outcome):
    tracer, exporter = tracing
    entered = asyncio.Event()
    release = asyncio.Event()
    observed = []
    restored = []
    failure = RuntimeError(f"{HANDLE} {SECRET}")

    class Operations(FakeSessionOperations):
        async def wait(self):
            current = trace.get_current_span()
            observed.append(current)
            entered.set()
            await release.wait()
            assert trace.get_current_span() is current
            if outcome == "error":
                raise failure

        async def create_session(self, agent_definition_id, idle_timeout_seconds):
            await self.wait()
            result = await super().create_session(agent_definition_id, idle_timeout_seconds)
            result.agent_session_id = HANDLE
            return result

        async def get_session(self, agent_definition_id, agent_session_id):
            await self.wait()
            return await super().get_session(agent_definition_id, agent_session_id)

        async def stop_session(self, agent_definition_id, agent_session_id):
            await self.wait()
            await super().stop_session(agent_definition_id, agent_session_id)

        async def delete_session(self, agent_definition_id, agent_session_id):
            await self.wait()
            await super().delete_session(agent_definition_id, agent_session_id)

    controller = SessionController(Operations())

    async def invoke():
        try:
            method = getattr(controller, f"{operation}_session")
            return await method(SECRET, *([] if operation == "create" else [HANDLE]))
        finally:
            restored.append(trace.get_current_span())

    with tracer.start_as_current_span("parent") as parent:
        task = asyncio.create_task(invoke())
        await asyncio.wait_for(entered.wait(), timeout=2)
        try:
            assert observed[0] is not parent
            assert observed[0].is_recording()
            assert exporter.get_finished_spans() == ()
        finally:
            if outcome == "cancel":
                task.cancel(f"{HANDLE} {SECRET}")
            release.set()
            if outcome == "success":
                result = await task
                if operation in ("create", "get"):
                    assert result.agent_session_id == HANDLE
            else:
                with pytest.raises(asyncio.CancelledError if outcome == "cancel" else RuntimeError):
                    try:
                        await task
                    except RuntimeError as caught:
                        assert caught is failure
                        raise
        assert trace.get_current_span() is parent
        assert restored == [parent]
        (span,) = exporter.get_finished_spans()
        assert span.name == f"stoke.session.{operation}"
        assert span.parent == parent.get_span_context()
        assert span.context.trace_id == parent.get_span_context().trace_id
        assert span.end_time >= span.start_time
        assert not observed[0].is_recording()
        assert span.status.status_code == (
            StatusCode.OK if outcome == "success" else StatusCode.ERROR
        )
        assert span.status.description is None
        assert not span.events
        assert dict(span.attributes) == (
            {} if operation == "create" else {"stoke.agent_session_id": hash_session_id(HANDLE)}
        )
        serialized = span.to_json()
        for sensitive in (HANDLE, "private-key", "private-token", "private-bearer"):
            assert sensitive not in serialized

    if operation == "delete" and outcome != "success":
        outcome = "success"
        assert (await controller.get_session(SECRET, HANDLE)).agent_session_id == HANDLE


async def test_validation_and_closed_session_failures_are_traced(tracing):
    tracer, exporter = tracing
    controller = SessionController(FakeSessionOperations())
    with tracer.start_as_current_span("parent") as parent:
        with pytest.raises(InvalidIdleTimeout):
            await controller.create_session("agent-a", 60)
        await controller.delete_session("agent-a", HANDLE)
        for operation in ("get", "stop", "delete"):
            with pytest.raises(SessionClosed):
                await getattr(controller, f"{operation}_session")("agent-a", HANDLE)
            assert trace.get_current_span() is parent
        spans = exporter.get_finished_spans()
        assert [span.status.status_code for span in spans] == [
            StatusCode.ERROR,
            StatusCode.OK,
            StatusCode.ERROR,
            StatusCode.ERROR,
            StatusCode.ERROR,
        ]
        assert all(not span.events and span.status.description is None for span in spans)


@pytest.mark.parametrize("operation", ["create", "get"])
async def test_translation_failure_is_inside_span(tracing, operation):
    tracer, exporter = tracing
    failure = ValueError(f"{HANDLE} {SECRET}")

    def translate(raw):
        assert trace.get_current_span().name == f"stoke.session.{operation}"
        raise failure

    controller = SessionController(FakeSessionOperations(), status_translator=translate)
    with tracer.start_as_current_span("parent") as parent:
        with pytest.raises(ValueError) as caught:
            await getattr(controller, f"{operation}_session")(
                "agent-a", *([] if operation == "create" else [HANDLE])
            )
        assert caught.value is failure
        assert trace.get_current_span() is parent
        (span,) = exporter.get_finished_spans()
        assert span.status.status_code == StatusCode.ERROR
        assert not span.events
        assert span.status.description is None


async def test_list_remains_uninstrumented(tracing):
    _, exporter = tracing
    assert await SessionController(FakeSessionOperations()).list_sessions("agent-a") == []
    assert exporter.get_finished_spans() == ()


@pytest.mark.parametrize("without_api", [True, False])
def test_tracing_is_noop_in_isolated_process(without_api):
    source = Path(__file__).resolve().parents[1] / "src"
    tests = Path(__file__).resolve().parent
    script = f"""
import sys
sys.path[:0] = [{str(source)!r}, {str(tests)!r}]
import asyncio
import importlib.util
from foundry_stoke import RawSession, SessionController, SessionClosed
from foundry_stoke.observability import Telemetry

class Operations:
    async def create_session(self, *args):
        return RawSession('handle', 'active')
    async def get_session(self, *args):
        return RawSession('handle', 'idle')
    async def stop_session(self, *args):
        pass
    async def delete_session(self, *args):
        pass

async def main():
    controller = SessionController(Operations())
    assert (await controller.create_session('agent')).agent_session_id == 'handle'
    await controller.get_session('agent', 'handle')
    await controller.stop_session('agent', 'handle')
    await controller.delete_session('agent', 'handle')
    try:
        await controller.get_session('agent', 'handle')
    except SessionClosed:
        pass
    else:
        raise AssertionError('deleted session was reopened')

asyncio.run(main())
if {without_api!r}:
    assert importlib.util.find_spec('opentelemetry') is None
else:
    from opentelemetry import trace
    assert not trace.get_current_span().is_recording()
    assert not isinstance(trace.get_tracer_provider(), __import__(
        'opentelemetry.sdk.trace', fromlist=['TracerProvider']).TracerProvider)
events = []
telemetry = Telemetry(events.append)
event = telemetry.emit('callback', {{'stoke.agent_session_id': 'handle'}})
assert events == [event]
assert event.attributes['stoke.agent_session_id'] != 'handle'
"""
    completed = subprocess.run(
        [sys.executable, *(["-S"] if without_api else []), "-c", script],
        capture_output=True,
        text=True,
        timeout=10,
    )
    assert completed.returncode == 0, completed.stderr
