## Context

See `proposal.md` for motivation and `specs/database-maintenance-coordination/spec.md` for behavior. The current reset surface is narrower than “database reset” suggests:

| Current surface | Exact action | Storage changed | Block-54 classification |
|---|---|---|---|
| Data / **Reset Immich Geo Data** / **Reset All Data...** then **Yes, reset all geo data** | Null all city/state/country values, then clear skipped tracking | Immich PostgreSQL `asset_exif` + `/data/skipped.db` | Lightweight reserved Web maintenance |
| Data / **Reset Immich Geo Data** / **Reset Selected Items** | Parse/deduplicate asset GUIDs, null all three values for matching IDs, remove those IDs from skipped tracking | Immich PostgreSQL + `skipped.db` | Lightweight reserved Web maintenance |
| Data / **Reset Immich Geo Data** / **Reset Matching City/State/Country** | Match one chosen column/value, null all three values, remove returned IDs from skipped tracking | Immich PostgreSQL + `skipped.db` | Lightweight reserved Web maintenance |
| Data / **Clear Skip List** | Delete all skipped rows | `skipped.db` only | Lightweight reserved Web maintenance |
| Administrative Areas / **Re-download** | Download/export/validate/publish a country cache | Overture or GADM cache database/files | Block 51 typed `CacheMutation` worker; out of scope here |
| Administrative Areas / **Delete**, **Delete All Overture Divisions**, **Delete All GADM Caches** | Delete final cache files | Overture or GADM cache files | Block 52 lightweight reserved Web maintenance; out of scope here |
| Settings / **Test Connection** | Open and dispose one PostgreSQL connection | None | Lightweight Web read; out of mutation admission |
| Settings / **Save All Settings** | Persist `settings.json` | Config only | Not a database reset and outside block 54 |

Location-option queries, skipped counts, and cache inventory are reads. There is no Settings database-reset button. Current ResetGeoData calls repository methods inline; reset-all alone has confirmation; selected/matching and Clear Skip List execute immediately. PostgreSQL and SQLite calls are separate. Data optimistically forces skipped count to zero even if the repository fails, while ResetGeoData reloads its option lists after mutation.

Finalized planning for block 47 has a closed worker union of `ProcessAssets`, `CoordinateLookup`, and `CacheMutation`. Block 50 owns one process-local, fail-fast exclusive resource and no queue. Block 52 establishes the compatible pattern for a non-worker maintenance owner whose page cannot release it early. At apply time block 54 must bind to those exact landed seams and stop on incompatibility rather than invent a second gate, identity, or protocol kind. The current source does not yet contain block-50 APIs, so planning names are descriptive, not promised implementation symbols.

## Goals / Non-Goals

**Goals:**

- Define the exact database-reset boundary from current code and preserve its data semantics and confirmation boundary.
- Serialize all four reset mutations against locally admitted workers and maintenance with one page-independent owner.
- Keep database-only work in Web, with typed validation/results and no geodata initialization.
- Make per-store commit, partial completion, handles, shutdown, UI reload, safe errors, and deterministic races explicit.

**Non-Goals:**

- Add or change Immich tables, indexes, columns, constraints, or migration scripts.
- Reset/delete assets, EXIF rows, Overture/GADM cache databases, settings, schedules, logs, or processing history.
- Change block 51 re-download, block 52 deletion, block 53 cache inventory, block 55 composition cleanup, or Settings behavior.
- Add `DatabaseMaintenance` to the closed worker protocol, broaden the processing advisory lock, add distributed locking, a queue, retries, preemption, priorities, fairness, or cross-container guarantees.
- Add application authentication/roles in this block. Existing deployment access controls remain the security perimeter; disabled buttons and request validation are not authorization.

## Decisions

### 1. Use one lightweight Database maintenance owner, not a typed worker job

Database reset uses Npgsql and a small SQLite repository operation; it does not download/export geodata or benefit materially from temporary-process memory reclamation. Introduce one page-independent maintenance controller/command family with closed operations equivalent to `ResetAll`, `ResetSelected(ids)`, `ResetMatching(scope, value)`, `ClearSkipList`, and a partial-result-only `RetrySkippedCleanup(target)`. It acquires block 50/52's exact non-worker reservation extension under bounded safe category “Database maintenance.” It creates no JobId, child process, protocol frame, exit code, or ProcessingState event.

The controller, not Razor, owns admission, command lifetime, typed result finalization, and exact-once release. The admitted task remains alive if a circuit disappears. Each page uses an operation generation so stale callbacks cannot overwrite a newer result; generation is UI correlation, not a reservation/job identity.

Alternative: add a `DatabaseMaintenance` worker kind. Rejected because block 47's closed union does not reserve it, this work initializes no heavy geodata, and a new protocol variant adds transport/finality complexity without memory isolation value. Alternative: call repositories inline under button `_busy`. Rejected because page lifetime can release or lose ownership and `_busy` cannot arbitrate processing or other pages.

### 2. Classify adjacent Data/Settings work explicitly

Only the four current database mutations enter this command. Reads remain direct bounded Web reads and do not reserve: reset option lists, skipped count, block-53 cache inventory, and Settings Test Connection. Reads can overlap an active job because they neither publish nor delete shared geodata; their own connection disposal and failure presentation remain required.

Block 51 remains the only owner of Re-download as a cancellable typed CacheMutation worker. Block 52 remains the only owner of per-cache Delete/Delete All as non-cancellable lightweight Cache maintenance. Cache inventory is unaffected and receives no invalidation from location/skipped reset. Settings Save All writes config, not a reset. No file under or behavior owned by block 55 is changed.

Alternative: reserve every database read. Rejected because it lengthens exclusive ownership and conflates connectivity/inventory reads with mutation. Alternative: fold all “Data” buttons into block 54. Rejected because numbered blocks 51–53 already define cache mutation/deletion/inventory boundaries.

### 3. Validate immutable commands before admission, then revalidate data-dependent input during ownership

Razor may parse for immediate feedback, but the command boundary is authoritative. Selected reset parses at the authoritative command boundary, preserves current behavior by ignoring and reporting malformed tokens when at least one valid GUID remains, rejects an empty valid set, and deduplicates the immutable valid set; it never silently ignores malformed tokens. Matching reset accepts only the closed City/State/Country discriminator and nonblank bounded value. Reset-all requires an explicit confirmed command that can only be produced by the existing confirmation action. Clear Skip List accepts no path or database override.

Syntactic validation occurs before admission. Data-dependent matching remains one parameterized PostgreSQL statement under the reservation; the database, not a stale options list, determines current matches. Values are never interpolated into SQL. Request types contain no SQL, connection string, credential, host path, cache path, or arbitrary options.

Alternative: trust disabled controls/confirmation booleans in component state. Rejected because stale or directly invoked handlers still require server-side validation. Alternative: reject selected-ID input whenever any malformed token accompanies valid IDs. Rejected because current behavior proceeds with valid IDs and reports ignored tokens. Alternative: reject a matching value merely because a previously loaded options list changed. Rejected because the atomic parameterized update can authoritatively produce zero matches.

### 4. Preserve per-store atomicity and make the cross-store boundary truthful

Each existing PostgreSQL reset is one parameterized UPDATE (with RETURNING where IDs are required), which PostgreSQL executes atomically. SQLite Clear All is one DELETE statement; targeted Remove uses one local SQLite transaction. Do not wrap unrelated pools or attempt a distributed PostgreSQL/SQLite transaction.

For multi-store commands, order is PostgreSQL first, then skipped cleanup for the exact requested/returned target. If PostgreSQL fails, SQLite is NotStarted. If PostgreSQL commits and SQLite fails, the typed result is Partial and preserves the cleanup target inside controller state. The UI offers an explicit **Retry skipped-list cleanup** action that reacquires maintenance independently, touches only `skipped.db`, and never repeats the committed Immich mutation. Targets are never rendered or logged as a raw ID list; UI shows bounded counts. For Reset All the retry target is All; selected uses the validated IDs; matching uses IDs returned before their values were nulled. No retry is automatic or queued.

This approach avoids the dangerous matching-value case where a later rerun can no longer rediscover committed rows. A successful retry or normal completion discards the retained target. Circuit disposal may discard presentation/retry state but cannot undo committed data; the final partial result still states that Clear Skip List or a new selected reset can repair tracking.

Alternative: SQLite first. Rejected because skipped entries could be removed for an Immich reset that never commits. Alternative: call the same reset again. Rejected because matching criteria disappear after PostgreSQL commit. Alternative: report generic failure. Rejected because it hides committed mutation and encourages unsafe reruns.

### 5. Hold reservation through handles, results, and tracked shutdown finality

The maintenance controller binds its tracked Task to the admitted owner before exposing execution. Every connection, command, reader, and SQLite transaction is async-disposed/disposed before result finalization; `Pooling=false` remains for skipped SQLite and no global pool clear is introduced. The owner result is frozen, then release runs exactly once in `finally`. Page reload occurs after release and is not part of protected mutation.

There is no user Cancel button. Navigation/circuit cancellation does not cancel repository work or release ownership. The shutdown fence linearizes before new admission. If shutdown loses to admission, it joins the exact tracked maintenance task and waits for completion/release; it does not send worker cancellation, kill a process, or free the slot on a timeout while code may still mutate. Database command timeouts/configured provider cancellation bound failure where already supported; forced process termination can still leave a truthful last-known/unknown client result, while PostgreSQL itself preserves statement transaction atomicity.

Alternative: pass circuit cancellation into repository mutation. Rejected because a disconnect can create ambiguous presentation and early ownership release. Alternative: release before result mapping or on shutdown timeout. Rejected because another worker could overlap open handles or ongoing mutation.

### 6. Keep block-50 exclusion claims process-local

The reservation serializes only callers using the same Standard/Web-only singleton. It does not protect against another Web container, run-once worker, manually invoked private worker, direct PostgreSQL client, or direct writer to a shared `skipped.db`. Do not acquire or broaden the ProcessAssets advisory lock: block 50 defines exit 3 and that lock as processing-only.

Operator-facing guidance states that strict reset/processing exclusion requires one interactive Web control plane and no independently launched conflicting writer. Permission or sharing violations are ordinary safe failures, not evidence to clear pools or retry automatically.

Alternative: silently reuse the processing advisory lock. Rejected because it changes finalized lock scope/exit semantics and still cannot protect the SQLite half. Alternative: filesystem/distributed lease. Rejected as a separate cross-container design.

### 7. Return closed, safe results and reload actual state

Admission results remain `Busy(safe owner category)` and `Unavailable(safe reason)`. An admitted command result contains operation, PostgreSQL stage status/count, skipped stage status/count, aggregate Complete/Partial/Failed outcome, stable safe error code/message, and optional opaque internal retry target. It contains no exception, stack, SQL, credential, connection string, path, or raw asset-ID list.

ResetGeoData disables all mutation controls while it owns an operation and preserves Reset All's current confirmation only. Data disables Clear Skip List while any page-owned request is pending. Final messages use existing alert patterns and an accessible live announcement. After release, ResetGeoData reloads location options; Data reloads skipped count from storage instead of assigning zero optimistically. Reload failure is separate from mutation result. The controller does not invalidate cache inventory because no cache file changed.

Alternative: infer success from no exception and optimistic counts. Rejected because repository failures and partial commits become false success. Alternative: hold admission through UI reload. Rejected because reads are lightweight and should not extend exclusive mutation ownership.

### 8. Preserve the existing security and permission boundary without overstating it

No new public HTTP endpoint, credential field, role, or authorization claim is added. Interactive access remains governed by the existing deployment perimeter. Commands use ConfigService-provided environment credentials and StorageOptions-derived `skipped.db` only. Parameterized SQL and closed operation DTOs prevent SQL/path injection. Safe result mapping covers authentication/authorization, connection, timeout, read-only, sharing, and I/O failures without leaking secrets or local topology.

Alternative: treat a confirmation dialog as authorization. Rejected because confirmation mitigates accidental Reset All only. Alternative: add roles/auth in this block. Rejected because it is a separate externally visible security feature requiring broader design.

## Risks / Trade-offs

- [A PostgreSQL commit followed by skipped cleanup failure is not atomic] → Preserve exact cleanup target internally, report Partial, and offer skipped-only explicit retry without rerunning PostgreSQL.
- [Navigation loses the partial-result retry control] → Do not cancel admitted work; retain safe repair guidance and allow Clear Skip List or a new selected-ID cleanup path, while never claim rollback.
- [Another container races reset] → State process-local scope and require one interactive writer for strict exclusion; do not broaden block 50's advisory lock.
- [Shutdown cannot finish a provider call promptly] → Keep ownership until the tracked task ends, use established provider timeouts, and report shutdown diagnostics rather than fabricate release.
- [A stale page reload overwrites a newer message] → Use page operation generation and immutable finalized results; reload errors remain separate.
- [Landed block 50/52 seams differ from planning terminology] → Re-read and adapt to exact owner/admission/shutdown types; stop rather than add nested coordination.

## Migration Plan

1. Re-read applied blocks 47, 50, and 52 and bind to their exact closed worker-kind, maintenance-owner, admission, finality, and shutdown seams; stop on incompatibility and do not edit block 55.
2. Add the Database maintenance non-worker owner/controller and deterministic admission/release/shutdown tests before routing a production page.
3. Add closed command/result/stage/retry-target types and adapt existing repository calls without SQL/schema changes; preserve Npgsql parameterization and SQLite connection policy.
4. Route ResetGeoData's three mutations, preserving only Reset All confirmation, then route Data Clear Skip List and remove optimistic zeroing.
5. Add authoritative result rendering, partial skipped-only retry, generation guards, post-release option/count reloads, and safe operator guidance for local-only coordination.
6. Run focused unit/component/race tests, applicable opt-in PostgreSQL integration tests, `npm run test`, strict OpenSpec validation/status, and a block-54-only diff review proving blocks 51–53/55 and Immich schema were untouched.

Rollback restores the prior direct page calls and removes the Database maintenance owner/controller/retry UI together. No data, config, cache, worker-protocol, or Immich schema migration is required; already committed reset data is not automatically restored.
