## 1. Baseline and compatibility locks

- [ ] 1.1 Re-read the applied block 15–30 protocol, role, command, launcher, bridge, cancellation, exit, and classifier source; record the exact symbols consumed and stop if those prerequisites are absent or semantically differ from their finalized specs.
- [ ] 1.2 Add/confirm byte-for-byte v1 processing request/event/terminal and negative-parser goldens before refactoring, including framing, canonical values, unknown discriminators, ordering, and terminal uniqueness.
- [ ] 1.3 Add characterization coverage for processing session identity, execute-flush/cancel ordering, bridge projections, exit classification, and process/stream finality.

## 2. V2 typed protocol foundation

- [ ] 2.1 Define the closed v2 job kind and concrete ProcessAssets request/result/event DTOs plus common ready, job-started, log, activity, terminal, and structured safe-error DTOs; prohibit untyped object/dictionary/JsonElement payload carriers.
- [ ] 2.2 Define reserved CoordinateLookup and CacheMutation descriptor seams without request/result schemas, handlers, advertisement, geodata initialization, or behavior owned by blocks 48 and 51.
- [ ] 2.3 Implement v2 semantic validation for identity/kind/payload agreement, supported-kind advertisement, sequence/activity/terminal rules, safe error bounds, and exactly one typed success result.
- [ ] 2.4 Reuse strict framing/scalar primitives while keeping v1 and v2 semantic codecs distinct; add deterministic v2 positive/negative wire goldens and prove v1 goldens remain unchanged.

## 3. Child-only version selection and command construction

- [ ] 3.1 Preserve the block 18 role parser and its exact sole `--internal-worker` argument unchanged; add a separate early selector that accepts only absent `IMMICH_REVERSEGEO_INTERNAL_WORKER_PROTOCOL_VERSION` for v1 or the exact value `2` for v2 and rejects every other present value before host construction or ready.
- [ ] 3.2 Extend the pure command descriptor/builder with an internal protocol choice: remove the reserved entry from v1 child environments, replace any inherited value with exact `2` for v2 children, preserve all other inherited environment entries, and never mutate the parent environment.
- [ ] 3.3 Keep the reserved entry out of AppConfig, public configuration binding, deployment/Docker documentation, UI/status models, logs, events, and errors; verify the entry alone cannot select worker role.
- [ ] 3.4 Add cross-platform compatibility/security fixtures covering the exact sole argument, absent-to-v1, exact-2-to-v2, ambient valid/invalid replacement or removal, unsupported/empty/whitespace values, selected/envelope mismatch, normal Web invocation, and no negotiation/downgrade.

## 4. Typed handler composition

- [ ] 4.1 Introduce typed job descriptor, arbitration metadata, event reporter, and `IWorkerJobHandler<TRequest,TResult>` contracts with explicit non-reflective adapters.
- [ ] 4.2 Build and startup-validate the DI registry for one descriptor/handler per supported kind; reject duplicate or request/result-incompatible registrations and advertise only successfully registered kinds.
- [ ] 4.3 Adapt the existing processing executor to the ProcessAssets handler using the admitted ProcessingRunRequest.RunId as JobId and keep terminal emission exclusively in the worker host.
- [ ] 4.4 Verify reserved CoordinateLookup/CacheMutation kinds remain unregistered, unadvertised, rejected before acceptance, and unable to resolve heavy services in this change.

## 5. Generalized launcher, session, and bridge

- [ ] 5.1 Generalize launcher inputs/event sinks and ChildWorkerSession to JobId/JobKind while retaining only an identical-value processing RunId compatibility alias and preserving process/stream ownership.
- [ ] 5.2 Put version-specific codecs behind the generalized session without changing readiness timeout, execute-flush gate, bounded stderr, wait-only cancellation, disposal, or completion finality.
- [ ] 5.3 Generalize stop/cancel targeting around the exact active JobId and registered cancellability while preserving the shared deadline, at-most-once cancel, cooperative token, and one process-tree kill policy.
- [ ] 5.4 Add the v2 ProcessAssets bridge adapter and prove it preserves v1 ProcessingState identity, UpdatedCount projection, handled per-asset failures, logs, scoped activities, terminal closure, and matching-handle release.

## 6. Terminal, exit, and classifier reconciliation

- [ ] 6.1 Enforce one host-owned worker terminal only after accepted execute; test that invalid/pre-acceptance, startup, crash, framing, transport, sink, forced-kill, and shutdown failures produce classifier outcomes but no synthetic terminal frame.
- [ ] 6.2 Reuse the established exit mapping and precedence for v2, keeping exit 3 exclusive to the PostgreSQL advisory lock and mapping typed handler/domain failure, startup, output transport, and cooperative cancellation consistently.
- [ ] 6.3 Generalize classifier evidence to JobId/JobKind/version without treating raw exit alone as outcome or allowing a later anomaly to replace an authoritative committed terminal.
- [ ] 6.4 Verify local arbitration/busy metadata launches no process and is not mapped to any worker exit; leave active-slot and busy policy implementation to block 50.

## 7. Parity, rollout, and strict verification

- [ ] 7.1 Run the same deterministic processing executor/bridge/session/cancel/crash/classifier contract suite against v1 and v2 and assert identical user-visible lifecycle and one identity end to end.
- [ ] 7.2 Keep production ProcessAssets on v1 by removing the reserved child entry until all parity suites pass, then switch to v2 by setting exact child value `2`; verify rollback removes the entry and requires no argument or data migration.
- [ ] 7.3 Extend the real worker-process fixture for child-environment selection before ready, v2 ready advertisement, typed dispatch, event ordering, terminal uniqueness, unsupported/unregistered kinds, malformed payloads, stderr drain, cancellation escalation, and exit mapping.
- [ ] 7.4 Run focused protocol/launcher/ProcessingState tests and the normal test suite; run `openspec validate 47-generalize-worker-launcher-to-worker-jobs --strict` and confirm no implementation or artifact from blocks 46, 48, or 51 changed.
