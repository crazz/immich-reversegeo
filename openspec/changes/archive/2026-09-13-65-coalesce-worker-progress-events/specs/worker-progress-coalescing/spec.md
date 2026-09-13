## Purpose

Keeps validated worker-job progress bounded and responsive while preserving protocol integrity, diagnostic history, activity lifecycles, and authoritative final outcomes across processing and other worker-backed Web operations.

## ADDED Requirements

### Requirement: Coalescing occurs only after full protocol acceptance
The system SHALL decode, frame-check, correlate, and apply the selected v1 or v2 protocol's exact sequence and lifecycle validation to every stdout frame before considering it for coalescing. Coalescing MUST NOT occur in the byte reader, codec, or primary stream validator and MUST NOT turn a malformed, missing, duplicated, regressive, wrong-kind, wrong-identity, post-terminal, or otherwise invalid frame into an accepted event.

#### Scenario: Invalid progress-shaped frame is received
- **WHEN** stdout contains a frame that resembles progress but fails framing, codec, sequence, correlation, kind, payload, or lifecycle validation
- **THEN** the established protocol failure path observes the original failure and no coalesced state update is produced

#### Scenario: Raw stream has a sequence gap
- **WHEN** the primary validator receives sequence N followed by N+2
- **THEN** it rejects the stream gap even if both decoded payloads would otherwise be replaceable progress

### Requirement: Replaceability is closed and capability-specific
The system SHALL treat an event as replaceable only when the finalized job contract declares that concrete event to be a complete current-state snapshot whose omitted intermediate values have no independent lifecycle or diagnostic meaning. V1 `progress/progress-changed` and the equivalent v2 `ProcessAssets` absolute-count snapshot SHALL be replaceable. A v2 capability progress event MAY be replaceable only through an explicit typed descriptor policy; an unknown event or a transition/step not so declared MUST be lossless. Ready, job/run started, eligibility, every activity start/end, every log at every level, warnings, errors, typed results, completed/cancelled/failed terminals, and controller-classified failures MUST NOT be discarded or replaced by this capability. Existing log payload and retention bounds remain authoritative elsewhere.

#### Scenario: Absolute processing snapshots burst
- **WHEN** several coherent absolute ProcessAssets snapshots for the same active job arrive without an intervening lossless event
- **THEN** only the newest applicable snapshot is required for downstream state projection and all earlier snapshots may be recorded as superseded

#### Scenario: Logs and activities interleave with progress
- **WHEN** trace, information, warning, or error logs or activity start/end events occur between progress snapshots
- **THEN** every such event is delivered exactly once in accepted order and is never classified as replaceable because log storage is bounded elsewhere

#### Scenario: Capability event has no replaceability declaration
- **WHEN** a CoordinateLookup, CacheMutation, or future job emits a progress-like event not explicitly declared to be a full-state snapshot
- **THEN** the event follows the lossless path

### Requirement: Each active job has bounded sequence-aware delivery state
For the one active worker session, the system SHALL maintain a bounded lossless FIFO plus at most one pending replaceable snapshot for the exact protocol version, job kind, and job identity. The pending slot SHALL use latest-wins replacement. Buffer capacity SHALL be a named internal policy with test injection, SHALL NOT be protocol or public configuration, and SHALL use a safe initial lossless capacity of 256 entries and one replaceable slot unless the required burst measurement demonstrates and records a different value before production enablement. A full lossless FIFO SHALL apply awaited backpressure; it MUST NOT drop a lossless event, busy-spin, synchronously block a Blazor renderer, or prevent the launcher's independently running stderr drain and process-exit observation from progressing.

#### Scenario: Replaceable slot is repeatedly overwritten
- **WHEN** validated snapshots arrive faster than downstream projection while no lossless barrier intervenes
- **THEN** memory use remains one pending snapshot and the slot retains the highest accepted source sequence

#### Scenario: Lossless capacity is saturated
- **WHEN** the lossless FIFO reaches its configured test or production capacity
- **THEN** the accepted-event producer waits asynchronously until the dedicated consumer advances, while stderr drainage, exit observation, cancellation, and disposal can continue without a circular wait

#### Scenario: Arbitrary lossless traffic exceeds every bounded consumer
- **WHEN** lossless producers remain faster than consumers indefinitely
- **THEN** the system preserves losslessness through bounded backpressure rather than promising impossible unbounded drainage or silently dropping events

### Requirement: Coalesced sequence gaps are explicit and narrow
The raw accepted stream sequence SHALL remain unchanged and exact; the coalescer MUST NOT renumber protocol events. A delivered snapshot SHALL carry internal evidence of the contiguous accepted replaceable sequence range it represents. A downstream bridge MAY advance over that declared range only when every omitted sequence was validated, belonged to the same version/kind/identity, and was superseded before any lossless barrier. Any unexplained gap, overlap, regression, identity change, kind change, or range containing a lossless event SHALL fail closed without state mutation.

#### Scenario: Latest snapshot represents a contiguous range
- **WHEN** validated replaceable snapshots at sequences 10, 11, and 12 are superseded before delivery
- **THEN** downstream receives sequence 12 with evidence that only accepted replaceable sequences 10 through 11 were intentionally suppressed

#### Scenario: Lossless event separates snapshots
- **WHEN** a log at sequence 11 occurs between snapshots 10 and 12
- **THEN** snapshot 10 is flushed before log 11 and snapshot 12 cannot claim sequence 10 or 11 as a coalesced gap

### Requirement: Lossless events and terminal finality are ordering barriers
Before delivering a lossless event, the system SHALL first deliver the latest pending snapshot whose source sequence precedes that event. A valid terminal SHALL close intake for that job, flush the latest pending snapshot, drain all earlier lossless events, project the terminal last, and complete a finality receipt only after those projections and the terminal's final UI notification succeed or one established bridge failure is recorded. Process/session finalization and admission release MUST await this receipt together with existing stdout, stderr, protocol, and bridge finality. No post-terminal update SHALL be delivered.

#### Scenario: Terminal arrives during a progress burst
- **WHEN** a terminal follows one or more pending snapshots
- **THEN** observers see the newest pre-terminal snapshot before the terminal and terminal remains the last projected worker event

#### Scenario: Transport ends without a valid terminal
- **WHEN** EOF, crash, protocol failure, forced stop, or disposal prevents a valid terminal
- **THEN** the coalescer does not fabricate a terminal and hands its bounded drain/abandonment observation to the existing controller classifier

### Requirement: Stale sessions cannot mutate current UI state
Every queued item, timer callback, drain receipt, and notification SHALL remain correlated to the exact protocol version, job kind, job identity, owner/session generation, and page operation generation where the finalized UI contract supplies one. A stale, wrong-kind, wrong-identity, superseded, duplicate, disposed, or non-owner callback MUST NOT mutate or clear a newer job's state, release its handle, or trigger a Blazor notification.

#### Scenario: Old timer fires after a new job starts
- **WHEN** a cadence callback from a completed or disposed job runs after a new owner generation is active
- **THEN** the callback is ignored and cannot flush old progress into the new job snapshot

#### Scenario: Lookup page is disposed
- **WHEN** navigation or circuit disposal marks the page operation disposed while queued callbacks remain
- **THEN** stale rendering is suppressed while the owner-bound stop and stream/finality cleanup continue through the existing asynchronous path

### Requirement: Cancellation, shutdown, and disposal have bounded non-fabricating cleanup
Cancellation, host shutdown, and disposal SHALL atomically stop new intake for the affected session, wake blocked producers/consumers, cancel cadence timers, and join one idempotent asynchronous drain/abandon operation. A valid already-accepted terminal SHALL retain the terminal barrier. Without a valid terminal, cleanup SHALL preserve already-projected lossless facts, SHALL not invent success or a protocol terminal, and SHALL return a bounded observation to existing classification. Disposal MUST NOT synchronously wait on the Blazor renderer or leave timer callbacks able to mutate state after ownership ends.

#### Scenario: Cancellation races terminal acceptance
- **WHEN** cancellation and a valid terminal race
- **THEN** exactly one atomic intake decision determines whether the accepted terminal drains through the barrier or the nonterminal classifier path completes, with no duplicate terminal or orphaned waiter

#### Scenario: Host shutdown occurs under backpressure
- **WHEN** shutdown begins while a producer awaits capacity and projection is active
- **THEN** all waiters are released or joined by the bounded cleanup path and process/stdout/stderr finality cannot deadlock on a UI callback

### Requirement: UI notifications are cadence-limited without losing state
State mutation SHALL preserve every delivered lossless event and latest snapshot, but ordinary Blazor change notification SHALL publish at most once per 100 milliseconds per read model by default. The cadence SHALL use injected `TimeProvider` time and a named internal option so tests use virtual time. The production value SHALL be confirmed by the required measurement and MAY be changed before enablement when the recorded evidence supports it. Authoritative terminal, retained failure, cancellation-finality, and disposal-finality boundaries SHALL flush one immediate final notification after all preceding state mutations and SHALL suppress a redundant scheduled notification for the same revision. New/reconnected components SHALL read the latest immutable snapshot immediately rather than waiting for a tick.

#### Scenario: Many updates arrive inside one cadence window
- **WHEN** progress and lossless state mutations produce multiple dirty revisions within 100 milliseconds
- **THEN** subscribers receive no more than one ordinary cadence notification and that notification exposes the newest complete snapshot

#### Scenario: Terminal arrives before the next tick
- **WHEN** terminal projection follows dirty progress or logs before the scheduled notification
- **THEN** the final snapshot is notified immediately once, pending cadence work for that revision is cancelled, and the terminal receipt completes afterward

### Requirement: Compatibility is preserved across worker protocol generations and job kinds
The change SHALL preserve all v1 canonical bytes, exact raw sequence validation, one-run identity, event meanings, and rollback behavior. V2 `ProcessAssets` SHALL produce ProcessingState lifecycle, counts, logs, activities, terminal state, and classified outcome equivalent to v1 for the same source events. CoordinateLookup and CacheMutation SHALL retain their page-independent state, generation, cancellation, result, attribution, and finality contracts and MUST NOT project transient job state into ProcessingState.

#### Scenario: V1 and v2 processing receive the same burst
- **WHEN** equivalent v1 processing and v2 ProcessAssets streams contain the same absolute snapshots and lossless events
- **THEN** both yield equivalent final visible state and ordering while all pre-coalescing codec goldens remain unchanged

#### Scenario: Lookup and cache jobs coalesce declared snapshots
- **WHEN** their typed descriptor marks a progress payload replaceable
- **THEN** only that page/controller read model uses latest-wins delivery while its logs, activities, results, terminal, stale-generation checks, and release barrier remain lossless

### Requirement: Measurement and observation remain bounded and separable
Before production defaults are enabled, a repeatable burst/process/Blazor measurement SHALL record input rate, delivered snapshot rate, lossless queue high-water mark and wait time, coalesced count, projection latency, notification rate, terminal-flush latency, allocation or retained-memory behavior, and test environment. Block 65 SHALL expose a bounded internal observation snapshot or callback for these facts, with no raw messages, identifiers, secrets, or unbounded labels. Block 66 MAY translate that seam into telemetry but block 65 MUST NOT define metric names, exporters, dashboards, or alert policy.

#### Scenario: Default policy is reviewed
- **WHEN** the burst benchmark and real child-process fixture complete on the documented representative environment
- **THEN** the chosen capacity and cadence, including retaining or changing 256 and 100 milliseconds, are recorded with the results and all correctness tests pass

#### Scenario: Telemetry is not yet implemented
- **WHEN** block 65 is applied before block 66
- **THEN** coalescing behavior and bounded observation remain fully testable without a telemetry provider
