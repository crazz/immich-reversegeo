## Purpose

Defines Web-host shutdown ownership of worker admission and child-session cleanup so termination cannot strand a process, redirected stream, or control-plane lease.

## ADDED Requirements

### Requirement: Shutdown closes admission atomically before cancellation
The control plane SHALL linearize the first Web-host stopping signal with worker-job admission, close admission before requesting cancellation, and reject every request ordered after that transition. A request racing the transition SHALL either be rejected as stopping or be included in the shutdown cleanup; it MUST NOT be accepted and omitted from cleanup.

#### Scenario: Shutdown races an idle admission
- **WHEN** shutdown and a new worker-job request race while admission is idle
- **THEN** exactly one ordering wins: either the request is rejected as stopping, or its ownership is captured and shutdown cleans it up

#### Scenario: Request arrives after admission closes
- **WHEN** a worker-job request arrives after the stopping transition
- **THEN** no run identity, child process, pending projection, or execution dispatch is created

### Requirement: One shutdown operation owns all active lifecycle races
The control plane SHALL create or join one idempotent shutdown operation for the exact active ownership record. That operation SHALL cover an admitted job that is pending before launch, concurrently starting, started but not ready, ready or executing, already terminal but still draining, or awaiting final coordinator cleanup. If process startup succeeds concurrently with cancellation, the resulting owned session SHALL join the same shutdown operation.

#### Scenario: Shutdown finds admitted work before process start
- **WHEN** shutdown captures an admitted job before a child session exists
- **THEN** cancellation prevents or joins further startup and any concurrently established session is still owned and cleaned up

#### Scenario: Shutdown races successful process start
- **WHEN** process ownership transfers while shutdown cancellation is already requested
- **THEN** the started session is attached to the captured job and the shared shutdown operation cleans it up

#### Scenario: Shutdown finds a pre-ready or running session
- **WHEN** shutdown captures a started session before readiness or during execution
- **THEN** the session follows the same block-28 cancellation and escalation policy applicable to its current lifecycle state

#### Scenario: Shutdown finds terminal work still draining
- **WHEN** an accepted terminal event or process exit is already observed but stream or ownership cleanup remains
- **THEN** shutdown joins completion, stream finality, disposal, and exact-handle cleanup without starting a second cancellation operation

### Requirement: Host shutdown reuses the finalized bounded cancellation policy
For a live child session, host shutdown SHALL invoke or join the same block-28 exact-session task used by explicit Stop. Its one injected-`TimeProvider` deadline SHALL begin at the first accepted Stop and SHALL use block 28's validated internal 10-second production default. The host lifecycle SHALL NOT create, reset, shorten, or replace that deadline, cancel-command writer, whole-process-tree kill attempt, stream-drain lifecycle, or session disposal. Stop latched before request acceptance SHALL retain block 28's rule that no cancel is written until the same session has successfully written and flushed execute.

#### Scenario: User Stop and host shutdown overlap
- **WHEN** explicit Stop and Web-host shutdown target the same active session concurrently
- **THEN** callers observe one cancellation/escalation operation with at most one cancel-command attempt and at most one process-tree termination attempt

#### Scenario: Worker cooperates within grace
- **WHEN** the active worker completes under block 28's cooperative cancellation path
- **THEN** shutdown does not force an additional kill and continues through shared stream and resource cleanup

#### Scenario: Worker remains alive at the shared deadline
- **WHEN** the process remains alive when block 28's one 10-second `TimeProvider` deadline wins
- **THEN** that same task attempts whole-process-tree termination once and shutdown waits for the resulting process and stream finality

### Requirement: Host budget contains shared cleanup without replacing its policy
Web composition SHALL allocate the configured Generic Host shutdown budget around the remaining portion of block 28's one 10-second grace deadline and SHALL reserve time for whole-process-tree termination, process exit, stdout/stderr finality, resource disposal, and other hosted-service stop ordering. Cancellation of the host stop token MUST NOT be forwarded as wait-only cancellation that detaches ownership, resets or shortens the shared deadline, cancels output pumps, skips disposal, or reports shutdown complete while an owned child remains alive. If the platform kill attempt fails while the child remains alive, ownership and stopping state SHALL be retained rather than reporting successful cleanup.

#### Scenario: Host stop token is cancelled during grace
- **WHEN** the host stop token is cancelled while the shared block-28 task is waiting on its existing deadline
- **THEN** the exact-session task keeps its original deadline and ownership, and the host lifecycle does not claim clean completion or create a second escalation path

#### Scenario: Configured bounds are incompatible
- **WHEN** startup validation finds that the remaining grace, cleanup reserve, and hosted-service ordering cannot fit within the configured host shutdown timeout
- **THEN** Web-host startup fails with a safe configuration error before worker admission opens

#### Scenario: Process-tree kill fails during host shutdown
- **WHEN** block 28 records a platform kill failure and the exact child remains alive
- **THEN** the host owner remains stopping with the same session ownership and raw failure fact instead of disposing live resources or reporting shutdown cleanup complete

### Requirement: Cleanup leaves no orphan and does not classify raw outcomes
Shutdown completion SHALL require process exit, stdout and stderr finality, exactly-once disposal of process and stream resources, block-27 nonterminal bridge cleanup where needed, and exact matching coordinator-handle release. Shutdown SHALL preserve any accepted terminal and raw completion observations unchanged. It MUST NOT synthesize Completed, Cancelled, or Failed, infer a crash or protocol failure, overwrite a terminal result, or otherwise perform block-30 classification.

#### Scenario: Session ends without an accepted terminal
- **WHEN** shutdown finishes a session whose raw observations contain no accepted domain terminal
- **THEN** bridge activities and matching coordinator ownership are abandoned or released through their existing nonterminal cleanup paths without fabricating a terminal classification

#### Scenario: Session already has an accepted terminal
- **WHEN** shutdown joins a session that already accepted a terminal event
- **THEN** that terminal remains authoritative and shutdown adds no duplicate result or fatal projection

#### Scenario: Cleanup completes
- **WHEN** the shared shutdown operation completes
- **THEN** no owned child process remains alive, both redirected streams are final, process resources are disposed once, bridge activity is closed, and the matching coordinator handle is released

### Requirement: Lifecycle ownership is idempotent across host signals and startup failure
The earliest application-stopping notification SHALL close admission synchronously, and the host's asynchronous stop path SHALL await the same shared shutdown operation. Repeated stopping notifications, repeated stop calls, service disposal, and partial host startup failure SHALL be idempotent. If lifecycle startup fails after registration or after a child is owned, the same shutdown path SHALL run before ownership is released.

#### Scenario: Application-stopping callback precedes asynchronous stop
- **WHEN** the application-stopping notification fires before the host invokes asynchronous service stop
- **THEN** admission is already closed and asynchronous stop joins the operation created by that notification

#### Scenario: Stop is invoked repeatedly
- **WHEN** host stop, application-stopping notification, and disposal are invoked more than once
- **THEN** admission remains closed and all callers join one cleanup result without duplicate cancellation, kill, drain, or disposal

#### Scenario: Host startup fails
- **WHEN** Web-host startup fails after shutdown ownership has been registered or a child session has been captured
- **THEN** admission remains closed and the same bounded cleanup completes without leaving a child or resource lease behind

## Audit Reconciliation

Shutdown is clean only after the exact owned worker has exited and both stdout and stderr drains have reached finality, followed by exact-handle cleanup. A rejected or failed tree kill leaves the session unresolved: shutdown must retain ownership/failure evidence and must not report clean completion, release the handle as settled, or treat a terminal frame alone as sufficient.

