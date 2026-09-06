## Context

Block 1 froze the old in-process zero-count lifecycle. Block 35 now owns scheduled local admission, immediate pending state, exact-request adapter arming, one advisory detector call, identity-checked predispatch finalization, lazy selected-backend resolution, and all eligible/busy/cancel/failure behavior. See `proposal.md` and `specs/processing/empty-scheduled-worker-gating/spec.md`.

This change is test-only. The focused seam names cannot be fixed until block 35 is applied; implementation must consume its landed coordinator/test APIs rather than add another scheduler, detector, backend selector, launcher, or finalizer.

## Goals / Non-Goals

**Goals:**
- Pin one accepted detector-empty scheduled occurrence as a high-signal regression.
- Prove both zero side effects and zero lazy materialization across the complete worker/heavy graph.
- Verify the exact local zero lifecycle and cleanup without real infrastructure or timing.

**Non-Goals:**
- Re-test block 35's eligible launch, local busy/duplicate behavior, detector cancellation/failure, worker authoritative zero, advisory Busy, retry, or retrigger matrix.
- Change production code, DI registration, protocol, state semantics, scheduling, backend defaults, or deployment modes.
- Repeat block 1's broad in-process skipped/batch/write collaborator characterization.

## Decisions

### Use one accepted-empty vertical regression

Arrange local admission as available, capture the admitted request/token, observe pending, return a completed normal `false` from the detector, and await the real block-35 local finalizer through matching-handle release. One vertical test keeps call order, state, logs, lazy resolution, and cleanup correlated to the same run identity. Small helper assertions may group diagnostics, but they must not turn this into a duplicate outcome matrix.

Alternative: parameterize false, cancellation, exception, busy, and eligible results. Rejected because block 35 owns those behavioral branches; it would obscure the single regression boundary. The focused test asserts absence of cancellation/fatal presentation and documents that a normal false result is the only empty input.

### Distinguish the local state adapter from the worker-event bridge

Block 35 arms its exact-request local adapter before detection and uses its identity-checked local finalizer to publish eligibility zero, start/completion state, and logs. The test permits and observes that local adapter path. It separately installs fail-on-resolution/access sentinels for the child protocol/session and block-27 worker-event state bridge; neither may be materialized or receive an event/result.

Alternative: assert no state adapter of any kind. Rejected because that would conflict with block 35's accepted ordering and make the required local lifecycle impossible.

### Prove laziness at every boundary with factories and counters

Use a detector spy plus a selected-backend resolver/factory counter that throws if invoked. Behind it, provide independent fail-fast counters for command building, launcher/process start, protocol/session, worker-event bridge, in-process executor, skipped/config/batch services, and Overture/GADM/airport/country/resolver dependencies. Assert both construction/resolution counts and operation counts are zero. This catches eager DI materialization even when no external call is visible.

Alternative: assert only launcher calls or inspect that no process exists. Rejected because an eagerly constructed backend can build commands, initialize geodata, or allocate native-heavy services before launch. Alternative: build the full production provider and inspect singleton state. Rejected because it is non-hermetic and can materialize the very graph under test.

### Drive lifecycle with signals and bounded timestamp assertions

Capture state immediately after `MarkPending()`, then allow the detector's normal false completion and await the scheduled operation. Assert the resulting zero snapshot, exact log suffixes/order, start/completion timestamps within before/after UTC bounds, absence of worker/cancellation/fatal entries, and final cleanup of activity, callbacks, active request/CTS, scope, and handle. Use task-completion signals or the existing deterministic block-35 harness, never sleeps, cron waits, or polling.

Alternative: inspect only final `IsRunning`. Rejected because it would miss skipped pending publication, fabricated worker terminal events, incorrect log ordering, or stale coordinator ownership.

### Keep external resources structurally impossible

The detector returns false in memory; no repository is registered or connected. All database, filesystem/geodata, process, and protocol collaborators are throwing sentinels behind lazy factories. Do not use the real block-26 process fixture here: it validates actual transport elsewhere and would weaken the no-launch proof.

## Risks / Trade-offs

- [Block-35 landed APIs use different names] → Re-read its completed source/tests and bind to the exact seams; do not introduce aliases or parallel abstractions solely for this test.
- [A fake bypasses production lazy-resolution order] → Invoke the same scheduled coordinator entry and real local finalizer used by production; substitute only boundary factories/collaborators.
- [Constructor counters miss transitive materialization] → Place counters at selected-backend resolution and at each heavy leaf graph, and combine them with throw-on-construction/access sentinels.
- [Over-asserted notifications or timestamps make the test brittle] → Assert observable state stages, bounded UTC timestamps, stable message suffixes/order, and cleanup; do not freeze exact callback counts or wall-clock prefixes.
- [Busy/cancel/failure expectations drift] → Keep those tests in block 35 and assert only that this normal-false accepted case emits none of their presentation.

## Migration Plan

1. Re-read the applied block-35 coordinator, detector, local-finalizer, backend factory, and tests; map their exact names and lifetimes.
2. Extend the existing block-35 unit harness with counting/throwing worker and heavy-graph sentinels, without changing production registrations.
3. Add the single accepted-empty vertical regression and deterministic stage/lazy-resolution assertions.
4. Run the focused test filter and normal unit suite, then strict validation/status and a block-36-only scope diff. Rollback removes only the regression helpers/test.

## Audit Reconciliation

This test-only change depends on the block-35 fixture and its landed scheduled detector/local-finalizer/child-backend seams. It reuses that fixture to prove detector-zero behavior rather than inventing a second scheduler, detector, child boundary, or worker fixture; implementation must conditionally bind to the exact landed names after block 35.

