## Context

See [proposal.md](proposal.md) for motivation and [specs/internal-worker-host/spec.md](specs/internal-worker-host/spec.md) for normative behavior. Finalized block 18 selects the exact private `--internal-worker` role before builder creation. Block 19 exposes builder-neutral shared/internal-worker registrations and preserves executor/resource identities without constructing a host. Blocks 15 and 17 define ready, execute, cancel, sequencing, and EOF semantics as pure contracts; blocks 21–23 still own concrete stdout emission, stdin reading/control-loop mechanics, and exit mapping.

The executable remains a Web SDK assembly, but that does not require the selected worker role to build an ASP.NET host. The finalized Phase 2 executor already owns one processing pass and its reporter/session owns accepted-run terminal events.

## Goals / Non-Goals

**Goals:**
- Build and run one `IHost` for InternalWorker through Generic Host APIs without creating ASP.NET Web hosting.
- Define deterministic startup, readiness, request-acquisition, one-execution, shutdown, terminal-coordination, and disposal ordering.
- Make host/request cancellation linkage and pre-request finality explicit while preserving the existing request/result/event identities.
- Keep lifecycle logic testable with in-memory collaborators and no console, ports, external stores, geodata, scheduler, or UI.

**Non-Goals:**
- Do not implement the NDJSON reporter/emitter, canonical stdout writes, sequence assignment, or flushing owned by block 21.
- Do not implement bounded stdin frame reading, validation/control pumping, or console input-loop mechanics owned by block 22.
- Do not select process exit codes or reinterpret outcomes owned by block 23.
- Do not launch a child process, add advisory locks, modes, Kestrel, ports, endpoints, scheduler behavior, UI/state projection, or generalized worker job kinds.
- Do not eagerly initialize the country index, DuckDB, downloads, or live database work at readiness.

## Decisions

### Build InternalWorker with HostApplicationBuilder and IHost

After the finalized role parser returns InternalWorker, use `Host.CreateApplicationBuilder`/`HostApplicationBuilder`, apply block 19's shared and internal-worker roots, register one worker lifecycle hosted service, and build an `IHost`. The consumed private selector is not forwarded as a Generic Host configuration argument. The Web branch continues to receive its original unchanged arguments and construct the existing `WebApplication`. The outer worker runner owns starting, completing stop, and asynchronously disposing this one host/provider; Generic Host lifetime, an external caller, or the lifecycle's one `StopApplication()` call may initiate stopping. The hosted lifecycle never disposes the host that is running it.

The worker builder configures host/framework logging to stderr at construction time. It does not configure URLs, Kestrel, routing, static assets, Razor, Blazor, antiforgery, Data Protection, the Web scheduler/coordinator, or UI state. Block 21 later becomes the only stdout writer.

Change 20 also supplies an explicit transitional production registration set because blocks 21 and 22 have not yet supplied the concrete worker reporter/readiness emitter or request source. This set reports the stable safe pre-request outcome `worker-transport-not-configured`, publishes no readiness, accepts no request, resolves no worker reporter, writes no stdout, and lets the lifecycle stop normally. Blocks 21 and 22 replace those registrations rather than wrap them. It is not a dummy EOF, a no-op reporter, or successful readiness, and block 23 remains responsible for mapping the recorded outcome to a process exit code.

Alternative: use `WebApplication.CreateBuilder` and omit endpoint mapping. Rejected because ASP.NET server/configuration facilities would still enter the worker graph. Alternative: add a second executable. Rejected because the migration deliberately uses one image and assembly.

### Use one one-shot hosted lifecycle service

Register one dedicated `BackgroundService` (exact type name follows the applied code) as the worker orchestration owner. It first awaits the Generic Host `ApplicationStarted` boundary so readiness cannot race host startup. It then creates one `AsyncServiceScope`, resolves the pre-request lifecycle collaborators there, and runs finalized required asynchronous initializers. When worker transport is configured, it next publishes readiness through an abstraction and only after readiness completion asks the request source for the initial execute lease. Accepted-only collaborators, including `IProcessingEventReporter`, are resolved only after a lease is accepted, so the transitional production path does not require or fabricate a reporter.

The required initializer set is the one established by applied prerequisites, including skipped-store initialization if the finalized executor requires it. The transitional production path performs that required lightweight initialization, then ends with `worker-transport-not-configured` before readiness publication and request acquisition. Readiness means host/service graph and required lightweight durable state can accept a request; it is not a geodata warm-up barrier. Heavy lazy initialization remains at executor/operation first use.

A startup, readiness, or acquisition failure is reported through the scoped pre-request coordinator once that coordinator has been acquired. Scope creation or collaborator-resolution failure that occurs before acquisition of that coordinator is classified as host infrastructure failure and proceeds directly through structured cleanup; the lifecycle cannot invoke a collaborator that was never acquired. Generic Host stopping while startup, readiness, or acquisition is pending is cancellation-driven cleanup, not fabricated EOF or pre-request failure finality.

Alternative: publish ready from registration or the service constructor. Rejected because DI construction is not successful startup and cannot await initialization. Alternative: make every execution service scoped. Rejected because block 19 already fixes singleton ownership; the scope is a lifecycle/disposal boundary and must not duplicate singleton state.

### Model request acquisition as a transport-neutral lease

Block 20 introduces or consumes a narrow request-acquisition seam, implemented in tests by an in-memory fake and later in block 22 by the bounded stdin reader/control pump. Its initial wait has three closed outcomes: accepted execute lease, clean EOF before request, or safe pre-request failure. An accepted lease exposes the exact immutable `ProcessingRunRequest`, a cooperative cancellation token driven by valid block-17 cancel commands, and async completion/disposal coordination for the later control pump. It does not parse bytes itself.

The host asks for an initial request exactly once. After acceptance it invokes the finalized executor exactly once and never returns to initial acquisition. Clean EOF after acceptance is represented by the lease as “no more controls,” not cancellation. A pre-request EOF/failure creates no request, session, executor invocation, or run-terminal event.

Alternative: have the hosted service call `Console.In.ReadLineAsync`. Rejected because it would steal block 22's byte bound, framing, validation, and concurrent cancellation-loop responsibilities. Alternative: allow a reusable loop. Rejected because v1 and process isolation require one request then exit.

### Link host and request cancellation only after acceptance

Create one linked `CancellationTokenSource` from the hosted service stopping token and accepted lease cancellation token, and pass its token to the executor. This preserves block 17's latched pre-execution cancel and makes Generic Host shutdown cancel active work. Before acceptance, the host stopping token alone cancels startup/readiness/request waiting.

Use Generic Host's lifetime integration for SIGTERM/SIGINT; add no custom signal registrations. The lifecycle catches only cancellation attributable to its active linked/host token according to the finalized cancellation taxonomy. Unrelated `OperationCanceledException` remains failure. Host shutdown timeout policy is not redefined here; the worker awaits executor and coordination through normal Generic Host stopping semantics.

Alternative: pass only the request token. Rejected because container/process shutdown would not reach execution. Alternative: interpret stdin EOF as cancellation. Rejected by block 17.

### Separate domain terminal production from host finality

The executor and finalized reporter/session remain the only owners of run-started/progress/terminal domain events. Add transport-neutral lifecycle/outcome hooks around the host: readiness publication; pre-request EOF/failure; and accepted execution completion/infrastructure failure. The accepted-completion hook can await pending transport flush/finality and hand one host outcome to block 23 later, but it cannot emit a second terminal or fabricate one when no request exists.

Every hook is awaited once. A hook failure is host infrastructure failure and does not cause a retry. The host calls `StopApplication()` in unconditional finalization only after the applicable hook settles.

Alternative: have the host always emit terminal from the returned result. Rejected because executor/reporter terminal ownership is already finalized and duplicate terminals violate protocol lifecycle. Alternative: treat every pre-request problem as a failed run. Rejected because block 7 creates identity only for an accepted request.

### Dispose from the inside out

After any externally initiated stopping request is observed, the lifecycle-owned finalization actions have one observable order: settle executor; await accepted/pre-request coordination when applicable; mark the accepted lease terminal and await its control-pump completion as defined by the seam; dispose the linked CTS and lease; asynchronously dispose the execution scope; call `StopApplication()` exactly once; then let the outer worker runner complete host stopping and asynchronously dispose the host/provider so singleton Npgsql/native resources are released. Scope disposal before the lifecycle's `StopApplication()` call is authoritative. If Generic Host lifetime or an external caller already initiated stopping, that call is idempotent rather than a second stopping transition.

The implementation must tolerate failures at every earlier stage and dispose only resources actually acquired. It must never perform a second wait or execution during cleanup. Generic Host stopping before acceptance has no EOF/failure hook; it cancels pending work and follows cleanup. A primary lifecycle failure remains authoritative if cleanup also fails, with the cleanup problem classified separately as host infrastructure failure.

Alternative: resolve scoped collaborators from the root and rely only on process exit. Rejected because tests and in-process host disposal must deterministically release managed/native resources even before OS reclamation.

### Test the real host lifecycle with in-memory boundaries

Build the worker `IHost` using explicit test registration replacements for required initialization, readiness, initial request lease, executor, reporter, terminal coordination, clock/reporting where required, and disposable sentinels. These are internal production worker-only extension contracts for blocks 21–23, not production-only test APIs. Drive ordering with `TaskCompletionSource` gates and cancellation tokens, not sleeps. Start and stop the host in-process with a test lifetime; do not invoke real console lifetime signals, stdin/stdout, Kestrel, PostgreSQL, SQLite, geodata, DuckDB, or downloads.

Descriptor tests supplement block 19 by proving the built host has no server/Web/scheduler/UI services. Lifecycle tests prove host-start-before-ready, init-before-ready, ready-before-request-wait, one executor call, exact identity, cancellation linkage from both lease and host stop, EOF/failure no-work behavior, terminal-hook uniqueness, one-shot stop, and disposal order. A production-registration test proves the transitional host reaches `worker-transport-not-configured` without readiness, acquisition, reporter resolution, or stdout. Logging capture proves host logs target stderr policy while stdout remains unclaimed until block 21.

## Risks / Trade-offs

- [Generic Host default logging could contaminate stdout] → Replace/configure console logging for the worker builder so all host/application logs use stderr before any hosted service starts; reserve stdout for block 21.
- [Readiness accidentally becomes expensive warm-up] → Limit it to finalized required startup initializers and assert heavy geodata/native/database work remains lazy.
- [A terminal hook duplicates the reporter terminal] → Define host hooks as observation/flush/outcome coordination only and test exactly one domain terminal owner.
- [Shutdown races request acceptance] → Use one lease acquisition result and linked tokens; once acceptance commits, preserve that immutable request and execute/coordinate it once, possibly already cancelled.
- [Cleanup hides an earlier failure] → Record the primary lifecycle outcome once, observe cleanup failures through the host infrastructure path, and never retry execution or terminal reporting.
- [Applied prerequisite names/lifetimes differ] → Re-read blocks 11, 15, 17–19 source/tests immediately before implementation and adapt to their exact contracts rather than introducing parallel identities or ownership.

## Migration Plan

1. Re-read the applied role parser, composition roots, executor/reporter, protocol, and request/control APIs; stop if required prerequisites are absent.
2. Add the transport-neutral lifecycle/request-lease/coordination seams and deterministic in-memory fakes without console transport.
3. Add the Generic Host builder path, stderr host logging policy, one async execution scope, and one hosted lifecycle service.
4. Implement startup/readiness/request/executor/cancellation/coordination/stop/disposal ordering and no-Web guards.
5. Run focused host tests, the default test suite, strict validation, final status, and a scope diff limited to block 20.

Rollback removes the worker Generic Host path and lifecycle seams while leaving role parsing, composition roots, and pure protocol/request contracts intact. No persisted data or public configuration migration is introduced.
