"""Internal optional tracing (T025/T040, ADR 0006)."""

from collections.abc import Awaitable, Callable, Coroutine, Iterator
from contextlib import contextmanager
from functools import wraps
from typing import Any, ParamSpec, TypeVar

from foundry_stoke.observability import redact_attributes

_Parameters = ParamSpec("_Parameters")
_Result = TypeVar("_Result")


def store_operation(
    name: str, provider: str
) -> Callable[
    [Callable[_Parameters, Awaitable[_Result]]],
    Callable[_Parameters, Coroutine[Any, Any, _Result]],
]:
    def decorate(
        operation: Callable[_Parameters, Awaitable[_Result]],
    ) -> Callable[_Parameters, Coroutine[Any, Any, _Result]]:
        @wraps(operation)
        async def traced(*args: _Parameters.args, **kwargs: _Parameters.kwargs) -> _Result:
            with _span(name, {"stoke.store.provider": provider}):
                return await operation(*args, **kwargs)

        return traced

    return decorate


@contextmanager
def session_span(name: str, session_id: str | None = None) -> Iterator[None]:
    with _span(name, {"stoke.agent_session_id": session_id} if session_id is not None else {}):
        yield


@contextmanager
def warmup_span(name: str, strategy: str) -> Iterator[Callable[[], None]]:
    with _span(name, {"stoke.warmup.strategy": strategy}) as mark_failed:
        yield mark_failed


@contextmanager
def _span(name: str, attributes: dict[str, str]) -> Iterator[Callable[[], None]]:
    failed = False

    def mark_failed() -> None:
        nonlocal failed
        failed = True

    try:
        from opentelemetry import trace
    except ImportError:
        yield mark_failed
        return

    with trace.get_tracer("foundry_stoke").start_as_current_span(
        name,
        attributes=redact_attributes(attributes, level="info"),
        record_exception=False,
        set_status_on_exception=False,
    ) as span:
        try:
            yield mark_failed
        except BaseException:
            span.set_status(trace.StatusCode.ERROR)
            raise
        else:
            span.set_status(trace.StatusCode.ERROR if failed else trace.StatusCode.OK)
