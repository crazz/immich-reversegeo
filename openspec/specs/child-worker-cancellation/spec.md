# Child Worker Cancellation Specification

## Purpose

Defines exact-session, bounded child-worker cancellation that preserves cooperative terminal reporting when possible, escalates unresponsive workers safely, and retains raw lifecycle evidence for later classification.

## Requirements

### Requirement: Stop targets only the current admitted child session
The control plane SHALL accept Stop only against its exact current active run and owned child session. The first accepted Stop SHALL atomically mark that handle stopping and create one shared cancellation operation. Concurrent or repeated Stop calls for the same handle SHALL join that operation and MUST NOT create another command write, grace deadline, or kill attempt. Stop while idle SHALL be a harmless no-op. A cancellation operation captured for an older handle MUST NOT read or act on a replacement session.

#### Scenario: Concurrent Dashboard Stop calls
- **WHEN** two callers request Stop concurrently for the same active child run
- **THEN** both observe the same cancellation operation while at most one cancel frame, one grace deadline, and one escalation attempt are created

#### Scenario: Old cancellation completes after retrigger
- **WHEN** cleanup from an older run races with a newly admitted run
- **THEN** the older operation cannot write to, kill, detach, or dispose the new run's session

#### Scenario: Stop is requested while idle
- **WHEN** Dashboard requests Stop with no active run
- **THEN** no session, command, deadline, process action, or cancellation source is created

### Requirement: Cancel delivery is accepted-session-only and exact once
An accepted Stop MAY latch while worker startup is incomplete, but the controller SHALL write a cancel command only after the same session has successfully written and flushed its one execute request and remains alive with eligible stdin. It SHALL serialize exactly one canonical next-sequence Phase 3 cancel frame carrying that session's exact run ID, write the entire frame, and flush it through the session's sole stdin writer. It SHALL NOT send cancel after startup failure, before execute flush, after exit, through closed stdin, or to another session. Stdin write or flush failure SHALL be retained as a typed raw fact and SHALL NOT be treated as command acceptance or implicit process exit.

#### Scenario: Stop arrives before readiness
- **WHEN** Stop is accepted while the exact worker is still waiting for valid readiness
- **THEN** cancellation intent is latched, no out-of-order cancel is written, and one cancel is written only if that session later flushes execute before its grace deadline

#### Scenario: Execute never becomes accepted
- **WHEN** startup times out, fails validation, exits, or fails execute write or flush after Stop was latched
- **THEN** no cancel frame is written and the operation preserves the startup/transport facts for its later exit or escalation path

#### Scenario: Cancel flush succeeds
- **WHEN** an active request-accepted session remains alive after Stop
- **THEN** one complete correlated cancel frame is written and flushed and no repeated caller emits another frame

#### Scenario: Process exits during cancel delivery
- **WHEN** process exit races the cancel write or flush
- **THEN** the session records the actual exit/transport facts, performs no stale follow-up write, and does not kill an already exited process

### Requirement: Grace has one deterministic internal deadline
The cancellation operation SHALL use an injected monotonic time source and one fixed internal 10-second grace measured from the first accepted Stop. The grace value SHALL NOT be configurable through composition, tests, persisted application settings, or the Settings page. Tests SHALL advance the injected clock instead of changing the grace value. Caller wait cancellation MUST NOT cancel or reset the owned deadline.

#### Scenario: Cooperative exit beats grace
- **WHEN** the exact worker exits before the 10-second deadline
- **THEN** forced termination is not requested and all callers observe the same cooperative completion lifecycle

#### Scenario: Stop occurs during startup
- **WHEN** Stop is accepted before execute delivery and the worker remains alive
- **THEN** the same deadline continues from the first Stop rather than restarting after readiness or cancel flush

#### Scenario: Fake time reaches deadline
- **WHEN** a deterministic test advances the injected time source to the grace deadline while the worker remains alive
- **THEN** escalation becomes eligible without sleeping, polling, or relying on wall-clock timing

### Requirement: Worker cancellation remains cooperative through the executor token
The worker SHALL connect the accepted request lease's single cancellation signal to the exact executor invocation token together with existing host-stopping linkage. A valid cancel accepted before executor entry SHALL be observable as already requested at entry; one accepted during execution SHALL request the same token; one ordered after terminal SHALL have no effect. The system SHALL NOT claim that cancellation can interrupt synchronous native or blocking work that does not observe that token; such work MAY continue until it returns or controller escalation terminates the process.

#### Scenario: Cancel is accepted before executor entry
- **WHEN** the worker input pump accepts cancel after execute but before executor invocation
- **THEN** the one executor invocation receives cancellation already requested without changing request identity

#### Scenario: Executor is blocked in noncooperative native work
- **WHEN** the executor does not observe cancellation before the controller grace expires
- **THEN** no synthetic cooperative terminal is assumed and the controller may escalate against the owned process tree

### Requirement: Cooperative completion preserves terminal and stream finality
Cancellation SHALL NOT fabricate or rewrite a worker terminal event. A valid cancelled terminal and orderly exit SHALL remain cooperative evidence, while any other valid terminal, absent terminal, contradictory raw exit, protocol/sink observation, or stdin failure SHALL remain independent raw evidence for block 30. A terminal event alone MUST NOT release process ownership. The cancellation lifecycle SHALL wait for raw process exit and finality of both stdout and stderr pumps before reporting settled cleanup.

#### Scenario: Cancelled worker exits within grace
- **WHEN** a worker accepts cancel, emits a valid cancelled terminal, exits 130, and closes both output streams before the deadline
- **THEN** the terminal and exit are preserved, no kill is attempted, trailing output is drained, and resources are released once

#### Scenario: Terminal arrives but process remains alive
- **WHEN** any terminal frame is accepted but the worker has not exited by the grace deadline
- **THEN** the terminal remains preserved and the still-live process remains eligible for escalation

#### Scenario: Cancel transport fails but process exits
- **WHEN** cancel write or flush fails and the process subsequently exits before grace
- **THEN** no kill is attempted and both the transport failure and complete raw exit/drain observations remain available

### Requirement: Grace expiry escalates through whole-process-tree termination
When the grace deadline wins while the exact process remains alive, the session owner SHALL make one escalation attempt equivalent to `Kill(entireProcessTree: true)`. It MUST recheck an exit racing the attempt and MUST NOT reacquire a process by PID or target another generation. After a successful kill request, the lifecycle SHALL await actual process exit and both output drains before disposal. Forced termination SHALL remain an unmapped raw platform observation rather than being assigned a block-23 worker-selected exit code.

#### Scenario: Unresponsive worker exceeds grace
- **WHEN** an armed worker ignores cancel and remains alive at the deadline
- **THEN** the controller requests whole-process-tree termination once, then waits for exit plus stdout/stderr finality before releasing ownership

#### Scenario: Process exits as escalation begins
- **WHEN** process exit wins the race with the tree-kill call
- **THEN** the outcome is recorded as already exited, no second kill occurs, and normal drain/disposal completes

#### Scenario: Platform rejects tree termination
- **WHEN** the platform reports a safe-normalized permission, unsupported-operation, or process-state failure and the process remains alive
- **THEN** the session records escalation failure, does not claim stopped or disposed, remains owned in stopping state, and performs no blind retry or PID-based fallback

### Requirement: Cancellation exposes classification facts without classifying them
The cancellation result SHALL retain safe typed facts for first Stop/deadline time, request acceptance, cancel write/flush outcome, exit before or during control, grace expiry, tree-kill attempted/accepted/already-exited/failed, and the block-25 terminal, raw exit, protocol/sink, stdin, stdout/stderr finality, and bounded stderr observations. It SHALL NOT declare a crash, missing-terminal cause, terminal/exit contradiction outcome, retry authorization, or projected UI failure; block 30 SHALL consume those facts.

#### Scenario: Forced kill completes without a terminal
- **WHEN** escalation terminates an unresponsive worker and all streams drain without an accepted terminal
- **THEN** the result identifies requested cancellation, grace expiry, forced termination, terminal absence, raw platform exit, and stream finality without inventing a failed or cancelled terminal classification

#### Scenario: Kill request fails
- **WHEN** whole-tree termination fails while the worker remains alive
- **THEN** callers receive a stable bounded escalation-failure fact without raw exception text, stack, command bytes, secrets, or retry advice

### Requirement: Stop, completion, and disposal share one resource lifecycle
Stop, natural completion, and asynchronous disposal SHALL converge on the session's existing process-exit and stdout/stderr-drain lifecycle. After exit and both drains, the owner SHALL close stdin and dispose cancellation timers/sources, redirected streams, process adapter, bridge/session resources, and other owned handles exactly once. Concurrent waits or disposal calls SHALL observe the same settled tasks. If escalation fails while the process remains alive, ownership and stopping state SHALL be retained until later exit or a separately owned host policy acts; resources MUST NOT be released as though exit occurred.

#### Scenario: Stop and disposal race after cooperative exit
- **WHEN** multiple callers await Stop and dispose the same session as cooperative completion settles
- **THEN** all observe one completion and every owned resource is disposed exactly once after exit and stream finality

#### Scenario: Kill succeeds with trailing diagnostics
- **WHEN** tree termination is accepted while stdout or stderr still has readable trailing bytes
- **THEN** disposal waits for both drains and preserves the bounded observations before closing handles

### Requirement: Cancellation verification is deterministic and reuses the process fixture
Tests SHALL cover session-policy races with injected process streams, exit gates, disposal counters, and fake time, and SHALL reuse block 26's armed cooperative and unresponsive real-process scenarios. Expected behavior SHALL be coordinated by readiness, execute/cancel markers, terminal events, and process exit rather than sleeps or polling. Finite real-time watchdogs MAY be used only to fail and reap a stuck fixture.

#### Scenario: Cooperative fixture is stopped
- **WHEN** the fixture signals armed and the controller accepts Stop
- **THEN** the test observes one valid cancel, one cancelled terminal, exit 130, complete drains, no kill, and exactly-once disposal

#### Scenario: Unresponsive fixture is stopped
- **WHEN** the fixture signals armed, observes cancel, and fake time reaches grace while it remains alive
- **THEN** the test observes one tree-kill request, process exit, complete drains, no fabricated terminal classification, and no surviving fixture process

## Audit Reconciliation

The one bounded escalation decision uses exactly one internal, exact-session 10-second deadline measured through `TimeProvider`; it is not configurable and creates no current or future public setting. After that deadline, raw process exit suppresses one tree-kill attempt; a live owned process receives at most one attempt. A terminal frame alone never settles process ownership.
