## Purpose

Makes child-process isolation the normal internal processing behavior while preserving control-plane lifecycle and empty-schedule guarantees during the short transition to removal of in-process production execution.

## ADDED Requirements

### Requirement: Child worker is the internal default
The system SHALL select the child-worker backend by default for every admitted manual processing request and every admitted scheduled request whose pre-launch detector reports eligible work.

#### Scenario: Default manual processing
- **WHEN** a manual request is admitted without an explicit internal composition override
- **THEN** the system resolves and dispatches exactly one child-worker backend

#### Scenario: Default eligible scheduled processing
- **WHEN** a scheduled request is admitted and its pre-launch detector reports eligible work without an explicit internal composition override
- **THEN** the system resolves and dispatches exactly one child-worker backend

### Requirement: Empty schedules resolve no backend
The system SHALL complete a detected empty scheduled request through the established local zero-work lifecycle without resolving an in-process or child-worker backend.

#### Scenario: Scheduled detector reports no work
- **WHEN** an admitted scheduled request's pre-launch detector reports no eligible work
- **THEN** the system launches no worker, resolves no processing backend, emits no worker protocol event, and returns the control plane to its defined idle zero-work state

### Requirement: Temporary fallback is internal and explicit
Until the block-38 removal change is applied, the system SHALL permit explicit in-process selection only through a code-level composition seam and SHALL NOT expose backend selection through persisted settings, environment variables, command-line arguments, endpoints, or UI.

#### Scenario: Test composition selects in-process
- **WHEN** an authorized test composition explicitly selects the temporary in-process value before host construction
- **THEN** admitted processing uses the in-process backend under the same coordinator contract

#### Scenario: Deployed operator supplies configuration
- **WHEN** an operator changes application settings, environment values, command-line values, or UI inputs
- **THEN** none of those inputs can select the temporary in-process backend

### Requirement: Selected child prerequisites fail visibly
The system SHALL validate prerequisites for the selected internal backend during host startup without resolving a per-run backend or constructing worker-only geodata services, and SHALL fail startup visibly when the default child-worker prerequisites are invalid or unavailable.

#### Scenario: Child prerequisite is unavailable at startup
- **WHEN** the default child selection cannot locate or compose its required internal-worker launch artifact or service graph
- **THEN** host startup fails with an actionable diagnostic and does not select or execute the in-process backend

#### Scenario: Child prerequisites are valid at startup
- **WHEN** the default child selection's prerequisites are valid
- **THEN** startup completes without launching a child process or resolving a run-scoped processing backend

### Requirement: Backend selection is final per run
The system SHALL freeze one backend selection on each admitted run and SHALL NOT automatically retry, resubmit, or fall back to the other backend after dispatch begins.

#### Scenario: Child execution fails
- **WHEN** the selected child path encounters startup, handshake, protocol, projection, process-exit, cancellation-escalation, or cleanup failure
- **THEN** the established classifier and finalizer publish one authoritative outcome and no in-process execution is attempted

#### Scenario: Duplicate trigger is rejected
- **WHEN** a second trigger arrives while the coordinator owns an admitted run or its cleanup
- **THEN** the second trigger resolves no backend and causes no child or in-process execution

### Requirement: Control-plane lifecycle remains backend-independent
The default change SHALL preserve the established request identity, pending/eligibility timing, processing counters, logs, activities, cancellation authorization, terminal outcome, cleanup, exact-handle release, and retrigger behavior for manual and eligible scheduled requests.

#### Scenario: Default child run reaches a terminal outcome
- **WHEN** a default child run completes, is cancelled, or fails
- **THEN** the control plane exposes the same outcome-specific state and exactly-once cleanup contract established for explicit child execution

#### Scenario: Stop targets a default child run
- **WHEN** Stop is accepted for the active default child run
- **THEN** cancellation targets only that exact run and follows cooperative cancellation, bounded escalation, stream drainage, terminal classification, and matching-handle release without fallback

### Requirement: Child role ships with the application
The system SHALL launch the internal worker role from the same application assembly and deployment image as the Web control process.

#### Scenario: Production artifact is packaged
- **WHEN** the production deployment artifact is built for this change
- **THEN** it contains the internal worker entry point and all runtime dependencies needed by the default child launch without requiring a second worker image or separately deployed assembly

## Audit Reconciliation

Block 36 must be applied first. Preserve four distinct outcomes: authoritative committed worker terminals; local admission rejection (no child); advisory Busy (the canonical failed child terminal with no eligibility and four zero counts); and forced raw kill, which is transport evidence classified through block 30 and is not itself a terminal. No fallback, retry, replay, or in-process execution follows any of them.

