## Context

Block 33 defines the immutable two-value internal selection, keyed run-scoped backends, selected-only resolution, and no-fallback rule. Blocks 34 and 35 exercise manual and eligible scheduled child execution, while block 36 protects the scheduled zero-work branch. Blocks 25–32 already own launch, protocol, projection, cancellation, classification, cleanup, advisory-lock, and process evidence. This change changes only the temporary default and must consume those applied contracts rather than create another dispatch path. See proposal.md for motivation and specs/processing/child-worker-default/spec.md for behavior.

## Goals / Non-Goals

**Goals:**
- Make ChildWorker the immutable internal selection used by ordinary production composition.
- Define startup validation, selected-only DI resolution, same-artifact packaging, lifecycle parity, sequencing, and rollback gates.
- Retain the block-33 in-process route solely as a short-lived explicit composition seam until block 38.

**Non-Goals:**
- No public setting, environment variable, CLI argument, endpoint, UI control, or Phase 6 deployment mode.
- No change to scheduling eligibility, the block-35 detector, worker request/protocol, advisory lock, processing pipeline, geodata behavior, or ProcessingState semantics.
- No second worker executable, assembly, image, sidecar, Docker socket, or per-run recovery policy.
- No edits to block 36, which is owned by the parallel change.

## Decisions

### 1. Change the composition default, not trigger code

Ordinary Web composition supplies the block-33 immutable selection as ChildWorker. Manual and scheduled paths continue through the same coordinator and freeze that selection on the admitted handle. Trigger-specific overrides are forbidden.

Alternative: hard-code child dispatch independently in each trigger. Rejected because it would bypass the single admission, cancellation, state, and cleanup contract and could weaken empty scheduling.

### 2. “Emergency” means a code-only composition seam

The explicit InProcess value remains callable only by internal/test composition before the host is built. It is not bound from AppConfig, configuration providers, environment, arguments, endpoints, or UI. In production, using it for an emergency requires a deliberate source/composition change and rebuilt artifact (or reverting block 37); an operator cannot toggle a running or already-built deployment. Block 38 deletes this seam and its production registration.

Alternative: add a hidden environment variable. Rejected because a hidden runtime switch is still a public operational contract, creates an untested support surface, and conflicts with the numbered migration.

### 3. Validate selected prerequisites at startup; resolve the backend per run

Before accepting work, startup validation checks the selected kind, required keyed registrations, launcher/role descriptor, current assembly location, and required child runtime files. Validation must not resolve a scoped IProcessingRunBackend, launch a process, construct the in-process executor, or construct worker-only geodata services. An undefined enum or missing/invalid selected-child prerequisite fails host startup with a bounded actionable diagnostic. DI validation does not inspect or activate the unselected graph.

After startup, backend resolution stays lazy: manual admission resolves the frozen child backend; scheduled admission resolves it only after the detector returns true. Detector zero, active cancellation/failure before dispatch, and local contention resolve neither backend. Runtime child start or protocol failures flow to the established classifier/finalizer and never trigger fallback.

Alternative: validate by eagerly resolving both keyed backends. Rejected because it violates selected-only construction and can load heavy geodata in the Web process. Alternative: defer every prerequisite error to the first run. Rejected because the new default could appear healthy while being unable to process and might invite unsafe fallback.

### 4. Preserve one authoritative lifecycle

The default child adapter uses the existing request/run ID, reporter lease, coordinator token, state projection, cancellation owner, classifier/finalizer, scope disposal, and identity-checked handle release. No adapter adds a second terminal report. Completed, Cancelled, and Failed remain worker terminal statuses; process-local rejection creates no process or exit, PostgreSQL advisory Busy is a launched Failed terminal plus exit 3, and forced kill is raw platform evidence for classification rather than a worker-selected exit. A selection is final from admission through finality: no automatic retry, replacement child, in-process fallback, replay, or resubmission.

Alternative: fall back only on child startup failure. Rejected because side effects and protocol acceptance can be ambiguous, dual execution would violate exact-once control-plane ownership, and prerequisite failures must remain visible.

### 5. Use the same image and assembly

The launcher continues to invoke the internal worker role from the Web application's own deployed assembly. The existing production image must include that assembly and all runtime dependencies required by both roles. Tests inspect the publish/image staging descriptor and run the process fixture against the packaged artifact; this block does not introduce a second publish or image.

Alternative: ship a dedicated worker binary or image. Rejected as a deployment architecture change outside this block and contrary to the master plan's initial composition decision.

## Risks / Trade-offs

- [A child prerequisite is missing in a previously Web-only-valid package] → Fail during startup validation with a safe actionable diagnostic; verify publish/image contents before rollout and do not silently use in-process.
- [Startup validation accidentally constructs a backend or geodata graph] → Use metadata/registration/descriptor validation plus fail-on-resolution factories in composition tests.
- [Default selection weakens the empty scheduled gate] → Retain detector-before-resolution order and assert zero backend, launcher, protocol, and geodata effects.
- [Child lifecycle differs from the temporary in-process route] → Run the parity matrix for state, cancellation, terminal cleanup, and retriggering, with child-specific transport failures remaining visibly Failed.
- [The emergency seam becomes a permanent hidden feature] → Keep it code-only, document that it requires rebuild/revert, and gate block 38 immediately after default rollout evidence.

## Migration Plan

1. **Entry gate:** Require blocks 33–35 applied and passing; require block 36's separately owned empty-schedule regression passing; require Phase 4 launcher/protocol/cancellation/classifier/advisory-lock process coverage passing. Re-read the applied APIs and stop for reconciliation rather than inventing replacements.
2. Verify production publish/image staging contains the same application assembly, internal-worker role, runtime configuration, and native/managed dependencies used by the launcher.
3. Add startup/composition tests, then change only the ordinary internal selection default to ChildWorker; leave the explicit code-only InProcess seam intact.
4. Run the full selection, manual, eligible scheduled, empty, cancellation/state parity, selected-only DI, prerequisite-failure, packaging, and process-fixture matrix plus npm run test.
5. **Rollout gate:** Do not advance to block 38 unless default child startup and all mandatory process outcomes complete with exact state finality, no orphan process/stream/activity, successful retrigger, and no in-process/geodata resolution.
6. **Rollback criteria:** Roll back block 37 if the packaged child prerequisites fail, mandatory fixture/default-path tests fail, lifecycle/state parity regresses, cleanup leaves owned resources, or an operationally supported environment cannot start the child. Rollback is a source-level revert or explicit internal composition change followed by rebuild/redeploy; never an automatic per-run fallback or public runtime toggle. Preserve diagnostics and evidence before rollback.
7. After stable default-path evidence, block 38 removes the enum/switch, in-process production adapter/registration, and emergency seam. Block 39 then enforces the Web geodata boundary.

## Audit Reconciliation

Block 36 must be applied first. Preserve four distinct outcomes: authoritative committed worker terminals; local admission rejection (no child); advisory Busy (the canonical failed child terminal with no eligibility and four zero counts); and forced raw kill, which is transport evidence classified through block 30 and is not itself a terminal. No fallback, retry, replay, or in-process execution follows any of them.

