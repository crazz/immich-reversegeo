## Purpose

Allows operators and external schedulers to execute exactly one globally excluded Immich ReverseGeo processing pass, observe a stable process result, and exit without starting a Web server or child-worker protocol.

## ADDED Requirements

### Requirement: Run-once is a public non-Web one-shot role
When the immutable deployment-mode snapshot resolves to Run-once, the process SHALL enter the existing typed RunOnce role boundary before Web construction. It MUST NOT construct or start Kestrel or another HTTP server; bind, probe, or reserve an HTTP port; map endpoints or static assets; construct Razor/Blazor UI; or register or activate the internal scheduler. It SHALL make one processing attempt and then stop, dispose all resources, and terminate.

#### Scenario: Run-once starts with ordinary Web port settings present
- **WHEN** `IMMICH_REVERSEGEO_MODE` is exactly `run-once` and Web URL or port environment values are also present
- **THEN** one Run-once attempt proceeds without constructing a Web host or opening, validating, or reserving an HTTP listener

#### Scenario: One attempt reaches finality
- **WHEN** the Run-once attempt completes, is cancelled, is busy, or fails
- **THEN** the process performs no second pass, disposes its host and owned services, and exits

### Requirement: Private internal-worker precedence remains unchanged
The exact private `--internal-worker` role and every malformed, duplicate, or augmented reserved-syntax failure SHALL retain the block 18 and block 40 precedence contract. Private-role selection or failure MUST complete without reading or validating `IMMICH_REVERSEGEO_MODE`, and Run-once MUST NOT add a public command-line alias or reinterpret ordinary host arguments.

#### Scenario: Private worker inherits Run-once mode
- **WHEN** the complete invocation is exactly `--internal-worker` and the inherited public mode value is `run-once`
- **THEN** InternalWorker is selected without entering Run-once composition or reading the public mode source

#### Scenario: Run-once text appears as an argument
- **WHEN** an ordinary public invocation contains `run-once` as command-line text but the public mode snapshot is not Run-once
- **THEN** the role parser preserves the argument and does not select Run-once from command-line syntax

### Requirement: Run-once executes the authoritative pipeline in the same process
Run-once SHALL compose the finalized worker execution and required shared services in the invoking process. It SHALL create exactly one fresh non-empty processing request whose trigger is RunOnce, open one reporting session, invoke the authoritative executor exactly once with that request and the host cancellation token, and use the executor's exact eligibility count exactly once. It MUST NOT start or invoke a child process, child launcher, controller bridge, worker stdin loop, or private execute/cancel protocol. It MUST NOT perform a preliminary detector count or create a stable work set.

#### Scenario: Eligible work exists
- **WHEN** the advisory lock is acquired and the authoritative count is nonzero
- **THEN** the same process executes the existing processing pipeline once using one RunOnce request and its pinned processing snapshot, without launching a child

#### Scenario: No eligible work exists
- **WHEN** the advisory lock is acquired and the authoritative count is zero
- **THEN** the executor completes with zero accounting, does not read non-empty-run configuration or heavy pipeline dependencies, and the process exits successfully without another count or pass

### Requirement: Advisory exclusion gates domain work after run-started
The existing PostgreSQL advisory run lock SHALL be attempted non-blockingly as the first executor-session gate immediately after run-started and before the authoritative eligibility count, snapshots, geodata initialization or lookup, asset mutation, or other domain/heavy work. A successful lease SHALL retain its dedicated database session through protected execution, terminal reporting, and lock finalization. Run-once MUST consume the established key, lease, loss, unlock, cleanup, and classification behavior without defining another local or distributed lock.

#### Scenario: Another deployment owns the lock
- **WHEN** the same Immich database already has an owner for the established advisory key
- **THEN** Run-once emits the existing failed busy terminal with zero domain counts, performs no eligibility or heavy/domain work, exits 3, and does not wait or retry

#### Scenario: Lock acquisition or ownership fails
- **WHEN** database open/acquisition fails, ownership is lost, or release cannot be confirmed without cooperative cancellation
- **THEN** the attempt is classified as infrastructure failure, protected work stops, cleanup is attempted with the established independent cleanup bound, and the process exits 5 absent a higher-precedence condition

### Requirement: Run-once has stable process outcomes
An orderly Run-once invocation SHALL return 0 for completed execution including no work, 3 for advisory-lock Busy, 4 for executor/domain failure, 5 for Run-once host startup, required configuration/dependency, config/data initialization, database/lock infrastructure, lifecycle, or cleanup failure, and 130 for cooperative cancellation or host shutdown. Invalid public deployment mode and private-role syntax SHALL retain their existing pre-host exit 2 behavior. Within Run-once finalization, infrastructure failure SHALL take precedence over Busy, executor/domain failure, cancellation, and completion; Busy SHALL take precedence over domain failure, cancellation, and completion; domain failure SHALL take precedence over cancellation and completion. Abrupt termination outside managed finalization SHALL remain an unmapped platform status. Run-once SHALL NOT emit private worker output-transport code 6 because it has no NDJSON protocol sink.

#### Scenario: Required startup configuration is unusable
- **WHEN** Run-once cannot initialize required configuration, data storage, database services, or another mandatory dependency before or during the attempt
- **THEN** it emits a bounded safe operator diagnostic, performs no retry, and exits 5

#### Scenario: Executor reports a domain failure
- **WHEN** the accepted executor session finishes Failed and no higher-precedence Run-once condition occurs
- **THEN** the process exits 4 after terminal handling and resource disposal

#### Scenario: Cleanup fails after completion
- **WHEN** the pass completes but host, lock, scope, provider, or owned-resource cleanup fails
- **THEN** the completed run fact is retained and the final process classification is infrastructure exit 5

### Requirement: Host signals cooperatively cancel the single attempt
Run-once SHALL rely on the host lifetime to translate SIGTERM and SIGINT into cancellation of the same token supplied to the executor and lock acquisition. If cancellation is observed before executor entry, the process SHALL start no domain work; if observed after the reporting session starts, the existing cancelled terminal and committed partial effects SHALL be preserved. Managed cancellation SHALL await terminal handling and owned cleanup before returning 130 unless a higher-precedence failure occurs.

#### Scenario: SIGTERM arrives during processing
- **WHEN** the process receives SIGTERM while the executor is active and no higher-precedence failure occurs
- **THEN** active work is cooperatively cancelled, already committed writes and counts remain, resources are disposed, and the process exits 130

#### Scenario: Shutdown begins before the request is accepted
- **WHEN** host stopping is requested before executor-session entry
- **THEN** no processing terminal is fabricated, no domain work begins, resources are disposed, and the process exits 130

### Requirement: Run-once emits ordinary operator logs, not worker protocol
Because Run-once has no controller, its standard streams SHALL contain human-readable line-oriented operator logs rather than private worker NDJSON. It MUST NOT emit a ready frame, protocol sequence numbers, execute/cancel frames, or require stdin. Informational lifecycle, eligibility, progress, no-work, and completion messages SHALL be ordinary stdout output; warnings, failures, and a bounded final nonzero classification summary SHALL use stderr. The stable automation contract SHALL be the process exit code, not log parsing. Logs and summaries MUST NOT expose credentials, connection strings, raw environment/configuration contents, command-line values, SQL, stack traces, or exception dumps. A best-effort ordinary log write failure SHALL NOT select private protocol output code 6 or cause execution retry.

#### Scenario: No-work invocation is observed by a cron runner
- **WHEN** a Run-once invocation acquires the lock and finds zero eligible assets
- **THEN** stdout contains ordinary human-readable start, zero-eligibility/no-work, and completion information, no line is a private protocol frame, and the process exits 0

#### Scenario: Busy invocation is observed by an operator
- **WHEN** advisory-lock contention selects Busy
- **THEN** stderr contains a bounded safe busy/final-classification diagnostic without sensitive values and the process exits 3

### Requirement: External scheduling creates attempts but Run-once never retries
Run-once SHALL perform no automatic retry, replay, fallback, replacement process, request resubmission, or catch-up pass for any completed, Busy, cancelled, failed, or abruptly terminated invocation. An external scheduler or operator MAY start a later independent invocation, but every invocation SHALL create a new request and re-evaluate the database under the ordinary non-transactional processing semantics. Exit codes MUST NOT claim that retry is safe; partial writes and skipped records from an earlier attempt SHALL remain.

#### Scenario: Busy attempt finishes
- **WHEN** Run-once exits 3 because the advisory lock is held
- **THEN** the process terminates without delay or retry and leaves any later invocation to external policy

#### Scenario: Failure follows committed writes
- **WHEN** an invocation fails or is cancelled after some asset effects have committed
- **THEN** it does not roll back, replay, or automatically retry those effects before exiting

### Requirement: The neutral image supports Compose jobs and cron
The same neutral production image and entrypoint SHALL support Run-once without a mode-specific binary, image, command, or exposed port. Public Docker-first documentation SHALL show an optional Compose job/service using exact `IMMICH_REVERSEGEO_MODE=run-once`, the existing database configuration and network, separate config and data mounts, no port publication, and no automatic restart. It SHALL show how cron or another scheduler starts one disposable invocation and SHALL state that overlap returns Busy/3 and that neither the application nor the example retries. The private worker token MUST NOT be documented as an alternative.

#### Scenario: Compose job runs one pass
- **WHEN** an operator starts the documented Run-once Compose job
- **THEN** it uses the same image and entrypoint, mounts config and data separately, publishes no port, performs one attempt, and exits for Compose to observe

#### Scenario: Cron starts a later invocation
- **WHEN** cron invokes the documented disposable Compose command at a later scheduled time
- **THEN** it starts a new independent Run-once process rather than waking a persistent scheduler or reusing the prior request
