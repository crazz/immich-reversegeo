## Context

See [proposal.md](proposal.md) and [the Run-once specification](specs/run-once-deployment-mode/spec.md). Block 40 resolves one immutable public deployment mode after block 18's private-role preflight: Standard and Web-only map to Web, while Run-once reaches the existing typed RunOnce boundary. The reserved exact `--internal-worker` role still wins without reading public mode configuration.

The prerequisite worker path already separates reusable execution from control transport. Block 11's executor accepts one immutable request, reporter, and cancellation token, opens the reporting session before its authoritative count, owns zero-work and the full heavy pass, and returns Completed, Cancelled, or Failed. Blocks 19–20 define shared/worker registrations and a one-shot internal Generic Host; blocks 21–22 add controller-only NDJSON/stdin ownership; block 23 maps managed worker outcomes; and block 31 inserts the PostgreSQL advisory lock immediately after run-started and before the count/domain work. Run-once must reuse the execution, initialization, lock, and outcome facts without pretending to be a controller-launched private worker.

## Goals / Non-Goals

**Goals:**
- Build one public non-Web composition that executes the authoritative pipeline in the current process and exits after exact cleanup.
- Preserve request/reporter/executor/lock identity and ordering, including exactly one authoritative count and ordinary zero-work completion.
- Provide stable process exits, cooperative signal cancellation, safe operator logs, and a Docker Compose/cron workflow.
- Make absence of Web, scheduler, private protocol, child launch, port use, and retry structurally testable.

**Non-Goals:**
- Do not change deployment-mode parsing, the private role grammar, worker protocol, executor pipeline, advisory key/lease semantics, processing settings, database predicates, or persistence behavior.
- Do not add a Run-once CLI alias, stdin request, controller, child process, local scheduler, UI/status surface, Kestrel endpoint, health port, daemon loop, queue, catch-up, or retry policy.
- Do not edit Standard or Web-only composition, add Docker image smoke orchestration owned by block 46, or add UI behavior owned by block 44.

## Decisions

### Branch from the immutable role decision before any Web builder

The top-level entry point consumes block 40's existing startup snapshot and block 18's selected role. RunOnce dispatches to a dedicated asynchronous top-level runner before `WebApplication.CreateBuilder` or any Standard/Web-only registration. The runner may use `HostApplicationBuilder`/Generic Host to obtain logging, lifetime signals, DI, and deterministic disposal, but it never calls Web-host defaults and never registers server features. It returns an integer only after host/provider disposal; services never call `Environment.Exit`.

The exact private preflight remains ahead of block 40. Do not add another scan for `--internal-worker` or a mode/role enum. Ordinary arguments remain the finalized role selector's unchanged argument sequence and can be passed to the non-Web host only through the established startup input; they are not a public Run-once command grammar.

Alternative: build the Web application and stop it after one pass. Rejected because Kestrel, endpoint/UI, port, and Web-only service initialization would already occur. Alternative: invoke Run-once through an application-owned CLI. Rejected because block 40 makes the environment variable the sole public mode source.

### Compose a direct one-shot execution graph, not the private worker protocol

Extract or consume the smallest landed registration slices from block 19/38: shared configuration/path/database services, required worker startup initialization, worker-only executor and heavy collaborators, advisory-lock services, and a Run-once reporter/outcome coordinator. Reuse exact singleton aliases and one Npgsql data-source owner. Do not call the InternalWorker host registration wholesale if it brings readiness, stdin, NDJSON, protocol emitter, controller leases, or output-transport finalization; do not call Web registration if it brings Razor, `ProcessingState`, coordinator, detector, scheduler, child backend/launcher, bridge, or host-child shutdown services.

After mandatory pre-run initialization succeeds, the runner creates one fresh `ProcessingRunRequest` with a non-empty ID and `ProcessingRunTrigger.RunOnce`. It opens one async execution scope if finalized worker lifetimes require one, resolves the exact executor/reporter once, and invokes once with the host stopping token. No detector is used: the executor/lock path performs exactly one authoritative count. The executor's existing zero gate preserves no-work laziness.

Alternative: launch `--internal-worker` as a child and write an execute frame. Rejected because the public process has no controller need, would double process lifetime/memory, and violates the requested same-process contract. Alternative: call the Web coordinator. Rejected because it would resolve a child-only backend and local control/UI lifecycle rather than direct RunOnce execution.

### Reuse the lock inside the accepted executor session

Do not pre-acquire the advisory lock in the runner. The Run-once reporter accepts run-started, then the established block-31 executor gate performs non-blocking acquisition before eligibility, snapshots, geodata, or mutation. The acquired lease retains its dedicated session through protected execution, terminal reporting, and lock cleanup. Busy uses the existing failed terminal with zero domain counts and contributes typed Busy; open/acquisition/loss/unlock ambiguity contributes Infrastructure; cancellation remains cooperative. This preserves one terminal owner and the exact key/lease/pool-safety implementation.

Alternative: check the lock before creating the request. Rejected because block 31 requires a valid accepted session and failed busy terminal after run-started. Alternative: add an in-process semaphore. Rejected because each Run-once process makes only one attempt and cross-process exclusion is the relevant boundary.

### Use a Run-once event reporter with ordinary operator output

Supply the executor with one validating Run-once reporter/session that preserves request identity, event order, activity cleanup, accounting, and terminal finality but projects those events to human-readable operator logs rather than protocol envelopes. Configure line-oriented logging so informational lifecycle/progress/no-work/completion output goes to stdout and warnings/errors go to stderr. A top-level stderr writer that outlives provider disposal emits one bounded safe final classification summary for orderly nonzero exits.

Do not register or initialize block 21's ready frame, sequence allocator, NDJSON codec/emitter, stdout-exclusive protocol stream, or block 22's stdin loop. Standard input is ignored. Exit status, not log text, is the stable automation surface. Log writes are best effort: because there is no protocol delivery guarantee or controller stream, their failure is not code 6 and never retries execution. Preserve all existing safe-message boundaries and never log raw configuration/environment values, arguments, credentials, SQL, exception dumps, or connection strings.

Alternative: emit the existing worker NDJSON directly. Rejected because there is no readiness/request handshake or controller and exposing the private transport would make operators parse an internal protocol. Alternative: send every log to stderr. Rejected because successful cron/job output is ordinary output; severity routing keeps failures separately observable without turning text into an API.

### Apply the established outcome taxonomy only where the public role has facts

Use the existing dependency-light outcome accumulator or a thin Run-once adapter over the same values and precedence; do not create conflicting numeric constants. The public Run-once subset is:

| Exit | Run-once classification |
|---:|---|
| 0 | Completed, including authoritative zero work |
| 2 | Existing pre-host invalid public mode/private role syntax only |
| 3 | Advisory-lock Busy |
| 4 | Accepted executor/domain Failed |
| 5 | Run-once startup, required configuration/dependency, config/data initialization, database/lock infrastructure, host lifecycle, scope/provider, lock, or other cleanup failure |
| 130 | Cooperative cancellation, SIGINT/SIGTERM, or host stopping |

For managed Run-once facts, preserve block 23 precedence after removing inapplicable input/output-protocol categories: Infrastructure > Busy > DomainFailed > Cancelled > Completed. A late cleanup failure can therefore change a 0/3/4/130 final process classification to 5 without rewriting an already accepted terminal result. Run-once never selects 6. Abrupt signal kill, fail-fast, stack overflow, or failure that cannot reach managed finalization remains an observed platform status rather than a promised code.

Alternative: return only success/failure. Rejected because cron and Compose need to distinguish Busy, processing failure, infrastructure failure, and cancellation. Alternative: map Busy to success or retry internally. Rejected because contention means no pass occurred and block 31/23 reserve code 3 without authorizing retry.

### Let host lifetime own signals and clean up exactly once

Use Generic Host console lifetime rather than custom signal handlers. Link the host stopping token into lock acquisition and executor execution. Cancellation before session entry creates no terminal; cancellation after run-started follows the executor's existing Cancelled result and retains committed partial effects. Always await applicable terminal handling, advisory lease finalization, async execution-scope disposal, host stop, and provider disposal before returning the final code. Run-once has no child process tree, kill escalation, stream drain, scheduler wake-up, or second request loop.

Record startup/initialization failures before request creation as Infrastructure/5 with no fabricated processing terminal. Failures after session entry use the reporter/executor terminal when that owner remains healthy and independently contribute the process classification. Cleanup uses predecessor-owned bounded non-cancelled cleanup tokens where required, especially for advisory unlock.

Alternative: install a direct `Console.CancelKeyPress` or POSIX handler. Rejected because duplicate signal ownership creates races with Generic Host and inconsistent cross-platform tests.

### Document an ephemeral Compose service and external cron boundary

Add public Docker-first instructions for an optional Run-once service/job that reuses the neutral image/entrypoint, database environment and Immich network, separate `/config` and `/data` mounts, sets exact `IMMICH_REVERSEGEO_MODE=run-once`, publishes no ports, and uses no automatic restart. Show a disposable invocation such as `docker compose run --rm <run-once-service>` for cron. Keep the persistent Standard example's omission/default unchanged and never advertise the private token.

State that one scheduler launch equals one attempt; overlap returns 3; the application neither retries nor rolls back prior effects; and any later launch is a new request against current database state. Orchestrator retries are an explicit operator policy outside this capability, not implied safe by an exit code.

Alternative: bake a Run-once command or image. Rejected because block 40 defines one image and entrypoint. Alternative: add `restart: on-failure`. Rejected because Busy and partial-effect failures have no automatic-retry contract.

### Verify composition and behavior without entering block 42 or block 46

Add descriptor/dependency tests against the real Run-once registration root. Positive assertions cover one mode snapshot, one host lifetime, exact shared/executor/reporter/lock identities, and disposal. Negative assertions reject Web server/UI/endpoints/Data Protection, scheduler/detector/coordinator/`ProcessingState`, child launcher/backend/bridge, private worker host/stdin/NDJSON/protocol, and port access.

Use deterministic fakes and gated streams/lifetimes for request identity, event order, lock-before-count, exact one count, eligible/no-work, Busy, domain failure, startup/config/data/database failures, SIGTERM-equivalent host stopping, partial effects, precedence, and disposal. A focused executable fixture may send the real supported termination signal where the existing cross-platform process harness permits, but it must use fake execution dependencies and no live database, geodata, download, Docker, fixed port, or HTTP listener. Real PostgreSQL lock semantics remain predecessor coverage; production-image/Compose execution remains block 46.

## Risks / Trade-offs

- [Registration helpers accidentally pull Web or private protocol services into Run-once] → Split by actual consumer, assert forbidden descriptors and construction sentinels, and reuse exact aliases rather than a broad role helper.
- [An operator expects NDJSON because the same image also runs private workers] → Document ordinary logs and stable exits; test absence of ready/protocol frames and stdin reads.
- [A pre-count duplicates the authoritative executor count] → Resolve no detector and assert one count after lock acquisition for both work and no-work paths.
- [SIGTERM races a terminal or cleanup failure] → Preserve established outcome precedence, terminal authority, non-cancelled cleanup, and gated race tests.
- [External retry repeats partial effects] → Ship `restart: "no"` guidance and state that exits classify one attempt without authorizing retry.
- [Applied prerequisite names or lifetimes differ from planning names] → Inventory the landed block 18/19/20/23/31/38 APIs before implementation and adapt the registration slice; stop rather than add parallel contracts.

## Migration Plan

1. Verify blocks 18–20, 23, 31, 38, and 40 are applied and re-read their concrete role, composition, executor, reporter, initializer, lock, outcome, and disposal APIs.
2. Add the Run-once-only registration/runner and ordinary event projection, reusing exact shared and worker execution aliases while excluding Web, controller protocol, and child-launch services.
3. Wire the existing RunOnce branch to create one request, execute once under host lifetime cancellation, finalize outcomes, dispose, and return its code.
4. Add focused composition, lifecycle, process-signal, exit, log-safety, and no-retry tests; run the normal suite and applicable integration tests without consuming block 46 Docker scope.
5. Add Docker Compose job/cron documentation with no ports or automatic restart. Deploy by selecting exact `run-once` for an ephemeral invocation; Standard and Web-only deployments are unchanged.

Rollback uses the prior image/change revert and removes the optional job definition. There is no settings, schema, lock object, config-volume, or data-volume migration; already committed processing effects remain ordinary application data.
