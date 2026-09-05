## 1. Reconcile Finalized Seams

- [x] 1.1 Re-read the applied block 15, 17, and 21–25 APIs and record the exact protocol owner project, descriptor constructor/factory, launcher/session result types, accepted-event sink, observation categories, byte limits, stderr capacity, and exit-code constants used by the fixture; stop rather than adding parallel contracts.
- [x] 1.2 Confirm a test-created general `ChildProcessStartDescriptor` can carry fixture-only discrete arguments without changing block 24's validated `WorkerCommandInvocation` resolver; if visibility is the only barrier, add the narrowest test visibility rather than relaxing production validation.
- [x] 1.3 Keep all implementation and test edits inside block 26 scope; do not edit block 27 state bridging or add block 28 cancellation policy, block 30 classification/UI projection, or block 32 PostgreSQL behavior.

## 2. Add and Stage the Fixture Executable

- [x] 2.1 Add `tests/ImmichReverseGeo.WorkerProcessFixture/ImmichReverseGeo.WorkerProcessFixture.csproj` as a `net10.0` console apphost project and include it in the solution without any production project referencing it.
- [x] 2.2 Reference only the finalized protocol-owning project/API surface needed for contracts, codecs, validators, and exit constants; prove the fixture entry point cannot enter production `Program`, dependency injection, worker services, PostgreSQL, or geodata loading.
- [x] 2.3 Add a build-only fixture project dependency and explicit Build/Publish staging in `ImmichReverseGeo.Tests` that copies the apphost plus all runtime/dependency files into the fixed `worker-process-fixture/` test-output subdirectory and fails when the expected artifact is missing.
- [x] 2.4 Add an exact-path cross-platform fixture locator for Windows, Linux, and macOS that validates the absolute staged apphost and working directory without PATH, current-directory, entry-assembly, or directory scanning.

## 3. Implement the Fixture Contract

- [x] 3.1 Implement strict closed fixture argument parsing for scenario token, absolute unique resource root, optional capture name, deterministic stderr byte count, sequence-fault subtype, terminal choice, and in-range exit code; reject duplicates, unknowns, relative paths, malformed numbers, and unsafe paths with a stable fixture-usage exit.
- [x] 3.2 Implement shared-codec helpers that emit canonical ready and valid run-scoped events with exact sequence/run correlation and read bounded execute/cancel frames transactionally from stdin; do not duplicate v1 DTOs, tokens, limits, or validators.
- [x] 3.3 Implement exact request capture using a same-directory temporary file, flush/close, and atomic rename before the post-request protocol handshake.
- [x] 3.4 Implement deterministic raw-output helpers for the minimal deliberate malformed UTF-8/JSON/framing, oversized, unknown compatibility, and sequence gap/replay faults while leaving all preceding valid frames shared-codec generated.
- [x] 3.5 Ensure stdout is reserved for protocol/fault bytes, fixture diagnostics use stderr, every write needed as a handshake is flushed, and no scenario uses `Thread.Sleep`, delay-based staging, fixed ports, mutable environment variables, or global scenario state.

## 4. Implement the Scenario Catalog

- [x] 4.1 Add `ready`, `success`, and `no-work` modes with ready-before-read, exact execute capture, deterministic accepted event order, canonical completed terminal, and exit 0.
- [x] 4.2 Add `pre-ready-crash` and `post-ready-crash` modes with bounded known stderr, explicit exit selection, post-request armed handshake where applicable, and no fabricated terminal.
- [x] 4.3 Add isolated `malformed`, `oversize`, `unknown`, and `invalid-sequence` modes with deterministic fault bytes/subcases and declared process exit behavior.
- [x] 4.4 Add `terminal-mismatch` subcases that emit completed/cancelled/failed as the final valid stdout frame and then return a separately selected contradictory exit code.
- [x] 4.5 Add `stderr-flood` with a known prefix, algorithmic body over 65,536 bytes, and known suffix beside a valid success stream, without buffering the entire flood in parent or child memory.
- [x] 4.6 Add `raw-exit` cases for 0, 2, 3, 4, 5, 6, 130, and one fixed unmapped 0–255 code, using managed return/exit rather than fail-fast or crash dumps.
- [x] 4.7 Add `cooperative-cancel` and `unresponsive` modes with execute/armed handshakes: the former accepts correlated cancel, emits one cancelled terminal, and exits 130; the latter may acknowledge observing cancel but remains alive without a terminal until externally killed.

## 5. Add Isolated Test Process Support

- [x] 5.1 Add a fixture descriptor factory that uses the staged apphost, absolute working directory, inherited environment, redirected streams, shell-free flags, and discrete non-secret scenario/resource arguments while bypassing—not modifying—the block 24 production resolver.
- [x] 5.2 Add a per-case resource lease with unique GUID directory, run ID, capture path, marker token, accepted-event collector, and PID/handle registration suitable for parallel MSTest execution.
- [x] 5.3 Add idempotent unconditional cleanup that closes accessible stdin, kills the registered live fixture process tree when needed, awaits block 25 exit/stdout/stderr finality, disposes the session once, deletes resources, and tolerates already-exited processes.
- [x] 5.4 Add bounded failure/cleanup watchdogs and an assembly-level last-chance fixture registry; use watchdogs only to diagnose/reap hangs, never to order expected test actions, and report unreaped PIDs as test failures.
- [x] 5.5 Add a minimal direct-process conformance helper only for fixture behavior block 25 cannot yet drive, retaining the same cross-platform staging, stream drainage, unique resources, and reaper guarantees.

## 6. Verify Fixture Conformance

- [x] 6.1 Test strict fixture CLI rejection and prove production application builds/publishes contain no fixture selector or fixture executable when test projects are excluded.
- [x] 6.2 Test valid codec reuse, ready-before-execute, exact atomic request capture, run-ID correlation, normal success, and canonical no-work completion without loading production services.
- [x] 6.3 Test every mapped raw exit and the unmapped exit directly, distinguishing fixture-selected codes from nonportable forced-kill codes.
- [x] 6.4 Test cooperative cancel reaches one cancelled terminal/130 and unresponsive mode reaches armed/cancel-observed handshakes but remains alive until the conformance reaper kills it; do not implement or assert production grace/escalation policy.
- [x] 6.5 Run multiple fixture instances concurrently and inject an assertion/early-abort path to prove capture isolation, idempotent cleanup, process-tree termination, stream finality, and no surviving registered PIDs.

## 7. Add Real Block 25 Launcher Tests

- [x] 7.1 Launch the staged `ready`, `success`, and `no-work` scenarios through the production block 25 adapter and assert startup, exact captured execute frame, callback ordering, terminal preservation, exit 0, and stream finality.
- [x] 7.2 Launch pre-ready and post-ready crashes and assert only typed startup/raw completion evidence, request handshake where applicable, no accepted terminal, and complete stdout/stderr drainage; leave cause classification to block 30.
- [x] 7.3 Launch malformed, oversized, unknown, and invalid-sequence scenarios and assert the first raw protocol/validator observation, callback suppression rules, continued drain, and exact exit capture without projecting a failed UI outcome.
- [x] 7.4 Launch terminal-mismatch subcases and assert the accepted terminal remains preserved beside the contradictory raw exit without choosing authority or classifying the contradiction.
- [x] 7.5 Launch stderr flood beside valid stdout and assert no deadlock, total byte count, truncation flag, exact 65,536-byte retained suffix/decoding, terminal, and raw exit.
- [x] 7.6 Launch raw-exit matrix cases needed to prove block 25 captures OS codes exactly; do not assert block 23/30 semantic classification or retry.
- [x] 7.7 Launch cancellation fixture modes only far enough to assert block 25 readiness/execute/armed raw evidence and cleanup; reserve cancel sending, grace, and escalation assertions for block 28.

## 8. Build and Validation

- [x] 8.1 Run a clean solution build, then focused fixture/launcher tests with `--no-build` to prove staged artifacts are available independently of test-time compilation.
- [x] 8.2 Exercise the test-project Publish staging path and run the fixture locator/smoke case from that output on the current platform; ensure the tasks/CI matrix names Windows, Linux, and macOS coverage.
- [x] 8.3 Run focused MSTests for block 26 and `npm run test`; confirm default Integration and Performance exclusions and no orphaned fixture processes after each run.
- [x] 8.4 Run `openspec validate 26-add-worker-process-fixture --strict` and `openspec status --change 26-add-worker-process-fixture`; reconcile every warning/error and review the final diff/status to confirm only change 26 planning or implementation files were touched.

## Applied prerequisite reconciliation

- Core owns `WorkerProtocolV1`, `WorkerProtocolCodec`, `WorkerProtocolMapper`, both stream validators, processing request/result payloads, and `WorkerProcessExitCodes`. The frame limit is `WorkerProtocolV1.MaxMessageBytes` (1,048,576 bytes); the launcher retains 65,536 stderr bytes.
- The finalized `IChildWorkerLauncher.LaunchAsync` takes only `WorkerCommandInvocation`. An internal `ChildWorkerLauncher.LaunchDescriptorAsync` extracts the same mechanics for the general `ChildProcessStartDescriptor` test seam. The production interface, validated selector, resolver, execution order, and runtime policy stay unchanged.
- The internal system adapter exposes its original `NativeProcess` as borrowed identity for fixture cleanup. The adapter remains the sole disposal owner; the fixture never rediscovers a process by PID or adds production termination policy.
- Tests consume `ChildWorkerLaunchResult.Started`, `ChildWorkerSession`, `IWorkerProtocolEventSink`, `ChildWorkerStartupObservation`, and `ChildWorkerCompletionObservation`; the fixture itself references Core only.
- Build/publish and focused fixture checks are required on Linux, Windows, and macOS in the change26 CI matrix.
- Existing executor characterization snapshots contain dot-decimal log strings. The complete local suite is run with `LC_ALL=en_US.UTF-8`; the initial macOS comma-decimal run exposed six pre-existing locale-sensitive assertions, while the English-locale run passed all 1,206 tests without changes to those contracts or production formatting.

## Final local validation

- Clean solution build and final build completed without warnings or errors.
- Focused Change26 tests passed 60/60 with `--no-build`; the same 60/60 passed directly from the Release publish output.
- `LC_ALL=en_US.UTF-8 npm run test` passed 1,208/1,208 with the standard Integration/Performance exclusions.
- No fixture processes or per-case resource directories remained after the final published-output run.
- Brooks review verified exact process identity, retryable cleanup, actual failure unwinding, completed-faulted drain cleanup, and the existing production policy boundary with no remaining findings.

- Feature-branch CI passed all six jobs for implementation commit `e1d4c6b80a27ef2f765833bef8336df2002a55f6`: https://github.com/crazz/immich-reversegeo/actions/runs/33985215502. This includes the full Linux test/Docker gate and Build/Publish fixture tests on Linux, Windows, and macOS.
