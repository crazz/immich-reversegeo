## 1. Reconcile the finalized block-35 test seam

- [ ] 1.1 Re-read the applied block-35 scheduled coordinator entry, detector, exact-request local finalizer/adapter, selected-backend factory, and tests; record the landed names/lifetimes and do not add parallel abstractions.
- [ ] 1.2 Confirm the focused case starts with local admission available and uses a detector that completes normally with `false`; leave block 35's busy/duplicate, cancellation, failure, eligible, worker-zero, advisory-Busy, and retrigger cases unchanged.

## 2. Build deterministic hermetic sentinels

- [ ] 2.1 Add an in-memory detector spy that captures call count, request identity, and coordinator cancellation token without using a database.
- [ ] 2.2 Add fail-on-resolution/counting selected-backend, command-builder, launcher/process-start, protocol/session, worker-event bridge, and in-process-executor factories/fakes.
- [ ] 2.3 Add constructor/resolution/operation counters or throwing sentinels for skipped/config/batch collaborators and Overture, GADM, airport, country-index, and resolver graphs, without building the production heavy provider.

## 3. Add the accepted-empty vertical regression

- [ ] 3.1 Invoke the same admitted scheduled operation as production, deterministically observe active request/CTS publication and pending state, release the detector with a normal no-work result, and await local finalization and matching-handle cleanup without sleeps or cron timing.
- [ ] 3.2 Assert exactly one detector call with the admitted request token and zero backend resolution, command build, process start, launcher, protocol/session, worker-event bridge, worker event/result, in-process execution, and heavy collaborator construction/access.
- [ ] 3.3 Assert eligibility/total zero; processed/skipped/error zero; `LastError == null`; bounded start/completion timestamps; no activity; exact nothing-to-process then zero-summary log order; no cancellation/fatal presentation; and terminal idle state with no active request, CTS, callback, scope, or handle residue.
- [ ] 3.4 Assert subsequent lazy factories remain at zero after cleanup so non-materialization is proven by counters/sentinels rather than inferred from the absence of a child process or geodata files.

## 4. Verify scope and contracts

- [ ] 4.1 Run the focused MSTest filter for the new accepted-empty regression and `npm run test` with default exclusions; use no real PostgreSQL/SQLite database, geodata, or block-26 child fixture.
- [ ] 4.2 Run `openspec validate 36-verify-empty-schedules-do-not-launch-workers --strict` and `openspec status --change 36-verify-empty-schedules-do-not-launch-workers`.
- [ ] 4.3 Review the scope diff and confirm only numbered block 36, its linked planning artifacts, and eventual focused test/helper files changed; do not touch block 35, block 37, production behavior, or configuration.

## Audit Reconciliation

This test-only change depends on the block-35 fixture and its landed scheduled detector/local-finalizer/child-backend seams. It reuses that fixture to prove detector-zero behavior rather than inventing a second scheduler, detector, child boundary, or worker fixture; implementation must conditionally bind to the exact landed names after block 35.

