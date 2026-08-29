## 1. Reconcile prerequisite contracts

- [ ] 1.1 Verify blocks 18–20, 23, 31, 38, and 40 are applied, inventory their landed role/composition/executor/reporter/initializer/lock/outcome/disposal APIs, and stop rather than introduce parallel contracts if a prerequisite is missing.
- [ ] 1.2 Identify the minimal existing shared and worker-execution registration slices Run-once can reuse without Web, scheduler, child-launch, or private protocol services.

## 2. Add Run-once composition and lifecycle

- [ ] 2.1 Add a Run-once branch at the existing typed RunOnce boundary before any Web builder, consuming the immutable block 40 mode snapshot without rereading mode or reparsing private syntax.
- [ ] 2.2 Compose one non-Web Generic Host (or landed equivalent) with required configuration/path/database initialization, exact worker executor aliases, advisory-lock services, Run-once reporter, host lifetime, and outcome finalization.
- [ ] 2.3 Add structural guards proving the Run-once graph has no Kestrel/server/endpoints/Razor/Blazor/Data Protection, scheduler/detector/coordinator/ProcessingState, child backend/launcher/bridge, internal-worker stdin/NDJSON/protocol, port, or UI services.
- [ ] 2.4 Create one fresh non-empty RunOnce request after required initialization, resolve one execution scope/reporter/executor, and invoke the executor exactly once with the host stopping token.
- [ ] 2.5 Preserve run-started then non-blocking advisory-lock acquisition then exactly one authoritative eligibility count, including the existing zero-work short circuit and no detector/pre-count.
- [ ] 2.6 Await terminal handling, lock finalization, asynchronous scope disposal, host stop, and provider disposal exactly once, then return without a second pass.

## 3. Operator output, signals, and process results

- [ ] 3.1 Implement a validating Run-once event projection to ordinary human-readable stdout/stderr logs without ready frames, NDJSON, sequence allocation, stdin reads, or private output-transport behavior.
- [ ] 3.2 Add bounded secret-safe final nonzero summaries and prove log-write failure is best effort, does not select code 6, and never retries execution.
- [ ] 3.3 Reuse the established outcome values/precedence to return 0 for completed/no-work, 3 for Busy, 4 for domain failure, 5 for startup/config/data/database/lock/lifecycle/cleanup infrastructure failure, and 130 for managed cancellation, while retaining pre-host exit 2 behavior.
- [ ] 3.4 Connect Generic Host SIGINT/SIGTERM stopping to the exact lock/executor token and preserve cancelled terminal/partial effects after session entry, no terminal before entry, higher-precedence failures, and complete cleanup.
- [ ] 3.5 Assert no automatic retry, replay, fallback, replacement process, resubmission, rollback, catch-up, child spawn, or second request for every terminal and abrupt-attempt path.

## 4. Verification

- [ ] 4.1 Add deterministic composition/provider tests for required descriptor identity, lazy construction, no Web/listener/port behavior, no scheduler/UI/protocol/child services, and complete sync/async disposal.
- [ ] 4.2 Add gated lifecycle tests for exact request ID/RunOnce trigger, event order, one executor invocation, lock-before-count, exact one count, eligible processing, authoritative no-work, and no second pass.
- [ ] 4.3 Add an outcome matrix for Busy, domain failure, startup/config/data/database/acquisition/loss/unlock/cleanup failures, managed cancellation, late-failure precedence, abrupt unmapped termination, safe logs, and no retry.
- [ ] 4.4 Add deterministic host-stopping tests and, where the existing cross-platform process fixture supports it, a focused real SIGTERM/SIGINT fixture proving exit 130 and cleanup without live PostgreSQL, geodata, downloads, Docker, HTTP, or fixed ports.
- [ ] 4.5 Run focused Run-once/role/composition/executor/lock/outcome tests, `npm run test` with default exclusions, and only prerequisite integration suites needed to protect the landed lock/process seams.

## 5. Docker-first operations documentation

- [ ] 5.1 Document an optional Compose Run-once job using the same image/entrypoint, exact mode environment value, existing database/network configuration, separate config/data mounts, no ports, and no automatic restart.
- [ ] 5.2 Document a disposable `docker compose run --rm` cron invocation, one-launch/one-attempt semantics, Busy exit 3, stable exit meanings, ordinary logs, retained partial effects, and the absence of application retry authorization without advertising the private worker token.
- [ ] 5.3 Keep the neutral Dockerfile and persistent Standard/Web-only examples unchanged except for Run-once-specific optional documentation; leave production-image/Docker smoke execution to block 46.

## 6. Final validation

- [ ] 6.1 Run `openspec validate 43-implement-run-once-deployment-mode --strict` and confirm final OpenSpec status is complete.
- [ ] 6.2 Review the final diff and source references as block-43-only, explicitly confirming no edits to block 42, no scheduler/UI/Kestrel/ports, and no implementation of block 44–46 scope.
