## Purpose

Provides Web operators a safe, accessible view of deployment policy and actual ProcessAssets child-worker lifecycle without exposing process or protocol internals or absorbing non-processing page state.

## ADDED Requirements

### Requirement: Web-hosted UI shows the immutable deployment mode
In a Web-hosted process, the system SHALL display the immutable startup deployment mode using exactly **Standard** or **Web-only**. Run-once SHALL expose no Web UI status because it starts no Web host. The displayed value MUST come from the already-resolved startup snapshot and MUST NOT reread, normalize, persist, or permit editing of the deployment-mode environment input.

#### Scenario: Standard Web host is displayed
- **WHEN** the immutable startup snapshot selects Standard and an operator opens or reconnects to the Web UI
- **THEN** the UI displays **Standard** and provides no mode editing control

#### Scenario: Web-only Web host is displayed
- **WHEN** the immutable startup snapshot selects Web-only and an operator opens or reconnects to the Web UI
- **THEN** the UI displays **Web-only** and provides no mode editing control

#### Scenario: Run-once is selected
- **WHEN** the immutable startup snapshot selects Run-once
- **THEN** no Web UI or Web status surface is started

### Requirement: Schedule policy is explained separately from saved schedule settings
The Web UI SHALL state that Standard permits internal scheduling and that Web-only disables internal scheduling by deployment policy. In Web-only, the explanation SHALL remain visible regardless of the saved enabled flag or cron value, SHALL NOT imply that saved schedule values were changed, and SHALL state that manual Dashboard runs remain available. The status surface MUST NOT add a setting, toggle, or other control.

#### Scenario: Standard permits scheduling
- **WHEN** the Web UI is running in Standard
- **THEN** it explains that internal scheduling is available and leaves the saved schedule's own enabled or disabled state to the existing schedule UI

#### Scenario: Enabled schedule is viewed in Web-only
- **WHEN** the Web UI is running in Web-only with a saved enabled schedule
- **THEN** it explains that internal scheduling is disabled by Web-only, that the saved value is retained, and that manual runs remain available

#### Scenario: Disabled schedule is viewed in Web-only
- **WHEN** the Web UI is running in Web-only with a saved disabled schedule
- **THEN** it gives the same deployment-policy explanation without claiming that Web-only changed the saved value

### Requirement: ProcessAssets worker lifecycle uses a closed user-visible vocabulary
The existing Dashboard and NavMenu status SHALL remain ProcessAssets-focused and display its lifecycle using exactly **Idle**, **Starting**, **Running**, **Cancelling**, or **Failed**. The value MUST be projected from the authoritative coordinator-owned worker session and finalized failure observations; Razor components MUST NOT infer it from processing counters, logs, process discovery, or `IsRunning` alone.

The mapping SHALL be: no owned or retained-failed worker session is Idle; an admitted child request that is resolving, launching, awaiting readiness, or accepting execution is Starting; a worker that accepted execution and has not begun cancellation or finalization is Running; an exact-session cancellation or host-stop request that is still settling is Cancelling; and an authoritative failed finality is Failed. Completed and orderly cancelled finality return to Idle. Failed remains visible until the next worker start or Web-host restart.

#### Scenario: Worker progresses through startup
- **WHEN** an admitted worker session progresses through command resolution, process start, readiness, and execution acceptance
- **THEN** the visible state remains Starting until execution is accepted and then changes to Running

#### Scenario: Active worker is cancelled
- **WHEN** cancellation is requested for the exact Starting or Running session and cleanup has not settled
- **THEN** the visible state is Cancelling until authoritative finality and cleanup are known

#### Scenario: Worker completes or is orderly cancelled
- **WHEN** the authoritative worker result is Completed or Cancelled and matching cleanup settles
- **THEN** the visible worker state becomes Idle

#### Scenario: Worker fails unexpectedly
- **WHEN** the finalized worker evidence authoritatively classifies the session as failed
- **THEN** the visible worker state becomes Failed instead of remaining Starting, Running, or Cancelling

### Requirement: ProcessAssets status excludes other jobs and local work
The existing card lifecycle SHALL describe only an admitted `ProcessAssets` child-worker session. `CoordinateLookup` and `CacheMutation` MUST NOT drive this card; their owning pages SHALL retain capability-specific state. Block 50's generic coordinator diagnostic projection SHALL remain separate and MUST expose no PID or JobId in UI. A local schedule check, work detector result, no-work decision that launches no child, database statistics refresh, or other Web-local activity MUST NOT fabricate Starting, Running, Cancelling, or Failed ProcessAssets status.

#### Scenario: Scheduled check finds no work
- **WHEN** Standard performs a local scheduled eligibility check and does not launch a child worker
- **THEN** worker status remains Idle while any existing no-work explanation follows its owning processing or schedule surface

#### Scenario: Web-local activity is in progress
- **WHEN** a Dashboard statistics refresh or another non-worker local activity is running
- **THEN** worker status remains based only on the coordinator-owned worker session

### Requirement: Status excludes internal and sensitive data
The user-visible status SHALL NOT include process IDs, run or job IDs, raw exit values, protocol names or frames, command arguments, environment values, stack traces, raw exception text, raw stderr, credentials, tokens, connection strings, or secrets. A Failed status MAY include one bounded, predefined operator-facing summary produced by the established safe failure classifier, but MUST NOT render arbitrary underlying detail. Existing safe Logs content MAY be linked for troubleshooting; this status change MUST NOT broaden what Logs stores or displays.

#### Scenario: Failure evidence contains sensitive stderr
- **WHEN** a failed worker's retained evidence includes a PID, raw protocol content, command data, or secret-like stderr
- **THEN** the status surface shows Failed and only an approved bounded summary, with none of the internal or sensitive values rendered

#### Scenario: Safe failure category is available
- **WHEN** the failure classifier supplies a predefined safe operator summary
- **THEN** the failure presentation may show that summary and a Logs navigation hint without exposing raw evidence

### Requirement: Status survives UI reconnection without durable runtime claims
Deployment and worker status SHALL live in a process-wide read-only snapshot rather than component-local fields. New components, browser reloads, and Blazor circuit reconnects within the same running Web host SHALL render the current snapshot immediately and then receive ordered change notifications. A Web-host restart SHALL reconstruct deployment mode from the new immutable startup snapshot and SHALL begin worker status at Idle unless that new coordinator actually owns a worker; the system MUST NOT persist stale worker status or failure evidence to settings or another durable file.

#### Scenario: Circuit reconnects during a worker run
- **WHEN** a Blazor circuit reconnects or a page is reloaded while the coordinator owns a Running worker
- **THEN** the newly rendered UI immediately shows the same mode and Running state without waiting for another worker event

#### Scenario: Web host restarts after a failure
- **WHEN** the Web host restarts after the prior process displayed Failed and no worker is owned by the new process
- **THEN** the UI shows the newly resolved mode and Idle rather than restoring a stale Failed or Running claim

### Requirement: Status changes are accessible and responsive
The detailed status SHALL use semantic labels that do not rely on color alone. Ordinary lifecycle changes SHALL be announced through a polite live status region, and a newly authoritative Failed transition SHALL be exposed as an alert without repeatedly announcing the same retained failure on unrelated state notifications. Compact navigation status SHALL have an accessible text alternative. Shared responsive rules SHALL live in `app.css` and SHALL preserve readable labels and non-overlapping layout at the existing narrow breakpoints.

#### Scenario: Screen reader observes lifecycle change
- **WHEN** worker status changes from Starting to Running
- **THEN** the updated textual label is available in a polite live region and is not conveyed only by the status-dot color

#### Scenario: Failure is retained across rerender
- **WHEN** one worker transition becomes Failed and unrelated processing notifications rerender the UI
- **THEN** the failure remains visible but is not repeatedly announced as a new alert

#### Scenario: Narrow viewport renders status
- **WHEN** the Dashboard or navigation is viewed at an existing mobile breakpoint
- **THEN** mode, schedule-policy, and worker labels remain readable without horizontal overlap or clipped essential text
