# foundry-stoke (Python)

Control-plane library for Foundry hosted agent instances.

This package (`import foundry_stoke`) provides the durable store provider abstraction with
reference providers, the session lifecycle controller, and the credential provider used to
authenticate against the Foundry control plane. See `docs/features/stoke-beta/` for the
design (spec, plan, ADRs, contracts).

Status: beta, P1 slice (durable store, session lifecycle, primary authentication).

## Session tracing

Install `foundry-stoke[tracing]` to enable the optional OpenTelemetry API integration.
Configure the tracer provider, processors, exporters, and sampling in your application.
The instrumentation scope is `foundry_stoke`. Stoke does not install an SDK or configure
Application Insights. Without the API or an application-configured provider, tracing is
a no-op; the default package has no mandatory dependencies.

Session operations emit `stoke.session.create`, `stoke.session.get`,
`stoke.session.stop`, and `stoke.session.delete` across their complete asynchronous
lifetime. Get, stop, and delete attach a hashed session handle. Create omits the handle;
all four omit free-form identifiers and exception text. Errors and cancellation set error
status without events, exception messages, stack traces, or status descriptions.
Existing `Telemetry` callbacks retain their behavior. Store and warmup tracing are outside
this slice.

The `dev` extra includes `opentelemetry-sdk` for real-span tests. Run the tests with
`python -m pytest`; the tracing suite also checks execution without the API in an isolated
process.
