## 1. Reconcile prerequisite contracts

- [x] 1.1 Treat blocks 18–24 as hard ordered prerequisites; re-read the applied block-24 `WorkerCommandInvocation` and blocks 15, 17, and 21–23 source APIs; record and use their exact descriptor, request codec, event codec/validator, identity, terminal, and exit types rather than introducing parallel contracts.
- [x] 1.2 Confirm the launcher remains a single callable service independent of coordinator selection, `ProcessingState`, graceful cancellation/escalation, crash classification, and retry policy.

## 2. Define launcher ownership contracts

- [x] 2.1 Define `IChildWorkerLauncher`, the discriminated `ChildWorkerLaunchResult`, and `ChildWorkerStartupObservation` so OS start failure is distinct from ready timeout/validation and execute write/flush failure.
- [x] 2.2 Define `ChildWorkerSession` with PID, exact request run/job identity, wait-only cancellation-aware startup/completion access, accepted terminal/raw exit observations, and idempotent asynchronous disposal without exposing `Process`.
- [x] 2.3 Define the serialized asynchronous accepted-event sink contract and immutable completion observation carrying stream finality, first protocol/sink observation, and bounded stderr tail/truncation without `ProcessingState` mapping or block-30 classification.

## 3. Implement process and stream lifecycle

- [x] 3.1 Add `IChildProcessFactory`/`IChildProcess` and the production `System.Diagnostics.Process` adapter that consumes the general `ChildProcessStartDescriptor`; keep launcher production entry points restricted to finalized block-24 `WorkerCommandInvocation`, redirects all three streams, transfers ownership atomically, and reports a safe typed start failure.
- [x] 3.2 Start stdout drainage, stderr drainage, and process-exit observation immediately after process creation; coordinate one completion only after exit and both stream pumps settle, including trailing bytes and faulted paths.
- [x] 3.3 Implement bounded incremental stdout framing through the shared Phase 3 codec and stateful validator, delivering each accepted event once in order while continuing drainage after invalid output or sink failure.
- [x] 3.4 Implement the fixed 65,536-byte stderr tail ring with total/truncation metadata and replacement-safe text snapshot while draining all stderr bytes.
- [x] 3.5 Implement the fake-time-driven 30-second ready deadline, then canonical one-time execute write and flush; keep stdin open after success and retain distinct timeout, validation, EOF/exit, write, and flush observations.
- [x] 3.6 Implement wait-only caller cancellation and one idempotent disposal lifecycle that closes stdin, suppresses callbacks, awaits the existing pumps/exit, and releases process resources without cancel commands or process termination.

## 4. Verify deterministic behavior

- [x] 4.1 Add a gated in-memory process factory/process and fake clock covering start exception, PID/exit, controllable stdin/stdout/stderr, pump gates, stream faults, and exactly-once disposal without sleeps or real worker services.
- [x] 4.2 Prove both output pumps start before readiness/exit waits and that stdout events plus stderr larger than pipe capacity drain concurrently without deadlock.
- [x] 4.3 Test valid ready → successful accepted sink callback → one complete execute write/flush → stdin remains open, plus ready-sink failure with no execute, ready timeout, pre-ready EOF/exit, malformed/incompatible/oversized/not-ready output, and execute write/flush failure.
- [x] 4.4 Test ordered accepted-event delivery, sink failure with continued drainage, normal terminal plus raw exit capture, process exit before trailing stream EOF, and absence of crash/domain/`ProcessingState` classification.
- [x] 4.5 Test exact 65,536-byte stderr retention/truncation, invalid/trailing UTF-8 snapshot handling, pre-start launch cancellation, cancellation racing after successful start still returning an owned session, wait cancellation that leaves session active, and repeated asynchronous disposal sharing one lifecycle.
- [x] 4.6 Keep the real fixture executable, production process-boundary scenario matrix, and launcher integration tests that consume that fixture deferred to block 26; preserve and prove the exact general process seam for that future consumption, and do not add fixture-only behavior to production.
- [x] 4.7 Run focused launcher tests, the normal `npm run test` suite, `openspec validate 25-add-child-worker-launcher --strict`, and a clean change-status check.
