## Purpose

Defines how the Web control plane deterministically finalizes one child-worker run when process, protocol, projection, cancellation, shutdown, and terminal evidence agree or conflict.

## ADDED Requirements

### Requirement: Final classification uses complete immutable session evidence
The control plane SHALL freeze terminal classification for one admitted worker session only after a typed no-process failure or process exit and stdout and stderr finality, using the finalized typed command-resolution, launch, readiness, request-write, protocol, sink/projection, terminal, exit, cancellation, fault-containment, kill, shutdown, and bounded-stderr observations. It SHALL preserve the first fault in each owning subsystem and SHALL NOT reconstruct protocol meaning from stderr or from a raw exit number alone.

#### Scenario: Trailing terminal after process exit observation
- **WHEN** process exit is observed before the stdout pump delivers a trailing valid terminal frame
- **THEN** classification waits for stdout and stderr finality and includes that terminal in the final evidence

#### Scenario: Raw mapped-looking exit without managed proof
- **WHEN** an abruptly terminated worker returns a platform status numerically equal to a mapped worker exit code but emits no valid terminal
- **THEN** the control plane treats the status as raw process evidence rather than proof of the mapped domain outcome

### Requirement: Startup and pre-ready failures are terminalized
An admitted run that has no committed terminal SHALL become failed exactly once when command resolution fails, process start fails, readiness times out, the process or stdout ends before valid readiness, readiness delivery is rejected, or execute write or flush fails. The diagnostic SHALL identify the safe lifecycle phase and failure category without fabricating worker events.

#### Scenario: Command descriptor failure
- **WHEN** command resolution fails after coordinator admission
- **THEN** the run becomes failed once, no process or pipe operation occurs, activities are cleaned, and only the exact admitted coordinator handle is released

#### Scenario: OS start failure
- **WHEN** the command is valid but the operating system fails to start the worker
- **THEN** the admitted run becomes failed once, no execute is written, and coordinator ownership is released

#### Scenario: Ready timeout
- **WHEN** no valid ready event is accepted before the launcher's finalized readiness deadline
- **THEN** the run becomes failed after owned process cleanup and receives a bounded readiness-timeout diagnostic

#### Scenario: Execute transport failure
- **WHEN** ready was accepted but the canonical execute frame cannot be written or flushed
- **THEN** the run becomes failed without claiming that worker execution started

### Requirement: Protocol faults fail closed
Without an already committed terminal, malformed UTF-8 or JSON, oversized or partial frames, unknown or incompatible protocol/version/direction/category/type, invalid readiness, sequence gap/replay/duplicate, run correlation, lifecycle, terminal consistency, progress, activity identity, or activity-cardinality faults SHALL produce one failed control-plane outcome. Same-version additive unknown object properties SHALL remain compatible and SHALL NOT be classified as faults.

#### Scenario: Malformed or oversized stdout
- **WHEN** stdout contains a malformed or oversized protocol frame
- **THEN** further callbacks are suppressed, both pipes continue draining, and finalization fails the run once with the specific safe fault category

#### Scenario: Unknown discriminator
- **WHEN** a frame has an unknown protocol, version, direction, category, or type
- **THEN** finalization reports the corresponding unknown or incompatible category and fails the run once

#### Scenario: Sequence or correlation fault
- **WHEN** a callback has a sequence gap, replay, duplicate, wrong run ID, or illegal lifecycle position
- **THEN** it mutates no processing state and the session is failed once after finality

#### Scenario: Activity cardinality fault
- **WHEN** an activity end is unknown or duplicated, an activity start reuses an active identity, or terminal facts contradict the open-activity contract
- **THEN** the callback is rejected, remaining activity scopes are cleaned during finalization, and no activity is revived

#### Scenario: Additive extension
- **WHEN** a same-version valid frame contains only unknown additive object properties
- **THEN** those properties are ignored and the frame remains eligible for normal projection

### Requirement: Fault containment is bounded and does not impersonate user cancellation
After a terminal-preventing startup, protocol, sink, projection, or output fault, the control plane SHALL begin or join one exact-session internal containment operation if the process remains alive. It SHALL reuse the block-28 owner's single deadline, whole-tree kill, exit/drain, and disposal mechanics without creating a second timer or process owner; it SHALL distinguish fault containment from user Stop and host shutdown, SHALL NOT send further protocol input after output/protocol safety is lost, and SHALL classify only after containment settles. An accepted kill SHALL be failure cleanup unless an earlier exact-session Stop or shutdown intent independently authorizes Cancelled. Kill failure SHALL retain ownership and surface Failed rather than release an unsettled process.

#### Scenario: Pre-ready timeout worker remains alive
- **WHEN** readiness times out and the worker does not exit
- **THEN** one internal fault-containment operation reaches the shared grace deadline, kills the process tree once, drains both pipes, and permits one Failed finality

#### Scenario: Post-acceptance protocol fault worker remains alive
- **WHEN** a terminal-preventing protocol or sink fault is latched after execute and the worker stays alive
- **THEN** containment sends no unsafe follow-up protocol frame, kills at most once after the shared deadline, drains fully, and finalizes Failed

### Requirement: EOF and absent terminal are distinguished
Clean stdout EOF, partial-frame EOF, and process exit without a terminal SHALL remain distinct observations. After execute acceptance, clean EOF is not cancellation. If no terminal was committed by finality, the control plane SHALL classify the run from known control intent and process facts, otherwise as failed missing-terminal/crash evidence.

#### Scenario: Post-ready crash
- **WHEN** execute was accepted and the worker exits without a terminal or known controller termination intent
- **THEN** the run becomes failed once with missing-terminal and raw-exit diagnostics

#### Scenario: Partial frame at EOF
- **WHEN** stdout ends with a non-empty incomplete frame
- **THEN** the run becomes failed for invalid framing rather than generic clean EOF

### Requirement: A committed valid terminal is authoritative
A terminal SHALL be authoritative only when it passed protocol and bridge validation and the run-scoped state finalization gate reports it committed. Completed, Cancelled, or Failed then remains the authoritative UI/run outcome even when a post-acceptance input fault, contradictory exit, output/disposal failure after successful terminal flush, or later shutdown observation exists. Such contradictions SHALL be recorded as supplementary anomalies and SHALL NOT cause a second terminal mutation, duplicate fatal count, duplicate summary, or terminal rewrite.

#### Scenario: Matching terminal and exit
- **WHEN** a committed Completed, Cancelled, or Failed terminal is accompanied by its block-23-consistent managed exit evidence
- **THEN** the terminal outcome stands and no anomaly is added

#### Scenario: Terminal and exit mismatch
- **WHEN** a valid terminal is committed and the final raw exit contradicts its expected managed pairing
- **THEN** the terminal outcome stands and one bounded supplementary process-integrity anomaly is recorded

#### Scenario: Output failure after terminal flush
- **WHEN** terminal flush and projection committed before a later output or emitter-disposal failure
- **THEN** the terminal outcome stands and code-6 evidence is supplementary

#### Scenario: Projection rejection before commit
- **WHEN** a valid terminal frame is accepted but bridge projection is rejected before the atomic state finalization gate commits
- **THEN** the classifier commits that same validated terminal semantics through the gate; if another outcome already won, that recorded winner remains authoritative

#### Scenario: Indeterminate projection response
- **WHEN** projection returns no reliable acknowledgement
- **THEN** the classifier queries the durable gate receipt, uses the recorded terminal when present, and otherwise treats the atomic operation as uncommitted and finalizes Failed for projection failure exactly once

### Requirement: Block 23 exit semantics and precedence are preserved
The classifier SHALL consume, not redefine, managed exit meanings and precedence: output 6, infrastructure 5, invalid input 2, reserved busy 3, executor/domain 4, cancellation or shutdown 130, and completion 0. Forced termination and unmapped platform death SHALL remain explicit raw facts. No exit classification SHALL authorize automatic retry.

#### Scenario: No terminal with mapped exit
- **WHEN** a worker exits with 0, 2, 3, 4, 5, 6, or 130 but no terminal was committed
- **THEN** the control plane combines the exit with lifecycle and controller facts and does not infer a successful or busy domain outcome from the number alone

#### Scenario: Reserved busy with failed terminal
- **WHEN** a future worker emits a valid Failed terminal carrying safe busy detail and exits with reserved code 3
- **THEN** the Failed terminal remains authoritative, the exit is a matching advisory process observation, the UI retains existing failed-terminal behavior, and no retry is scheduled

### Requirement: Cancellation, forced termination, and shutdown remain distinguishable
A committed Cancelled terminal SHALL remain authoritative. If controller-requested Stop or host shutdown ends an admitted session without a committed terminal, an orderly managed cancellation/shutdown exit before execution evidence SHALL finalize as Cancelled; an accepted whole-process-tree kill after grace SHALL finalize as Cancelled with a forced-termination warning only when the exact session had a latched Stop or host-shutdown intent. Kill rejection, unrelated abrupt death, or missing terminal without exact-session termination intent SHALL finalize as Failed. Shutdown SHALL use the same finalizer before releasing the matching coordinator handle.

#### Scenario: Cooperative cancellation
- **WHEN** Stop produces a committed Cancelled terminal and managed exit 130
- **THEN** the run is Cancelled once with no fatal error and no kill anomaly

#### Scenario: Forced kill after exact-session Stop
- **WHEN** the exact session ignores a latched Stop, grace expires, tree kill is accepted, and no terminal is committed
- **THEN** the run is Cancelled once with a bounded forced-termination warning after exit and stream finality

#### Scenario: Kill failure
- **WHEN** tree kill fails and process ownership cannot be settled
- **THEN** the run is Failed once when final evidence permits classification and ownership remains attached until cleanup is actually settled

#### Scenario: Host shutdown before worker execution
- **WHEN** host shutdown is the known cause of an orderly managed exit before run-started and no terminal is expected
- **THEN** the admitted run is Cancelled once without fabricating a worker terminal frame

### Requirement: Diagnostics are bounded and redacted
The launcher SHALL continue draining stderr and retaining only its final 65,536 bytes with total/truncated metadata and replacement-safe decoding. Classification SHALL use typed safe categories, lifecycle phase, terminal/exit consistency, and a predefined operator action as the primary UI diagnostic. Arbitrary stderr SHALL NOT be copied verbatim into UI state. Any displayed stderr-derived excerpt SHALL be separately bounded, remove control characters, redact credentials, URI userinfo, connection strings, authorization material, tokens, command/request frames or payloads, and secret-like key/value data across case and common delimiter variants, and indicate truncation or redaction.

#### Scenario: Stderr flood
- **WHEN** stderr exceeds 65,536 retained bytes while the worker produces a valid completed terminal and matching exit
- **THEN** the run remains Completed, drainage finishes, and diagnostics retain only bounded tail and truncation metadata

#### Scenario: Secret-like stderr content
- **WHEN** retained stderr includes credentials, connection strings, bearer material, or secret-like values
- **THEN** no unredacted value appears in LastError, UI logs, or the classifier's rendered diagnostic

### Requirement: UI finalization and coordinator release are exactly once
Each admitted run SHALL have one linearizable finalization gate shared by normal terminal projection and abnormal classification. The winner SHALL perform the sole UI terminal mutation; terminal or abandonment cleanup SHALL close all run activities; callback acceptance SHALL then close; and only after state cleanup SHALL the exact matching coordinator handle be released. Late, duplicate, stale, cross-run, or post-finality events SHALL be rejected or ignored without state mutation, activity revival, or impact on a replacement run.

#### Scenario: Terminal races crash classification
- **WHEN** a valid terminal projection races process-finality classification
- **THEN** exactly one wins the finalization gate and the other records at most a supplementary anomaly without another UI terminal mutation

#### Scenario: Late event after release
- **WHEN** an event for the finalized run arrives after callback closure or coordinator release
- **THEN** it is ignored or rejected without changing state or the active replacement run

#### Scenario: Activity cleanup before release
- **WHEN** abnormal finalization occurs with activities still open
- **THEN** all run-owned activities are closed and visible state is terminal before the matching coordinator handle is released

### Requirement: Verification is deterministic and preserves fixture ownership
Block-26 real-process modes SHALL verify existing process-boundary classifications, while OS-start, readiness-timeout, execute-write/flush, sink/projection, and kill-rejection cases SHALL use deterministic injected seams where block 26 has no corresponding mode. Tests SHALL use gates, fake time, explicit exit/EOF signals, complete drainage, and unconditional process cleanup rather than sleeps, polling, or automatic retry.

#### Scenario: Block-26 matrix
- **WHEN** ready, success, no-work, pre-ready-crash, post-ready-crash, malformed, oversized, unknown-message, invalid-sequence, terminal/exit mismatch, stderr-flood, mapped/unmapped raw-exit, cooperative-cancel, and unresponsive modes are exercised
- **THEN** each produces the specified single outcome, anomaly, diagnostic, cleanup, and coordinator-release behavior without changing fixture semantics

## Audit Reconciliation

There is one exact-session internal deadline, started by whichever happens first: accepted Stop, host shutdown, or fault containment. It is the block-28 internal exact 10-second `TimeProvider` deadline, never a second timer. Classification must keep semantic rejection (a definite invalid/contradictory event), noncommit (no authoritative terminal commit), and indeterminate receipt (a terminal/projection attempt whose authoritative commit cannot be known) distinct; none may be silently upgraded to a committed terminal. The coordination and worker-event bridge capability contracts are modified to expose these bounded observations and finalization handoff without changing UI projection ownership.

