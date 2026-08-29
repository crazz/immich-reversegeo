## Purpose

Provides a one-shot internal Generic Host lifecycle that executes one accepted processing request without initializing the Web control plane or taking ownership of protocol transport details.

## ADDED Requirements

### Requirement: Internal worker uses a non-Web Generic Host
When the finalized role selector chooses InternalWorker for the exact private invocation `--internal-worker`, the system SHALL construct an `IHost` through the Generic Host builder path using the finalized shared and internal-worker registrations. It MUST NOT construct a `WebApplicationBuilder` or `WebApplication`, bind Kestrel or another listener, map endpoints or static assets, or register Razor/Blazor, antiforgery, Data Protection, the Web scheduler, Web coordinator, or Web UI state.

#### Scenario: Private worker role starts
- **WHEN** the complete invocation is the exact accepted `--internal-worker` selector
- **THEN** exactly the Generic Host worker path is built and started without constructing the Web host

#### Scenario: Worker composition is inspected
- **WHEN** the internal-worker host descriptors and built provider are inspected without running work
- **THEN** the finalized executor graph is available and the prohibited Web, server, scheduler, coordinator, and UI facilities are absent

#### Scenario: Default application starts
- **WHEN** no private worker selector is supplied and the finalized role selector chooses Web
- **THEN** the existing Web builder, registrations, middleware, endpoints, and no-argument startup behavior remain on the Web path

### Requirement: Worker startup has one ordered readiness boundary
The worker lifecycle SHALL wait until the Generic Host has started, create one asynchronous execution service scope, resolve the worker lifecycle collaborators from that scope, and complete every finalized required asynchronous worker startup initializer before invoking the readiness publication hook. Required initialization SHALL include the established skipped-storage lifecycle point when required by the finalized executor graph, but SHALL NOT eagerly materialize the country index, bootstrap DuckDB, start downloads, open live PostgreSQL work, or perform execution merely to become ready. The lifecycle MUST NOT begin consuming the initial execute request until readiness publication has completed successfully.

#### Scenario: Successful startup becomes ready
- **WHEN** the Generic Host starts and all required worker startup initializers complete
- **THEN** readiness publication completes before the initial execute-request wait begins

#### Scenario: Required initialization fails
- **WHEN** a required worker startup initializer fails before readiness
- **THEN** no readiness is published, no request is accepted, no executor is invoked, the pre-request failure hook is notified, and host shutdown begins

#### Scenario: Lazy heavy dependency remains unused
- **WHEN** the worker reaches readiness but has not accepted a request
- **THEN** readiness has not itself built the country index, bootstrapped DuckDB, started a download, opened processing database work, or invoked the executor

### Requirement: One request lease drives one executor invocation
After readiness, the worker lifecycle SHALL await one execute-request lease through a transport-neutral request-acquisition boundary. A successfully acquired lease SHALL contain the exact immutable block-7 request reconstructed under block 17 and a cooperative cancellation signal for that request. The lifecycle SHALL invoke the finalized processing executor exactly once with that request, the finalized worker reporter dependency, and the linked execution token. It SHALL NOT create or translate the run identity, alter the trigger, precompute eligibility, snapshot settings or work sets, start the Web scheduler, await a second execute request, or support worker reuse.

#### Scenario: One request is accepted
- **WHEN** request acquisition returns a valid execute lease after readiness
- **THEN** the executor is invoked once with the lease's exact run ID and trigger and no second execute acquisition occurs

#### Scenario: Executor returns
- **WHEN** the one executor invocation completes with a validated processing result
- **THEN** terminal coordination is awaited for that same request/result and the worker proceeds toward host shutdown rather than waiting for another job

#### Scenario: Executor fails outside a result
- **WHEN** the executor or reporter infrastructure throws after request acceptance
- **THEN** accepted-request failure coordination is awaited, no second invocation is attempted, and host shutdown begins without fabricating a second domain terminal result

### Requirement: Shutdown and request cancellation share one execution token
For an accepted request, the lifecycle SHALL create one execution cancellation token linked to the request lease's cooperative cancellation signal and Generic Host stopping token. A valid correlated cancel accepted by the later input loop, explicit host shutdown, or SIGTERM/SIGINT translated by Generic Host lifetime SHALL request cancellation of that token. The worker SHALL rely on Generic Host lifetime rather than install an independent signal handler. Cancellation before executor entry SHALL reach the executor already requested; cancellation during execution SHALL propagate cooperatively; clean stdin EOF after request acceptance SHALL NOT cancel execution.

#### Scenario: Control cancellation precedes execution
- **WHEN** the accepted request lease is cancelled before executor invocation
- **THEN** the executor receives the exact request with its linked token already cancelled

#### Scenario: Host shutdown occurs during execution
- **WHEN** Generic Host begins stopping while the executor is active
- **THEN** the linked execution token is cancelled and the lifecycle awaits execution and terminal coordination subject to the host's shutdown policy

#### Scenario: Input half-closes after acceptance
- **WHEN** stdin reaches clean EOF after the request lease has been accepted without a cancel command
- **THEN** execution continues without cancellation and may complete its terminal coordination normally

### Requirement: Pre-request finality starts no processing run
Clean EOF before a complete accepted execute request SHALL be reported through the pre-request EOF coordination hook, invoke no executor, create no processing request or run terminal, and stop the host. Startup failure, readiness-publication failure, or request-acquisition validation/transport failure before acceptance SHALL similarly invoke the pre-request failure hook with a safe structured reason, start no work, and stop the host. This capability SHALL preserve block 17's distinction between clean pre-request EOF and partial or invalid input while leaving bounded stdin reading and process exit mapping to later changes.

#### Scenario: Stdin closes before a request
- **WHEN** request acquisition reports clean EOF before accepting execute
- **THEN** the EOF hook is awaited, no executor or run-terminal hook is invoked, and host shutdown begins

#### Scenario: Initial input is invalid
- **WHEN** request acquisition reports malformed, incompatible, partial-at-EOF, or otherwise invalid initial input before acceptance
- **THEN** the safe pre-request failure hook is awaited, no processing run starts, and host shutdown begins

#### Scenario: Host stops while waiting for request
- **WHEN** Generic Host stopping is requested after readiness but before execute acceptance
- **THEN** the pending request wait is cancelled, no processing run starts, and lifecycle cleanup proceeds

### Requirement: Terminal coordination is single-owner and awaited
The executor's finalized reporter/session SHALL remain the sole producer of accepted-run lifecycle and terminal domain events. The worker host SHALL expose transport-neutral coordination hooks that observe pre-request finality or the accepted invocation's result/failure, await any required terminal flush/finality work, and supply one finalized host outcome to the later exit mapper. Host coordination MUST NOT duplicate, synthesize, or replace a terminal domain event already owned by the executor/reporter, and MUST NOT emit a run terminal when no request was accepted.

#### Scenario: Accepted execution completes normally
- **WHEN** the executor and its reporter complete one accepted run
- **THEN** host terminal coordination observes that completion once and finishes before host stop/disposal

#### Scenario: Pre-request failure occurs
- **WHEN** the worker fails before accepting execute
- **THEN** only pre-request coordination runs and no accepted-run terminal event is fabricated

#### Scenario: Terminal coordination fails
- **WHEN** terminal flush/finality coordination throws
- **THEN** the failure is observed as host infrastructure failure, resources are still cleaned up, and no retry or duplicate terminal coordination occurs

### Requirement: Worker resources are disposed after terminal coordination
For every success, cancellation, EOF, or failure path, cleanup SHALL use this one observable order: settle the applicable coordination hook; settle the accepted lease/control pump if one exists; dispose the linked cancellation source and lease; asynchronously dispose the execution scope; request application stop exactly once; then dispose `IHost`/its provider to release singleton/native resources including the shared data source. Disposal SHALL NOT begin a second request, and accepted-run terminal coordination SHALL finish before lease, scope, or host resources are released.

#### Scenario: Accepted run reaches cleanup
- **WHEN** accepted execution and terminal coordination finish by success, cancellation, or failure
- **THEN** the request lease, execution scope, and host-owned resources are each released without another request wait

#### Scenario: Pre-request path reaches cleanup
- **WHEN** readiness, request acquisition, or pre-request EOF ends the lifecycle
- **THEN** every resource created before that point is disposed and application stop is requested without executor invocation

#### Scenario: Scoped collaborator is asynchronously disposable
- **WHEN** a scoped worker collaborator implements asynchronous disposal
- **THEN** its disposal completes before the host is considered fully terminated

### Requirement: Standard-stream ownership remains separated
The Generic worker host SHALL write no request or protocol data itself. Host/framework/application logging initialized on the InternalWorker path SHALL be directed to stderr from host construction onward so startup and shutdown diagnostics cannot contaminate stdout. Stdout SHALL remain reserved for the sole protocol emitter supplied by block 21; block 21 SHALL own canonical NDJSON serialization, sequence assignment, write serialization, and flushing. Block 22 SHALL own bounded stdin framing, initial-request/control reading, and loop mechanics. Block 23 SHALL own process exit-code selection.

#### Scenario: Host logs during startup and shutdown
- **WHEN** Generic Host or worker lifecycle logging is produced before, during, or after a request
- **THEN** the log is routed to stderr and no non-protocol host text is written to stdout

#### Scenario: Block 20 is applied alone
- **WHEN** the host lifecycle is tested before blocks 21 through 23 are wired
- **THEN** in-memory readiness/request/terminal collaborators can drive the lifecycle without real stdin, stdout NDJSON, or exit-code behavior
