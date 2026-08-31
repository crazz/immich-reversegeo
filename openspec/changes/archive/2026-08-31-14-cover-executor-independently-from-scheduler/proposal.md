## Why

Block 11 extracts the processing pass and adds narrow extraction-equivalence tests, but that representative coverage is not the reusable scheduler-free characterization needed before later coordinator and worker changes. A broader direct-executor matrix is needed to freeze batching, ordering, persistence, partial-effect, cancellation, failure, and terminal-result behavior without relying on cron, hosting, UI state, or infrastructure.

## What Changes

- Reuse and extend the direct `ProcessingRunExecutor` fixture introduced during block 11 instead of creating duplicate empty or mixed-pass extraction tests.
- Add focused scheduler-free characterization for run snapshots, keyset batches and delay, clamped parallelism, every asset disposition, resolver/airport/city fallback ordering, persistence-before-disposition, and retained partial effects.
- Add deterministic cancellation and failure matrices across meaningful executor boundaries, including repository, reporter, and critical `OutOfMemoryException` paths.
- Assert immutable result, event, count, request-correlation, and fixed-UTC timestamp invariants through the block 7–10 contracts.
- Keep Phase 1 hosted-service lifecycle/state tests and block 11 host delegation/DI/extraction tests at their existing scopes; do not reproduce them in this change.
- Require controlled fakes, gates, and fixed time only—no cron, coordinator, hosted service, Blazor, `ProcessingState`, real PostgreSQL/SQLite, or real geodata/cache artifacts.

## Capabilities

### New Capabilities
- `processing-run-executor-testing`: exhaustive deterministic characterization of a processing run through the standalone executor’s public result and reporting contracts.

### Modified Capabilities
- None.

## Impact

- Primarily expands `tests/ImmichReverseGeo.Tests/ProcessingPipelineTests.cs` or a narrowly split executor-test fixture in the same test project after block 11 is applied.
- Depends on the finalized block 7 request/result model, block 8 reporter session, block 10 resolver reporting boundary, and block 11 executor seams.
- Adds no production behavior, dependency, schema, schedule, coordinator, UI, protocol, persistence, or deployment change.
