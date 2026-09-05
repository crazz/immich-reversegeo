## MODIFIED Requirements

### Requirement: Execution and terminal reporting have distinct owners
The system SHALL dispatch each accepted request once through the configured execution boundary with the matching reporter and cancellation token. Execution/session reporting SHALL remain the sole owner of completed, cancelled, and failed domain terminal results. For a child-owned execution, block 30 SHALL resolve typed no-process failure or physically final session evidence through the same exact-request receipt used by normal terminal projection. It SHALL reserve that child execution before command resolution and permit at most one launch. The in-process execution boundary remains selected until the later backend-routing change. The control plane MUST NOT fabricate a duplicate terminal result; it SHALL only observe execution, report ordinary control-plane infrastructure faults through the projection's guarded abandonment path when needed, and clean up ownership. For a shutdown-owned handle without a block-30 child finalizer, failure cleanup SHALL instead close matching activities through the existing nonterminal abandonment path and MUST NOT add a fatal error, fabricate a terminal result, or consume block 30's outcome-classification authority.

#### Scenario: Executor returns a completed result
- **WHEN** execution returns a completed result whose request matches the active request
- **THEN** the control plane performs no second terminal report and proceeds to exact-handle cleanup

#### Scenario: Reporting infrastructure faults
- **WHEN** in-process preparation or execution faults outside host shutdown without an accepted domain terminal result
- **THEN** the matching projection arm is abandoned through the control-plane cleanup path and local admission is released without synthesizing a second result

#### Scenario: Shutdown-owned execution is cancelled without a terminal
- **WHEN** execution for the captured shutdown handle has no child finalizer and faults or is cancelled before a domain terminal is accepted
- **THEN** cleanup closes only matching activity and coordinator ownership after session finality, preserves raw observations, and adds no fatal projection or terminal summary

#### Scenario: An admitted child command fails before process creation
- **WHEN** the exact admitted handle has reserved child execution and command resolution or OS start fails
- **THEN** the child finalizer records one Failed result through the shared receipt, performs no replacement launch, and permits only matching-handle cleanup

### Requirement: Exact terminal cleanup permits safe retrigger
After completed, cancelled, failed, setup-faulted, or reporting-faulted execution, the system SHALL detach only the matching active handle, dispose its cancellation resources once, observe all task faults, and release local admission after required finality. For child-owned execution, physical process exit, both output pumps, the recorded UI finalization receipt, callback closure, and owned resource cleanup SHALL precede matching-handle detachment. A rejected kill or missing finalization receipt MUST NOT release an unsettled handle. Late cleanup from an older request MUST NOT detach or cancel a newer request. A request made after matching cleanup completes SHALL be eligible for admission.

#### Scenario: Run completion is followed by retrigger
- **WHEN** a run reaches terminal state and its matching control-plane cleanup completes
- **THEN** a later request can be accepted with a different non-empty run identifier

#### Scenario: Old cleanup arrives after a later run starts
- **WHEN** stale cleanup for an older request is observed after a newer request owns admission
- **THEN** the newer request, cancellation control, reporting arm, and execution continue unchanged

#### Scenario: Physical or UI finality remains unsettled
- **WHEN** a child-owned run cannot establish physical exit or its exact UI finalization receipt
- **THEN** local admission remains owned and no replacement run is admitted, even if a cleanup observer faults

### Requirement: Web shutdown closes admission before draining local execution
The system SHALL synchronously close local admission at the first Web-host stopping signal, capture any active ownership record under the same gate, reject later requests, and publish one shared shutdown task. The earliest application-stopping callback, factory-aliased hosted coordinator, repeated stop calls, startup failure, and service disposal SHALL use that same singleton state and task. Shutdown SHALL join existing cancellation and completion ownership for in-process execution and any attached child session. It SHALL await physical process exit, both redirected-stream finalities, the block-30 finalization receipt for child-owned execution, callback closure, resource disposal, nonterminal bridge cleanup where needed, and exact-handle release. Captured Stop, shutdown, and terminal-preventing fault timestamps SHALL arbitrate through the one child termination owner; the earliest timestamp determines intent and deadline, with Stop/shutdown winning an equal-clock tie. Later containment SHALL close unsafe input without creating a second timer. Host stop-token cancellation MUST NOT abandon that owned cleanup or falsely report clean completion. This boundary MUST NOT redefine worker protocol, cancellation policy, outcome classification, or cross-process locking.

#### Scenario: Shutdown races with a new request
- **WHEN** Web-host shutdown closes admission while a trigger concurrently requests a run
- **THEN** exactly one atomic ordering wins: either the run was already admitted and is captured for cancellation and cleanup, or the request is rejected as stopping

#### Scenario: Shutdown begins with an active run
- **WHEN** the Web host begins stopping while local execution or an attached child session is active
- **THEN** no later run is admitted and host shutdown joins the exact run's existing cancellation and resource cleanup through finality

#### Scenario: Host stop token expires before owned cleanup
- **WHEN** the supplied host shutdown token is cancelled while an owned process or stream remains unsettled
- **THEN** the shared task retains ownership and remains pending until actual cleanup finality, without resetting grace or creating a second kill attempt

#### Scenario: Fault containment precedes a later Stop
- **WHEN** an exact child fault is observed before a later Stop or shutdown marker
- **THEN** the shared termination operation retains containment intent and its original deadline, and a later Stop does not turn fault cleanup into Cancelled
