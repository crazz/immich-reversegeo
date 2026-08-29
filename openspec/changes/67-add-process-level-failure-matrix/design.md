## Context

See `proposal.md` and `specs/worker-process-failure-recovery/spec.md`. Blocks 15–32 already own v1 bytes, lifecycle validation, worker exits, launcher pumps/readiness, the hermetic child fixture, cancellation/shutdown and classification, and the ProcessAssets advisory lock. Blocks 40–46 own deployment modes and the bounded production-image Docker fixture. Blocks 47–54 add v2 job identity, CoordinateLookup, local arbitration, CacheMutation publication/cleanup, and lightweight maintenance boundaries. Block 66 fixes the lifecycle event catalog. This change must compose those seams without altering their production behavior or duplicating fixture ownership.

## Goals / Non-Goals

**Goals:**
- Build one reviewable applicability matrix across v1 ProcessAssets and applicable v2 CoordinateLookup/CacheMutation paths.
- Make every failure deterministic through fixture commands, gates, explicit stream bytes/EOF/exit, fake monotonic time, isolated filesystem roots, and controlled dependencies.
- Assert exact terminal/exit/telemetry finality and all owned resource cleanup, including negative evidence for no-child/no-lock/no-temp paths.
- Keep all hermetic rows in the normal suite and isolate only external-PostgreSQL rows behind Integration.

**Non-Goals:**
- Changing protocol fields, exit taxonomy, terminal precedence, launcher/cancellation policy, advisory locking, arbitration, cache semantics, mode behavior, or block-66 telemetry.
- Adding automatic retry, queues, production fault-injection switches, a second Docker harness, a CI workflow, soak/RSS limits, or distributed exclusion.
- Re-testing geodata correctness or contacting live Overture/GADM endpoints.

## Decisions

### 1. Use an explicit applicability table, not a Cartesian product

The test data model records protocol (v1/v2), job kind, phase, injected fault, raw-exit expectation (`Absent`, `ExactManaged`, or `PresentPlatformRaw`), terminal source/outcome, expected event IDs, and cleanup probes. Rows are included only where the finalized job descriptor owns the dependency or effect.

| Failure family | v1 ProcessAssets | v2 CoordinateLookup | v2 CacheMutation |
|---|---|---|---|
| Invalid mode/protocol selector, request/payload, or runtime config | Yes | Yes | Yes |
| Invalid mode before host/child | Shared pre-host canary | Shared pre-host canary | Shared pre-host canary |
| Local Busy/Unavailable | Yes, no child | Yes, no child | Yes, no child |
| Spawn/ready timeout/crash/exits/protocol/pipes | Yes | Yes | Yes |
| PostgreSQL unavailable/advisory lock/exit 3 | Yes | No | No |
| Cooperative/forced cancellation | Asset-work gate | Lookup gate where cancellable | Cache-work gate |
| Cache temp/publication/retry | No | Incidental cache reads only, no mutation row | Yes |
| Parent shutdown/orphan cleanup | Yes | Yes | Yes |

This avoids fake parity such as exit 3 for lookup/cache. Alternative: generate every fault for every kind and assert refusal. Rejected because it obscures contract ownership and can accidentally standardize behavior that is explicitly not applicable.

### 2. Extend the block-26 fixture with closed, test-only scripts

Use a closed unstartable launcher descriptor for command/spawn failure because no child fixture can run before process start. Add fixture modes that compose already-owned post-start primitives: never-ready, crash-after-ready, terminal-then-exit, mapped/unmapped exit, exact malformed/truncated/oversize/unknown-semantic/out-of-order bytes, additive-compatible properties, concurrent stdout protocol burst plus stderr flood, cooperative gate, unresponsive descendant tree, and controlled cache workspace operations. Inputs are closed selectors and bounded numeric sizes; no arbitrary command, path, frame, or shell fragment is accepted. Each invocation gets a unique root and a registry entry before start, and teardown kills/waits registered process trees and fails the test if fallback cleanup was needed or a descendant remains.

Alternative: add fault injection to the production worker. Rejected because test controls must not become a runtime attack surface or alter production branches. Alternative: create another helper executable. Rejected unless the existing staged fixture cannot host a required platform behavior; block 26 remains the single fixture owner.

### 3. Separate controller observations from expected domain outcomes

A row stores raw observations first: command resolution/start, worker PID, ready, accepted frames, first protocol/bridge fault, committed terminal receipt, raw exit, both pump completions, cancellation/kill facts, stderr-tail metadata, process-tree probe, coordinator receipt, lock probe, and filesystem snapshot. Assertions then apply the existing block-30 classifier and block-66 sink contract. This makes a contradictory terminal/exit row prove both facts rather than comparing only a final UI value.

A valid committed terminal is asserted before supplementary anomalies. A no-terminal row expects exactly one classifier-owned final outcome only after evidence finality. The test never infers process death from a cancellation token, exit from a terminal, drainage from exit, or release from a disabled UI control.

Alternative: assert only public ProcessingState/page results. Rejected because it cannot detect leaked pipes, stale handles, terminal rewrites, or incorrect raw-exit preservation.

### 4. Test 10-second policy with TimeProvider and use watchdogs only for harness failure

Cancellation rows capture first Stop/shutdown at monotonic zero, advance to 9,999 ms and then exactly to the configured production 10,000 ms deadline. Cooperative rows release the worker before the boundary; forced rows keep the worker and descendant blocked until the existing whole-tree kill is observed. A real-time named watchdog exists only to abort a broken test and perform registered cleanup; it is never a behavioral clock or an assertion that a grace duration elapsed.

Alternative: configure a millisecond grace in process tests. Rejected because the request requires evidence for the production 10-second value and fake time makes that cheap. Alternative: sleep 10 seconds. Rejected as slow and racy.

### 5. Drive protocol faults as exact byte streams while both pumps continue

The fixture writes exact stdout byte sequences for malformed JSON, EOF truncation, invalid UTF-8/BOM/framing, 1,048,577-byte frames, duplicate/unknown semantic fields, wrong correlation, sequence gap/regression/duplicate, illegal lifecycle and post-terminal data. It independently fills stderr beyond both OS pipe capacity and the 65,536-byte retained tail. Pump-start gates prove both readers are active before the child begins flooding; exit and final bytes can be released in either order.

Protocol telemetry asserts EventId 6630 and a closed category but never fixture bytes. Exit/classification asserts EventId 6641 only after both drains. Alternative: feed strings directly to the codec. Rejected because block 16 already owns codec purity; this matrix must prove framing, OS pipes, containment and final drainage together.

### 6. Keep cache tests filesystem-hermetic and publication-aware

A test cache workspace contains a known old final database and attempt-owned same-directory temp/download files. Closed fixture stages fail at preparation, transfer/export, validation, handle close, or pre-replace; cancellation is gated before and after atomic publication. Snapshots/hash comparisons prove whether the old or new final file is present and glob checks prove attempt artifacts are absent. Retry occurs only after terminal, pumps, cleanup and coordinator release; it receives a new JobId and path. The test drives the real cache publication/cleanup operation through existing test seams; it does not simulate publication in the child fixture. Retry cannot start until required old-attempt cleanup is complete. Any delayed exact-task-identity continuation tested separately performs map comparison only and cannot touch files or job ownership.

No test downloads bytes. Minimal deterministic valid/invalid cache files and controlled readers stand in for network/export work. Alternative: point at a local HTTP server. Rejected because it adds networking and does not improve process/cache ownership evidence.

### 7. Put only external PostgreSQL rows in Integration

Normal tests include spawn/readiness/crash/exit/protocol/pipe/cancellation/shutdown/arbitration/cache/orphan rows and require no Docker daemon, fixed port, database, or network. Integration rows use block 32's disposable PostgreSQL preference or explicit dedicated `immich_reversegeo_test_` database, fixed advisory key, serial ownership, and capability-specific backend-loss allowance. They cover unavailable connection/session and contention/reacquisition after completion, exits 4/5/6/130, ambiguous unlock/disposal quarantine, and abrupt death. Closed fixture-based configuration/dependency failures remain in the normal suite because they open or control no external PostgreSQL connection.

Block 46's production-image fixture is re-read for run-unique labels, named finite deadlines, no-download sentinels, evidence retention and EXIT/INT/TERM cleanup. This change reuses those harness disciplines but does not rebuild the image or make Docker a normal matrix dependency.

Alternative: categorize every process test Integration. Rejected because the process fixture is hermetic and default coverage is needed. Alternative: emulate the advisory lock. Rejected because PostgreSQL session death/release is the behavior under test.

### 8. Assert block-66 telemetry through one structured sink

For launched jobs, table expectations select from 6610–6612, 6620–6623, 6630, 6640–6641, and conditional 6650. Assertions match EventId/name, level, template fields, canonical `job_id`, exact `job_kind` and bounded origin, distinct controller/worker PIDs, ready/terminal booleans, raw exit and closed classifier codes, and non-negative monotonic durations. Redaction scans structured state, rendered message, scopes, and attached exceptions for every injected secret/payload/path/frame/stderr marker. A pre-launch invalid/Busy/Unavailable row asserts events 6610–6650 are absent because it never enters the launcher. A spawn-failure row expects 6610, no 6611/6612/6640, and one Warning 6641 with null worker PID and `process_classification=startup-failed`.

Every 6641 assertion verifies block 66's complete `available` or explicit `unavailable` memory shape, method/scope/1000-ms interval and count/reason consistency, but memory values are not pass/fail resource thresholds. Non-coalescer rows use lifecycle/log frames and assert no 6650; one deliberate replaceable-progress pressure row asserts at most one exact bounded 6650. Block 68 owns repeated-worker memory behavior.

## Risks / Trade-offs

- [OS process-tree and signal semantics differ] → Probe named capabilities, keep portable process/pipe/exit assertions mandatory, and skip only the dependent row with an explicit reason.
- [A hung fixture can leave descendants] → Register before start, use one named watchdog per phase, kill/wait the whole registered tree in unconditional teardown, and fail on leaks or fallback cleanup.
- [Matrix size hides intent] → Keep expected evidence in reviewable row definitions and use focused helpers only for observation mechanics, not expected outcomes.
- [Telemetry changes concurrently in block 66] → Re-read its final block and all four artifacts before validation; bind exact catalog names/fields without editing block 66.
- [External PostgreSQL session behavior varies by environment] → Keep only rows that open or control an external PostgreSQL connection/session Integration-only, use safe category assertions and exact fixed-key behavior, and allow inconclusive only for block 32's explicitly permitted unavailable backend-loss capability.
- [Filesystem replacement differs by platform] → Use same-volume isolated roots, close all handles, probe only genuinely platform-specific primitives, and assert final/temp state rather than timing.

## Migration Plan

1. Add the matrix data model, observation recorder, structured sink, unique-root/process registry and cleanup assertions around the existing fixture.
2. Add closed fixture modes for lifecycle, protocol, pipe, cancellation/tree and cache stages without changing production worker controls.
3. Implement normal-suite rows by failure family, then add external-PostgreSQL Integration rows using block 32's setup.
4. Run focused matrix tests, repeated normal-suite runs, explicit integration tests when PostgreSQL is available, and strict OpenSpec validation.
5. If fixture extensions destabilize existing block-26/46 evidence, remove the new rows/modes; no production rollback or data migration is required.
