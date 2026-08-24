# foundry-stoke (Python)

Control-plane library for Foundry hosted agent instances.

This package (`import foundry_stoke`) provides the durable store provider abstraction with
reference providers, the session lifecycle controller, and the credential provider used to
authenticate against the Foundry control plane. See `docs/features/stoke-beta/` for the
design (spec, plan, ADRs, contracts).

Status: beta, P1 slice (durable store, session lifecycle, primary authentication).
