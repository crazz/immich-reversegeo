## Context

See proposal.md for motivation. The due-schedule branch acquires the run lock, marks state pending, and calls private `ProcessingBackgroundService.RunOnceAsync`. That method performs one exact eligibility count, calls `ProcessingState.StartRun(total)`, and returns early for zero through a `finally` that completes state and logs the summary. Its concrete constructor dependencies and private pass method prevent deterministic isolation today.

The exact count includes unprocessed GPS assets even if their IDs are already in `skipped.db`, because skipped IDs are loaded only after the zero gate. Service startup also initializes `skipped.db` before scheduling begins; that startup action is distinct from loading skipped records during a pass.

## Goals / Non-Goals

**Goals:**
- Exercise the same pass method used after scheduled admission without real time, PostgreSQL, SQLite, or geodata.
- Prove the zero gate's call/non-call boundary and current state/log outcome.
- Keep production DI resolution and pass ordering unchanged.

**Non-Goals:**
- Testing cron calculation, scheduler delays, run-lock admission, `MarkPending()`, or startup initialization.
- Extracting the Phase 2 executor, introducing the later work detector, or changing exact-count SQL semantics.
- Proving geodata services are not constructed by DI; the contract covers operation calls during the empty pass.

## Decisions

### Use an internal pass entry and internal operation seam

Keep the public production constructor unchanged. Refactor its current concrete method bindings into a small internal immutable operation set used by `RunOnceAsync`, and add an internal construction/invocation path available to `ImmichReverseGeo.Tests` through friend-assembly access. The production path supplies delegates to the existing config, database, skipped-store, resolver, airport, skipped-write, and location-write methods. The test supplies one zero-returning count spy and fail-fast delegates for every operation that must remain unused, including the configuration read; configuration is read only after a positive eligibility result.

This is preferred over reflection because it is compile-time checked, over starting `BackgroundService` because that couples the test to cron and real delays, and over making service/repository APIs public or virtual because that widens production surface. The seam is not a new executor or public abstraction and can be removed or absorbed when later blocks extract one.

### Test execution after admission, not the scheduler loop

Invoke the internal pass directly. Both scheduled and manual admission currently call the same method; this block names the scheduled use case but intentionally freezes only execution after admission. Scheduler lifecycle and arbitration are covered separately.

### Assert stable state and message content

Use a real `ProcessingState`. Assert zero totals and counters, inactive state, null last error, and non-null start/completion timestamps. Read logs with `GetRecentLog()`, ignore the wall-clock prefix, and assert that the nothing-to-process message precedes `Run complete. Processed=0 Skipped=0 Errors=0`.

## Risks / Trade-offs

- [The operation set mirrors several existing collaborators] → Keep it internal, immutable, and limited to calls already made by the pass; do not expose a general processing API.
- [Direct pass invocation does not prove scheduler admission] → State that boundary in test names and leave cron/lock/`MarkPending` behavior to lifecycle tests.
- [Exact count is an expensive no-work detector and includes previously skipped IDs] → Characterize rather than optimize it here; later detector work owns that change.
- [Exact log assertions can be brittle] → Match message content and order only, not timestamps or notification counts.

## Migration Plan

1. Add the internal operation seam and friend-test access without changing the public constructor or hosted-service registration.
2. Add the deterministic empty-pass characterization test in the Web test project.
3. Run the focused test, then the default non-integration/non-performance suite.
4. No deployment or data migration is required; rollback removes the seam and test together.
