## MODIFIED Requirements

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
