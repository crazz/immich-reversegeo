# processing-work-detection Specification

## Purpose

Processing work detection gives the lightweight Web control plane a stable advisory scheduled-launch decision while preserving worker authority, current eligibility, and safe evolution to cheaper detector strategies.

## Requirements

### Requirement: Immutable advisory request and result
The system SHALL evaluate scheduled work before admission through an immutable request containing only the scheduled trigger and an immutable logical detection snapshot. No processing identity exists at this point, so the request SHALL contain no RunId or JobId. The snapshot SHALL identify the control-plane purpose and eligibility coverage/strategy, and SHALL NOT contain SQL, schema names, connection data, row identities, exact counts, cursor values, work sets, or processing settings. A successful evaluation SHALL return one immutable result with `HasWork` as the sole launch-gating value and bounded low-cardinality diagnostic metadata limited to detector strategy, logical coverage, and fallback use. Callers SHALL NOT branch processing behavior on diagnostic metadata.

#### Scenario: Scheduled caller evaluates current full eligibility
- **WHEN** a due scheduled occurrence requests the current full-eligibility strategy before identity creation or admission
- **THEN** the detector receives that immutable request and returns one `HasWork` decision with safe bounded metadata

#### Scenario: Result is inspected for diagnostics
- **WHEN** a caller records or tests a successful detector result
- **THEN** it can observe strategy, logical coverage, and fallback use but cannot obtain an exact count, SQL detail, credentials, asset identity, work set, or cursor value

### Requirement: Current eligibility remains unchanged
The initial detector SHALL report work exactly when the current eligibility query observes at least one asset whose city and country are both null, latitude and longitude are both present, and asset deletion time is null. Its first adapter SHALL preserve existing behavior by evaluating the current exact count once and mapping a positive value to `HasWork = true`, but the detector contract SHALL make no exact-count promise. Dashboard statistics SHALL retain their exact count, and the processing worker SHALL retain its independent authoritative exact eligibility count and zero gate.

#### Scenario: Current count-backed adapter finds eligible work
- **WHEN** the unchanged exact eligibility query returns a positive count for a scheduled detector request
- **THEN** the detector reports `HasWork = true` without returning or publishing that count

#### Scenario: Current count-backed adapter finds no eligible work
- **WHEN** the unchanged exact eligibility query returns zero for a scheduled detector request
- **THEN** the detector reports `HasWork = false` with safe metadata and no exact-count value

#### Scenario: Dashboard refreshes statistics
- **WHEN** Dashboard statistics are requested
- **THEN** the exact repository count remains available and the request does not use the scheduled detector as a substitute

### Requirement: Detection is lightweight and side-effect free
A detector evaluation in this change SHALL perform only the database read needed for eligibility. It SHALL NOT mutate Immich data or schema, read or mutate skipped-asset storage, read processing configuration, fetch batches, initialize or access geodata/resolver/cache/airport services, create protocol events, enrich a worker request, resolve an execution backend, launch a worker, or persist detector state. Detector completion SHALL NOT update processing state directly. Existing scheduled preflight handling SHALL own no-work/cancellation/failure closure without a processing identity or state lifecycle; only a positive observation may proceed to the existing admission, pending, adapter, child-dispatch, and identity-checked cleanup path.

#### Scenario: Detector reports no work
- **WHEN** a scheduled evaluation completes with `HasWork = false`
- **THEN** the detector itself has performed no mutation or heavy-service side effect and the existing scheduled preflight closes with bounded no-work logging and no run identity, admission, or ProcessingState transition

#### Scenario: Detector reports work
- **WHEN** a scheduled evaluation completes with `HasWork = true`
- **THEN** the detector itself has not launched or configured a worker and the existing coordinator attempts shared admission before deciding whether to dispatch a newly admitted request

### Requirement: Cancellation and failure are not no-work decisions
The detector SHALL observe the exact existing preflight token linked to scheduler/host cancellation. Matching cancellation SHALL be surfaced as cancellation, and an unexpected detector fault SHALL be surfaced as failure; neither SHALL be converted to `HasWork = false` or a successful result. Existing scheduled preflight handling SHALL retain bounded safe logging and cancellation/drain cleanup, with no run identity, admission, ProcessingState lifecycle, backend resolution, worker launch, fallback, or automatic retry.

#### Scenario: Evaluation is cancelled
- **WHEN** the matching preflight token is cancelled before detection completes
- **THEN** cancellation is propagated distinctly from no work and the scheduled path performs its existing pre-admission cancellation cleanup without creating a processing lifecycle

#### Scenario: Evaluation fails
- **WHEN** the eligibility read or detector adapter fails unexpectedly
- **THEN** failure is propagated distinctly from no work and only bounded safe detail reaches the existing local failure presentation

### Requirement: Only internal scheduled launches use the detector
Standard-mode internal scheduling SHALL use exactly one detector evaluation before run identity creation, shared admission, pending-state publication, and matching state-adapter arming. Only positive detection SHALL proceed to ordinary shared admission; only admitted work may publish pending, arm the adapter, and dispatch one child. Dashboard manual processing, Web-only mode, and public Run-once execution SHALL bypass the detector: manual processing proceeds through its existing admitted child path, Web-only starts no internal scheduler or detector activity, and Run-once proceeds directly to the worker-side advisory lock and authoritative count. This change SHALL preserve the scheduler's pinned configuration snapshot, preflight cancellation/shutdown drain, shared admission, pending-state order, and admitted-attempt cleanup behavior.

#### Scenario: Standard scheduled occurrence is admitted
- **WHEN** a due Standard-mode occurrence detects current work and then acquires shared admission
- **THEN** detection has occurred exactly once before identity/admission/pending/adapter creation and the admitted request follows the existing child-dispatch lifecycle

#### Scenario: Positive detection loses admission
- **WHEN** detection reports work but another processing or maintenance operation owns shared admission
- **THEN** the scheduled occurrence follows its existing contention outcome with no pending state, adapter, backend, child, reservation, retry, or queue

#### Scenario: Dashboard manual run is admitted
- **WHEN** a user starts a manual processing run
- **THEN** the run does not invoke scheduled work detection and retains its existing authoritative worker lifecycle

#### Scenario: Web-only host is running
- **WHEN** saved schedule settings are enabled in Web-only mode
- **THEN** no internal schedule or detector evaluation starts while manual processing remains available

#### Scenario: Run-once process starts
- **WHEN** public Run-once execution is selected
- **THEN** it does not register or invoke scheduled detection and retains one advisory-lock-protected authoritative count

### Requirement: Detector observations do not create a stable work set
A detector result SHALL describe only the completed observation and SHALL NOT reserve rows, guarantee later eligibility, or establish an atomic snapshot with worker admission or execution. A positive result followed by a worker authoritative count of zero SHALL remain an ordinary launched no-work run. A negative result followed by newly eligible work SHALL leave the completed scheduled occurrence closed and defer that work to a later ordinary trigger. Neither race SHALL cause fallback, replacement launch, replay, resubmission, catch-up, or automatic retry.

#### Scenario: Work disappears after positive detection
- **WHEN** detection reports work but the worker's authoritative count later observes zero
- **THEN** the single launched worker completes through its ordinary zero-work lifecycle without fallback or retry

#### Scenario: Work appears after negative detection
- **WHEN** detection reports no work and an asset becomes eligible immediately afterward
- **THEN** the local occurrence remains complete and the asset waits for a later ordinary trigger

### Requirement: Full-eligibility strategy evolution does not expose storage queries
Later detector implementations SHALL reuse the same request/result authority boundary. An existence implementation may change only how current full eligibility is observed. The finalized contract SHALL expose no watermark source, incremental coverage, cursor, persistence, reconciliation identity, or NAS-specific schedule mode; changes 61–64 authorize none of those behaviors. Any future alternative requires new evidence and explicit revision of those planning decisions before it can extend this contract.

#### Scenario: Existence detector replaces the count-backed adapter
- **WHEN** a later change selects a bounded existence implementation for current full eligibility
- **THEN** callers retain the same `HasWork`, cancellation, failure, race, and authority semantics without receiving SQL details

#### Scenario: Rejected strategy values are inspected
- **WHEN** a caller attempts to select incremental, watermark, reconciliation, or NAS-specific coverage
- **THEN** the immutable contract rejects the unsupported value and every scheduled check retains current full-eligibility behavior
