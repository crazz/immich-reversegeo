# Processing Run Coordination Specification

## Purpose

Defines the local control-plane lifecycle for admitting, identifying, preparing, cancelling, dispatching, and cleaning up exactly one processing run in the Web process.

## Requirements

### Requirement: Local admission has one owner and explicit outcomes
The Web coordinator SHALL make one atomic process-local admission decision for Manual and Scheduled requests only. RunOnce remains a valid block-7 request/executor trigger for a separate run-once deployment invoker and SHALL NOT be exposed through this Web coordinator, Dashboard surface, or scheduler contract. It SHALL admit at most one run, and the coordinator's admission decision SHALL distinguish accepted, rejected because a run is already active, and rejected because shutdown has closed admission. A rejected request MUST NOT create a run identity, replace cancellation control, mark pending, arm reporting, or invoke execution. Trigger-facing adapters MAY delay or map that decision only as required by their established caller contract.

#### Scenario: Manual request arrives during an active scheduled run
- **WHEN** a scheduled run owns local admission and a manual request is received
- **THEN** the manual request receives the already-active outcome without changing the scheduled run

#### Scenario: Scheduled request arrives during an active manual run
- **WHEN** a manual run owns local admission and a scheduled request is received
- **THEN** the scheduled request receives the already-active outcome without changing the manual run

#### Scenario: Admission is closed for shutdown
- **WHEN** any trigger requests a run after local shutdown admission has closed
- **THEN** the request receives the stopping outcome and creates no run work

### Requirement: Trigger-facing completion and contention behavior remains compatible
The system SHALL use common admission semantics while retaining each trigger caller's established completion and presentation contract. A manual start call SHALL return after accepted dispatch ownership is established and SHALL add no contention log when already active. The block-12 scheduled start call SHALL report its exact rejected-already-running outcome immediately, or await an accepted run through matching terminal cleanup before reporting accepted-after-terminal; scheduled contention SHALL retain the scheduled skipped-pass log. Contention MUST NOT be represented as a processing-run event.

#### Scenario: Manual contention remains silent
- **WHEN** a manual request is rejected because another run is active
- **THEN** its prompt call receives the already-active decision and no manual contention log or run event is added

#### Scenario: Scheduled contention remains visible
- **WHEN** a scheduled request is rejected because another run is active
- **THEN** its block-12 call reports rejected-already-running, the scheduled skipped-pass control-plane message is added exactly once, and no run event is created

#### Scenario: Accepted scheduled execution remains awaited
- **WHEN** the block-12 scheduled boundary admits a due trigger
- **THEN** that call does not report accepted-after-terminal until the matching run reaches terminal cleanup, after which the scheduler may recalculate

### Requirement: Accepted runs are cancellable before pending is visible
For each accepted request, the system SHALL create one new non-empty run identifier with the accepted trigger and SHALL install that request and its live cancellation control as the active handle before pending state can be observed. Outside host shutdown it SHALL then mark pending, arm reporting for the same request, and permit exactly one execution dispatch in that order. If shutdown has captured the accepted handle before dispatch, preparation SHALL observe that shutdown cancellation at its existing phase boundaries and prevent further dispatch where possible, while retaining ownership through cleanup. A prompt accepted admission decision MUST NOT be returned until preparation and dispatch ownership are established; a caller contract that promises accepted-after-terminal MUST additionally await matching terminal cleanup.

#### Scenario: Cancel races with accepted manual admission
- **WHEN** cancellation is requested immediately after an accepted manual run first becomes pending
- **THEN** the cancellation reaches that accepted run's token and is not lost or directed to an older run

#### Scenario: Scheduled request is prepared
- **WHEN** a scheduled request wins admission
- **THEN** its unique scheduled identity, cancellation control, pending projection, reporting arm, and sole execution dispatch all refer to the same run

#### Scenario: Shutdown captures accepted preparation
- **WHEN** host shutdown captures an accepted handle before pending projection or execution dispatch completes
- **THEN** preparation observes shutdown cancellation at its next phase boundary, prevents further dispatch where possible, and joins exact-handle cleanup without a fatal or terminal projection

### Requirement: Cancellation targets the one active local run
The system SHALL make the existing control-plane cancel command request cancellation of whichever local Web-coordinator run is active, whether Manual or Scheduled. Cancellation while idle SHALL be a harmless no-op. Cancellation SHALL request cooperative termination and MUST NOT release admission before execution and terminal cleanup have stopped using the active handle.

#### Scenario: Dashboard cancels a scheduled run
- **WHEN** the Dashboard cancel command is used while a scheduled run is active
- **THEN** that scheduled run observes cancellation and no other run is affected

#### Scenario: Cancellation is requested while idle
- **WHEN** cancellation is requested with no active run
- **THEN** no cancellation source is created and the next request remains eligible for admission

### Requirement: Execution and terminal reporting have distinct owners
The system SHALL dispatch each accepted request once through the configured execution boundary with the matching reporter and cancellation token. Execution/session reporting SHALL remain the sole owner of completed, cancelled, and failed domain terminal results. The control plane MUST NOT fabricate a duplicate terminal result; it SHALL only observe execution, report ordinary control-plane infrastructure faults through the projection's guarded abandonment path when needed, and clean up ownership. For the exact shutdown-owned handle, failure cleanup SHALL instead close matching activities through the existing nonterminal abandonment path and MUST NOT add a fatal error, fabricate a terminal result, or consume block 30's outcome-classification authority.

#### Scenario: Executor returns a completed result
- **WHEN** execution returns a completed result whose request matches the active request
- **THEN** the control plane performs no second terminal report and proceeds to exact-handle cleanup

#### Scenario: Reporting infrastructure faults
- **WHEN** preparation or execution faults outside host shutdown without an accepted domain terminal result
- **THEN** the matching projection arm is abandoned through the control-plane cleanup path and local admission is released without synthesizing a second result

#### Scenario: Shutdown-owned execution is cancelled without a terminal
- **WHEN** execution for the captured shutdown handle faults or is cancelled before a domain terminal is accepted
- **THEN** cleanup closes only matching activity and coordinator ownership after session finality, preserves raw observations, and adds no fatal projection or terminal summary

### Requirement: Exact terminal cleanup permits safe retrigger
After completed, cancelled, failed, setup-faulted, or reporting-faulted execution, the system SHALL detach only the matching active handle, dispose its cancellation resources once, observe all task faults, and release local admission in an unconditional cleanup path. Late cleanup from an older request MUST NOT detach or cancel a newer request. A request made after matching cleanup completes SHALL be eligible for admission.

#### Scenario: Run completion is followed by retrigger
- **WHEN** a run reaches terminal state and its matching control-plane cleanup completes
- **THEN** a later request can be accepted with a different non-empty run identifier

#### Scenario: Old cleanup arrives after a later run starts
- **WHEN** stale cleanup for an older request is observed after a newer request owns admission
- **THEN** the newer request, cancellation control, reporting arm, and execution continue unchanged

### Requirement: Web shutdown closes admission before draining local execution
The system SHALL synchronously close local admission at the first Web-host stopping signal, capture any active ownership record under the same gate, reject later requests, and publish one shared shutdown task. The earliest application-stopping callback, factory-aliased hosted coordinator, repeated stop calls, startup failure, and service disposal SHALL use that same singleton state and task. Shutdown SHALL join existing cancellation and completion ownership for in-process execution and any attached child session. It SHALL await physical process exit, both redirected-stream finalities, resource disposal, nonterminal bridge cleanup where needed, and exact-handle release. Host stop-token cancellation MUST NOT abandon that owned cleanup or falsely report clean completion. This boundary MUST NOT redefine worker protocol, cancellation policy, outcome classification, or cross-process locking.

#### Scenario: Shutdown races with a new request
- **WHEN** Web-host shutdown closes admission while a trigger concurrently requests a run
- **THEN** exactly one atomic ordering wins: either the run was already admitted and is captured for cancellation and cleanup, or the request is rejected as stopping

#### Scenario: Shutdown begins with an active run
- **WHEN** the Web host begins stopping while local execution or an attached child session is active
- **THEN** no later run is admitted and host shutdown joins the exact run's existing cancellation and resource cleanup through finality

#### Scenario: Host stop token expires before owned cleanup
- **WHEN** the supplied host shutdown token is cancelled while an owned process or stream remains unsettled
- **THEN** the shared task retains ownership and remains pending until actual cleanup finality, without resetting grace or creating a second kill attempt
