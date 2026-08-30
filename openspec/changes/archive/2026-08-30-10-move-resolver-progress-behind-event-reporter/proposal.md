## Why

Administrative resolution still reaches the Web singleton `ProcessingState` through a resolver-only progress adapter. Moving that last processing-time bridge onto the existing run-scoped event session makes resolver and cache progress transportable while keeping the reusable singleton resolver safe for concurrent and non-processing use.

## What Changes

- Pass block 9's already-open processing run session explicitly into administrative resolution; do not open, arm, or discover a processing run inside the resolver.
- Emit the current country, Overture, and GADM resolver/cache messages as awaited Information events through that session.
- Represent each local non-ready cache wait with the session's opaque correlated async activity scope, preserving source-specific download-versus-wait labels and independent overlapping lifetimes.
- End accepted activity scopes on success, ordinary failure, cancellation, or unwind without treating an end as proof that a shared download succeeded.
- Remove the nested `ProcessingResolutionProgress` direct-state bridge while leaving block 9's main-pass routing and singleton Web projection unchanged.
- Keep no-report resolver use silent and processing-session independent; leave Lookup's page-local cache/status flow unchanged.
- Preserve source ordering, resolver results, cache synchronization/ownership, readiness/unavailability behavior, block-6 cancellation taxonomy, OOM and exception behavior, and reporter broken-session semantics.

## Capabilities

### New Capabilities
- `resolver-progress-event-reporting`: Reports processing-time administrative resolver diagnostics and cache-wait activities through an existing run session while preserving correlation, source behavior, and non-processing isolation.

### Modified Capabilities
- None.

## Impact

- Planned production paths: `src/ImmichReverseGeo.Web/Services/AdministrativeAreaResolverService.cs` and the narrow resolver call/bridge in `src/ImmichReverseGeo.Web/Services/ProcessingBackgroundService.cs`.
- Planned verification paths: focused Web tests for resolver reporting, concurrency, cancellation/failure, no-report behavior, processing-session reuse, DI boundaries, and Lookup isolation.
- Depends on blocks 8–9 as planning prerequisites, not evidence that their source has landed. At apply time, verify their required source APIs, registrations, and focused tests exist and pass; if absent, stop and apply them first rather than recreating or assuming their contract here. This change then does not revise their vocabulary, projection, registration, or main-pass sequencing.
- No HTTP/API, configuration, database, cache format, source-ranking, user-visible message text, Lookup workflow, or worker protocol change is planned. The resolver-only progress interface is an internal source seam replaced by the run-session overloads.
