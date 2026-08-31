## Context

See `proposal.md` and `specs/processing-run-coordination/spec.md`. The current source is still pre-Phase-2: `ProcessingBackgroundService` owns one semaphore, manual-only `_runCts`, cron execution, and direct pipeline work. Manual contention returns silently; scheduled contention writes `Scheduled run skipped because a processing pass is already in progress.` Manual dispatch is fire-and-forget, scheduled execution is awaited under the host token, and the Dashboard shows Cancel for both even though it currently cancels only manual execution.

Blocks 7–12 are planning artifacts in this checkout, not source APIs. Their required final shape is: accepted-only immutable requests, a singleton reporter/state adapter armed after `MarkPending()`, an awaitable singleton-compatible executor that owns domain terminal reporting, and a minimal scheduler-facing execution-start seam. Block 12 is concurrent and must be applied first, then its exact API and DI registrations must be re-read before block 13 implementation.

## Goals / Non-Goals

**Goals:**
- Give one singleton control-plane component atomic local admission, active-handle, dispatch-task, cancellation, retrigger, and shutdown ownership.
- Preserve block-9 pending/arming order and block-11's sole ownership of domain terminal results while making setup and infrastructure-fault cleanup leak-safe.
- Preserve trigger-specific contention presentation while intentionally making the already-visible Cancel command effective for scheduled runs.
- Keep the coordinator's concrete, Dashboard, scheduler-start, and hosted-lifecycle aliases reference-identical to one coordinator object; separately keep concrete `ProcessingBackgroundService` and its `IHostedService` alias reference-identical to one scheduler object. The coordinator and scheduler remain distinct.

**Non-Goals:**
- Do not calculate cron occurrences, delay schedules, detect due times, or alter the block-12 scheduler loop. Do not expose `RunOnce` through this Web coordinator; a separate run-once deployment invoker retains the block-7 request/executor trigger.
- Do not move, redesign, test exhaustively, or otherwise own the block-11 execution pipeline; block 14 retains broad executor-only coverage.
- Do not define worker protocol, serialization, process launch/session behavior, force termination, output draining, or worker shutdown escalation.
- Do not add PostgreSQL advisory locking or any cross-process exclusion; block 31 owns that distributed boundary.
- Do not move skipped-store startup initialization into run admission or change settings, persistence, geodata, or processing outcomes.

## Decisions

### Use one gated active handle instead of a semaphore held across async execution

The coordinator keeps a short synchronous gate around an admission-open flag and an optional active handle. The handle contains the immutable request, one coordinator-owned CTS, a completion signal/owned task, and cleanup identity. The idle-to-active transition creates and publishes the request plus CTS atomically; no external callbacks execute under the gate. This closes the current lost-early-cancel window and lets cancellation capture the exact live source without racing replacement.

The accepted call then performs the required handshake outside the gate: `MarkPending()`, arm the exact request on the reporter adapter, launch one guarded execution path, store/observe its completion, and only then return Accepted. Any failure after reservation enters the same exact-handle cleanup path. Alternative: move the existing `SemaphoreSlim` into the coordinator. Rejected because a semaphore does not model stopping, request identity, stale cleanup, or atomic CTS publication and encourages holding ownership across callbacks.

### Preserve distinct caller completion contracts over one admission primitive

Consume the finalized block-12 scheduler contract exactly: its asynchronous result is `RejectedAlreadyRunning` or `AcceptedAfterTerminal`. The coordinator's private/common admission primitive can produce an accepted handle with immutable `Manual` or `Scheduled` request identity plus an internal completion signal, but it must not expose mutable execution internals or a `RunOnce` admission surface. The block-12-facing scheduled method maps rejection immediately and, when accepted, awaits that exact handle through terminal cleanup before returning `AcceptedAfterTerminal`. This preserves configuration reevaluation and occurrence behavior without moving cron ownership. Its stopping token participates in both the accepted run's linked cancellation and the await.

Dashboard Run Now uses a separate narrow manual surface over the same primitive and returns after dispatch ownership is established, preserving prompt behavior; it receives enough Accepted/AlreadyRunning/Stopping information not to treat a cleanup-window or shutdown rejection as success. The coordinator still retains and observes every execution task. Scheduled AlreadyRunning retains the unchanged skipped message once, while manual AlreadyRunning remains silent. Alternative: force both callers to await terminal or both to return at admission. Rejected because either would change block-12 scheduler sequencing or the characterized Dashboard interaction. Alternative: normalize both contention logs. Rejected because contention logs are characterized control-plane behavior and are not run events.

### Create identity only after winning admission and prepare projection in the established order

Inside the successful idle-to-active transition, create a fresh non-empty `Guid`, construct the finalized block-7 request with the caller's trigger, and create the live CTS. This is after rejection checks, so AlreadyRunning and Stopping create no identity. Publish the handle before calling `MarkPending()`; then call `MarkPending()`, arm the exact singleton block-9 adapter with the same request, and dispatch the executor with the reporter alias and coordinator token. Execution cannot open a session before arming.

Block 9 describes `MarkPending()` before request construction as observable sequencing, but the request is not visible until after admission and publishing it first is required to make immediate Cancel safe. The externally required order remains admission → pending → arm → execution. Apply must use finalized adapter names rather than add a second correlation vocabulary.

### Give both trigger paths one coordinator-owned CTS

Create one CTS per accepted run and use its token for the executor regardless of trigger. Link it to the application-stopping token (or have the shutdown callback cancel it through the same active handle) using the finalized host-lifetime shape. `CancelActiveRun` captures the current handle under the gate, requests cancellation outside the gate, and returns whether it targeted a run; disposal occurs only after exact-handle detachment. Object-disposal races are handled idempotently.

This intentionally corrects the current mismatch where Cancel is visible during a scheduled run but only manual execution owns `_runCts`. Alternative: retain trigger-aware manual-only cancellation. Rejected because it preserves a misleading command and complicates one-active-run ownership; this behavioral normalization is called out in proposal, spec, Dashboard tests, and release-facing review if implementation deems docs necessary.

### Keep domain terminal reporting in the executor and add guarded arm abandonment

The block-11 executor/session emits exactly one Completed, Cancelled, or Failed result and the block-9 adapter releases its arm after terminal projection. A matching returned result therefore causes no coordinator terminal event. The coordinator merely observes it and proceeds to handle cleanup.

Setup failure, adapter/reporter infrastructure failure, or a synchronous dispatch fault may leave pending state or arm ownership without a domain terminal result. The finalized adapter must expose, or block 13 must add on that same singleton instance, a narrow identity-checked control-plane abandonment operation. It closes only the matching pending/armed projection, unwinds activity, and makes later arming possible; it does not synthesize `ProcessingRunResult`, recurse through a broken reporter session, or directly mutate `ProcessingState` from the executor. It is idempotent if terminal projection already released the arm. Internal projection callbacks may throw, so coordinator handle release remains in `finally`; adapter internal ownership release must likewise use `finally` so a subscriber cannot permanently retain correlation.

Alternative: release admission and leave the adapter armed after reporter failure. Rejected because the next accepted trigger would fail arming and retrigger would be impossible. Alternative: fabricate a Failed result in the coordinator. Rejected because terminal result/accounting belongs to execution/session and may be unavailable after reporter failure.

### Detach by request identity before disposing and admitting a retrigger

The guarded execution path catches/logs infrastructure exceptions, invokes identity-checked abandonment only when terminal reporting did not finish, and in `finally` removes the active handle only if reference/request identity still matches. It then disposes the CTS once and completes the owned completion signal. Every accepted path is observed. Cleanup never clears a newer run, and admission is released only after the executor and reporter have stopped using the old handle.

There can still be a brief projection-idle/control-plane-cleanup interval because block 9 completes Web state and summary before `ExecuteAsync` returns. The explicit admission result prevents that interval from looking accepted; deterministic retrigger tests synchronize on coordinator cleanup, not sleeps or `ProcessingState.IsRunning` alone. Alternative: release before terminal projection. Rejected because a new arm could overlap late events and violate the single-run correlation invariant.

### Make the coordinator a singleton host-lifecycle participant beside the scheduler

Register one concrete `ProcessingRunCoordinator` singleton. Factory-alias the Dashboard-facing contract, finalized block-12 execution-start contract, and any coordinator hosted-service registration to that exact coordinator instance. Keep concrete `ProcessingBackgroundService` factory-aliased as `IHostedService` to its exact scheduler instance, reduced by block 12 to startup/schedule duties. Coordinator and scheduler are distinct; dependency direction is scheduler → scheduler-start contract → coordinator, never coordinator → scheduler. It depends on the start contract; the coordinator must not depend on the scheduler, avoiding a cycle.

Register an application-stopping callback that atomically closes admission and requests active cancellation as soon as shutdown begins. The coordinator's `StopAsync` (or finalized equivalent lifecycle hook on the exact instance) repeats that idempotently and awaits the active completion signal subject to the supplied shutdown token. Register/start ordering must make the coordinator available before the scheduler and stop the scheduler's request loop before coordinator drain, while the admission flag remains the authoritative race boundary. This block establishes only local cooperative draining; block 29 later extends the same owner for worker grace/kill/output cleanup.

Alternative: let only the scheduler's stopping token cancel scheduled execution. Rejected because Dashboard/manual requests could still race shutdown and manual execution would remain outside host lifetime. Alternative: create a second coordinator instance through `AddHostedService<T>()`. Rejected because scheduler, Dashboard, and shutdown would observe different admission state.

## Risks / Trade-offs

- [Block 12 applies a differently named or shaped start contract] → Apply it first, re-read source/tests/DI, and implement or factory-alias that exact contract rather than editing block 12 or adding a competitor.
- [Projection becomes idle just before coordinator cleanup] → Make admission rejection explicit, keep exact-handle ownership through executor return, and gate retrigger tests on cleanup completion rather than UI timing.
- [`MarkPending`, arm, or an `OnChanged` subscriber throws] → Put all post-reservation work inside one guarded path; make projection abandonment and active-handle cleanup idempotent and `finally`-based.
- [Cancellation races CTS disposal] → Capture the exact handle under the gate, cancel outside it, detach by identity, and dispose once after no execution path can use the source.
- [Shutdown and trigger admission race] → Linearize both on the same short gate and test both possible legal outcomes with deterministic barriers.
- [Scheduled cancellation is a behavior change] → Treat it as an intentional correction to an already-visible Dashboard command and cover it explicitly; do not silently claim exact cancellation compatibility.
- [Local exclusion is mistaken for deployment-wide exclusion] → Name and document the boundary as process-local; defer PostgreSQL advisory locking to block 31.

## Migration Plan

1. Apply blocks 7–11, apply concurrent block 12, and re-read the finalized request, adapter arm/abandon, executor, execution-start, host-lifetime, DI, and test APIs. Stop if those prerequisites are absent rather than recreating them.
2. Characterize current silent manual contention, scheduled skipped logging, immediate pending timing, prompt Run Now return, ineffective scheduled Cancel, terminal/release ordering, and hosted-service identity before ownership moves.
3. Add the singleton coordinator and exact interface/host aliases; implement gated reservation, active CTS publication, pending/arm handshake, owned executor dispatch, cancellation, abandonment, exact cleanup, and shutdown quiescence.
4. Route the finalized block-12 scheduler start seam and Dashboard Run Now/Cancel through the coordinator. Remove lock/CTS/dispatch ownership from the scheduler service while retaining startup initialization, cron calculation, and control-plane schedule logs there.
5. Add deterministic gated coordinator, Dashboard-boundary, shutdown-race, and DI identity tests; run focused tests, the default suite, strict OpenSpec validation, and a scope diff.
6. Roll back by restoring the block-12 temporary in-process start adapter and previous Dashboard bindings. No data, settings, database, or protocol migration is required.
