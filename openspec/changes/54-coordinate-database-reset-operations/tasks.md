## 1. Prerequisite and scope binding

- [x] 1.1 Re-read the applied block-47 worker-kind registry and applied block-50/block-52 admission, maintenance-owner, release, and shutdown contracts; record exact type names and stop if using them would require a second gate, worker identity, or lifecycle owner.
- [x] 1.2 Add a block-54 scope test/table covering only Reset All, Reset Selected Items, Reset Matching City/State/Country, Clear Skip List, their current confirmation boundaries, and their exact PostgreSQL/SQLite effects.
- [x] 1.3 Add negative scope assertions that Settings Test Connection/Save All, cache Re-download/Delete/Delete All, cache inventory, worker protocol kinds, Immich schema, and block 55 are unchanged.

## 2. Lightweight database-maintenance orchestration

- [x] 2.1 Extend the landed non-worker resource-owner model with one bounded safe Database maintenance category and add closed validated commands for reset all, selected IDs, matching scope/value, clear skip list, and skipped-only partial retry.
- [x] 2.2 Implement one page-independent controller that validates before admission, handles Admitted/Busy/Unavailable, binds a tracked non-cancellable task, freezes typed per-store results, and releases the exact owner once in a finalization path.
- [x] 2.3 Prove the controller creates no worker JobId/process/protocol/exit, initializes no geodata, updates no ProcessingState, provides no cancel capability, and never queues, hands off, preempts, or retries automatically.
- [x] 2.4 Integrate the controller with the permanent shutdown fence so shutdown-before-admission rejects without writes and admitted maintenance remains owned until its tracked repository work and result finalization end.

## 3. Repository atomicity, handles, and safe results

- [x] 3.1 Preserve parameterized single-statement PostgreSQL reset behavior and actual affected/returned IDs for all, selected, and matching variants; add no Immich migration or schema/index/table change.
- [x] 3.2 Preserve `Pooling=false` and explicit SQLite transaction/disposal behavior, add explicit Clear All and targeted-remove outcomes, and prohibit process-wide pool clearing or caller-supplied paths.
- [x] 3.3 Implement PostgreSQL-first multi-store orchestration with NotStarted/Succeeded/Failed stage states; skip SQLite after PostgreSQL failure and return Partial with an opaque exact retry target after SQLite failure.
- [x] 3.4 Implement explicit skipped-only retry for All or retained IDs that reacquires maintenance, never repeats PostgreSQL, never logs/renders raw ID lists, and discards retry state after success.
- [x] 3.5 Map permission/authentication, connection, timeout, read-only, sharing, and I/O failures to stable bounded safe result codes/copy with no exception, stack, SQL, credential, connection string, host path, or raw asset IDs.

## 4. Reset and Data page integration

- [x] 4.1 Route **Reset All Data...** through the controller only after the existing **Yes, reset all geo data** confirmation; cancellation must attempt no admission or mutation.
- [x] 4.2 Route **Reset Selected Items** through authoritative token parsing that rejects an empty valid set, deduplicates valid IDs, and preserves reporting/ignoring malformed tokens when valid IDs remain and route **Reset Matching City/State/Country** through the closed scope plus parameterized current-value command without adding a confirmation.
- [x] 4.3 Route Data **Clear Skip List** through the same maintenance controller without adding a confirmation and remove optimistic `SkippedCount = 0` behavior.
- [x] 4.4 Disable conflicting page mutation controls for the owning request, suppress stale generation callbacks, expose no user Cancel, and render accessible Validation/Busy/Unavailable/Complete/Partial/Failed plus skipped-only retry results.
- [x] 4.5 After finalized release, reload ResetGeoData location options or Data skipped count from storage; report reload failure separately, preserve the immutable mutation outcome, and emit no cache-inventory invalidation.

## 5. Deterministic coordination and lifecycle tests

- [x] 5.1 Add barrier-controlled reset-versus-ProcessAssets/CoordinateLookup/CacheMutation/cache-deletion races proving exactly one local owner, no losing write/launch, safe busy category, no queue, and later reuse.
- [x] 5.2 Add deterministic simultaneous reset/reset, Busy callback, stale/wrong-owner release, duplicate release, repository-throw-before/after-stage, result-finalization/release, circuit-disposal/completion, and page-generation races without sleeps.
- [x] 5.3 Add shutdown-before-admission, admit-versus-shutdown, shutdown-during-PostgreSQL, shutdown-between-stores, shutdown-during-SQLite, repeated shutdown, and timeout-diagnostic tests proving no early release, fabricated cancel, child kill, or orphan mutation task.
- [ ] 5.4 Add handle/disposal tests proving all commands/readers/transactions close before release, no global pool clear occurs, an external sharing violation is truthful failure, and post-release page reads use fresh connections.
- [x] 5.5 Add negative composition tests proving lightweight reads can execute without reservation, Reset operations resolve no worker/geodata services, and multiple-container scope is not represented as distributed exclusion.

## 6. Operation and integration coverage

- [x] 6.1 Add component/controller tests for all current button labels and exact safeguards: Reset All confirmation/cancel, selected malformed/empty/deduplicated IDs, each matching scope/blank/zero-match value, Clear Skip List, disabled controls, safe results, and reload failure.
- [x] 6.2 Add repository tests for PostgreSQL affected/returned IDs and atomic rollback-on-statement-failure plus SQLite Clear All/Remove transaction counts, missing database behavior, permission/read-only errors, and no handle leaks.
- [x] 6.3 Add multi-store success, PostgreSQL failure with SQLite NotStarted, SQLite failure after PostgreSQL commit, skipped-only retry, retry failure, and navigation-after-partial tests with truthful counts and no automatic PostgreSQL replay.
- [x] 6.4 Run applicable opt-in PostgreSQL integration coverage using the repository's integration test setup; verify only `asset_exif.city/state/country` change and no assets, rows, schema, settings, or cache files change.

## 7. Documentation and strict verification

- [x] 7.1 Update relevant user guidance for exact reset scope, existing confirmation boundaries, complete/partial outcomes, explicit skipped-only retry, permission failures, no user cancellation, and the one-interactive-Web requirement for strict local coordination; do not describe a Settings reset or distributed lock.
- [x] 7.2 Run focused controller/component/repository/race/integration tests as applicable, `npm run test`, and documentation build when user guidance changes.
- [x] 7.3 Run `openspec validate 54-coordinate-database-reset-operations --strict` and final status; perform a block-54-only diff review proving block 55, adjacent change artifacts/code, worker kinds/protocol, cache inventory, and Immich schema were untouched.
