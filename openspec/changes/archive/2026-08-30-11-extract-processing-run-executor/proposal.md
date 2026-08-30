## Why

The hosted background service still owns both control-plane concerns and the complete processing pass, preventing the same behavior from being invoked independently by a later coordinator or worker host. A UI- and scheduler-independent execution boundary is needed. Blocks 7–10 are planning prerequisites, not evidence that their source has landed; at apply time, verify their required source APIs, registrations, and focused tests exist and pass, and if any is absent, stop and apply it first rather than recreating or assuming its contract here.

## What Changes

- Introduce an awaitable processing-run executor that accepts the finalized block-7 request, the block-8 reporter, and a cancellation token, and returns the matching terminal result.
- Move authoritative eligibility counting, the non-empty configuration snapshot, skipped-ID suppression, keyset batching, bounded parallel asset processing, administrative resolution, optional airport fallback, write-back, skipped persistence, and per-asset disposition accounting into that executor.
- Preserve the exact administrative-first/airport-second resolution order, current fallback rules, query semantics, batch cursor/delay behavior, independent per-asset writes, partial-run effects, cancellation taxonomy, diagnostics, and event lifecycle established by blocks 6–10. This retains the existing post-fallback logger-only no-city conditional as a source-compatible guard; under the existing `GeoResult` invariant it is unreachable and is not an executable skipped outcome.
- Keep the hosted service as the in-process caller and leave startup initialization, cron timing, nonblocking admission, run-lock ownership, request creation/adapter arming, pending state, manual cancellation-source ownership, and UI/control-plane logs outside execution.
- Add narrow executor-facing collaborator seams and deterministic clocks/gates without redesigning repository queries, geometry, cache behavior, work detection, or public settings.
- Preserve existing external behavior; this is a refactor with no database, configuration, UI, scheduling, or protocol migration.

## Capabilities

### New Capabilities
- processing-run-execution: Executes one admitted processing request to a terminal result independently of scheduling and WebUI state while preserving the current processing and persistence semantics.

### Modified Capabilities
- None.

## Impact

- Planned implementation affects the processing executor, the pipeline code currently in ProcessingBackgroundService, narrow collaborator abstractions or adapters for ImmichDbRepository, SkippedAssetsRepository, AdministrativeAreaResolverService, OverturePlacesService, ConfigService, Web DI registration, and focused tests.
- The executor and its production collaborators remain singleton-compatible; all counters, cursors, skipped-ID snapshots, configuration snapshots, reporter sessions, and timing facts for an invocation are run-local.
- Blocks 7–10 are planning prerequisites only. Verify whether they are applied in source before implementation; if not, apply them first. This block then consumes the verified reporter-backed resolver API without revising it.
- This change precedes scheduler reduction (12), coordinator ownership (13), and the broader scheduler-free executor test matrix (14). Phase 3 protocol/process concerns and Phase 7 work detection remain out of scope.
