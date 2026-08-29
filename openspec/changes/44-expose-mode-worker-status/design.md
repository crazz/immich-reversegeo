## Context

See [proposal.md](proposal.md) for motivation and the [deployment-mode worker-status specification](specs/deployment-mode-worker-status/spec.md) for observable behavior. The checked-in source still shows the pre-migration baseline: `ProcessingState` owns processing counters/logs and a broad `OnChanged` event, Dashboard directly injects `ProcessingBackgroundService`, and NavMenu infers a dot from `IsRunning`/`LastError`. The finalized plans for blocks 13, 24–30, and 40–43 establish the coordinator-owned child session, immutable deployment-mode startup snapshot, safe failure classification, and Standard/Web-only composition, but their exact applied names are not present in this checkout yet. Block 44 implementation must re-read and consume those landed contracts rather than invent parallel lifecycle or mode ownership.

The original MASTERPLAN placeholder mentioned PID and run ID. The direct block-44 request supersedes that placeholder for this reconciliation: neither identity is user-visible. Exact identities remain internal only where prerequisite contracts need them for stale-event rejection and cleanup.

## Goals / Non-Goals

**Goals:**
- Build one process-wide, read-only presentation snapshot from the immutable mode selection and authoritative processing-worker session observations.
- Define a total mapping from finalized raw control states to the five exact operator labels.
- Keep worker lifecycle independent from processing counters, detector activity, and other local Web work.
- Give Dashboard and NavMenu a consistent accessible view that survives component/circuit replacement in the same host.
- Retain only classifier-approved bounded failure copy and notify observers once per effective status change.

**Non-Goals:**
- Add settings, toggles, APIs, persisted status, live mode changes, or worker controls.
- Expose PID, run/job identity, exit values, raw protocol/process details, exception text, stderr, or secrets.
- Change scheduling, worker launch/finality, `ProcessingState` counter semantics, Logs retention, or Run-once output.
- Implement block 45's cross-mode composition matrix, block 46's image tests, block 47's generalized worker-job protocol, block 50's generic coordinator diagnostics/arbitration, any non-ProcessAssets page state, or block 70's comprehensive deployment-mode guide.

## Decisions

### 1. Add a dedicated immutable Web status snapshot beside processing progress

Create a singleton ProcessAssets-focused read model (final name chosen from landed conventions) whose public surface is an immutable snapshot containing only:

- display mode: Standard or Web-only;
- derived internal-scheduling policy and fixed explanatory copy key;
- worker display state: Idle, Starting, Running, Cancelling, or Failed;
- optional classifier-approved bounded failure summary;
- a monotonic presentation revision for ordered/deduplicated notification.

Initialize mode from block 40's already-resolved immutable composition snapshot in the Web composition root. Do not read the environment in the model and do not add mode to `AppConfig`. Keep this model separate from `ProcessingState` unless the landed block-27 adapter already has a clearly bounded immutable snapshot abstraction that can be extended without mixing ownership. Dashboard and NavMenu consume the read-only interface; only an internal ProcessAssets coordinator/session observer can publish worker transitions. The existing card remains ProcessAssets-focused after blocks 47–50: `CoordinateLookup` and `CacheMutation` never drive it, their owning pages retain capability-specific state, and block 50's generic coordinator diagnostics remain a separate non-card boundary.

Alternative: add mode and lifecycle fields directly to `ProcessingState`. Rejected as the default because it encourages deriving worker state from run progress and makes unrelated counter notifications look like lifecycle changes. An adapter over the existing singleton is acceptable only if it preserves the separate immutable snapshot and effective-change notification rules.

### 2. Map finalized raw session facts, not Razor heuristics

Attach at the narrow coordinator/launcher observation point that already owns exact-session lifecycle and block-30 finality. Map the finalized raw control phases as follows:

| Authoritative observation | User label |
|---|---|
| No processing-worker session owned and no retained failure | Idle |
| Admitted/resolving/start attempted/pre-ready/ready/request write or flush not yet accepted | Starting |
| Execute accepted and no cancellation/finality operation active | Running |
| Exact-session Stop or host-stop cancellation is settling | Cancelling |
| Authoritative Failed terminal/final classifier result | Failed |
| Completed or orderly Cancelled finality after matching cleanup | Idle |

Publish Starting no earlier than processing-worker session admission. Therefore Standard's schedule calculation/work detector and its no-work outcome remain Idle when no child is admitted. Do not observe `ProcessingState.IsRunning`, logs, counters, operating-system process discovery, or PID. Internal session identity/generation is still used to discard late transitions, but is absent from the public snapshot.

Failed retains its safe summary after coordinator handle release so an unexpected exit cannot immediately look Idle. The next exact worker admission atomically replaces it with Starting; a host restart creates a fresh singleton at Idle. Alternative: retain every terminal state. Rejected because the requested vocabulary has no Completed/Cancelled labels and existing processing progress already reports those outcomes.

### 3. Preserve block-30's error-detail boundary

Accept only the predefined bounded operator summary produced by block 30's safe classifier/renderer. Do not construct messages from exceptions, raw stderr, commands, frames, environment, exit numbers, or existing `ProcessingState.LastError`, whose domain-error content has a different contract. The public snapshot type deliberately has no fields for PID, run ID, raw category payload, protocol, or arbitrary detail. Dashboard may link to Logs, but this change does not widen log content. Later generic coordinator diagnostics may use internal identity for ownership and stale-event rejection, but neither they nor this card render PID or JobId.

Alternative: expose a generic `Detail` string from the launcher. Rejected because a stringly boundary makes later accidental secret and internal-protocol disclosure likely.

### 4. Notify only on effective immutable snapshot changes

Serialize publications through the session owner or a short model lock, compare the full UI-safe value, increment the presentation revision, then notify after the new snapshot is visible. Idempotent, stale, or unrelated processing updates produce no lifecycle notification. New components read the current snapshot synchronously before subscribing, so browser reload and circuit replacement in the same host do not wait for another event.

This is process-lifetime continuity, not durable recovery. On host restart, block 40 resolves mode again and the new coordinator/read model begins Idle; no worker or failure status is written to `settings.json`, `/config`, `/data`, browser storage, or another file. This avoids reviving a worker claim the new process does not own.

Alternative: persist the last failure. Rejected because it would create a new data model/migration, could display stale ownership, and contradicts the coordinator as source of truth.

### 5. Put detailed policy on Dashboard and compact lifecycle in NavMenu

Add a read-only Dashboard status card before processing statistics so it remains visible even when database statistics fail. Reuse the existing `settings-card`, alert, stat-label/value, and lounge-theme patterns, with dedicated classes only for layout. Show:

- Deployment mode: Standard or Web-only;
- Internal scheduling: available in Standard, disabled by Web-only;
- the Web-only explanation that saved schedule values remain unchanged and manual runs remain available;
- Worker: one exact lifecycle label;
- on Failed, the approved summary and a Logs link.

Adapt NavMenu's existing status dot to use the worker snapshot, add a textual/accessible `Worker: <label>` equivalent, and never let color or a title attribute carry the only meaning. Keep the full mode/schedule explanation on Dashboard to avoid crowding navigation. Logs needs no status panel or storage change.

Alternative: place mode controls in Settings. Rejected because deployment mode is startup-owned and read-only. Alternative: show detailed status on every page. Rejected because Dashboard plus persistent navigation gives sufficient visibility without duplication.

### 6. Use semantic live regions and shared responsive CSS

Dashboard's ordinary lifecycle text uses a stable `role="status"`/polite live region. Failed copy uses the existing error-alert visual language and alert semantics; unchanged retained content is not recreated from unrelated `ProcessingState` notifications. NavMenu includes real accessible text (visually hidden only where necessary) and marks decorative dots accordingly. Add all layout, state-color, wrapping, and narrow breakpoint rules to `src/ImmichReverseGeo.Web/wwwroot/app.css`; do not create scoped Razor CSS. Respect reduced-motion preferences for any Starting/Running animation and ensure state differences remain textual.

### 7. Keep verification within block 44

Use pure mapping/read-model tests and the landed focused Razor rendering seam from prerequisite Web-mode work. Cover exact labels/copy, every raw-to-display transition, stale/idempotent suppression, failure retention and clearing, safe-summary boundary, no-child no-work, reconnect/new-consumer snapshot behavior, semantic markup, and responsive class hooks. Do not duplicate block 45's service-composition matrix or block 46's process/image tests.

## Risks / Trade-offs

- **[Risk] Prerequisite source names differ from their plans** → At apply time, stop and reconcile against the landed block 13/24–30/40–43 APIs before editing; do not create a second mode resolver, coordinator, or failure classifier.
- **[Risk] Failed retention is mistaken for a currently owned process** → Pair Failed with past-tense explanatory copy and no active identity; clear it only on a new admission or host restart.
- **[Risk] Broad `ProcessingState.OnChanged` causes repeated announcements** → Give the status snapshot its own effective-change revision/event and stable live-region DOM.
- **[Risk] Raw state gains phases later** → Make the mapping exhaustive and fail tests on an unmapped state rather than silently calling it Running.
- **[Risk] NavMenu becomes crowded on mobile** → Keep full policy copy on Dashboard and wrap or visually compact only the redundant prefix while preserving accessible text.
- **[Risk] Users interpret Web-only as modifying the saved schedule** → Use explicit policy copy that the value is retained and manual runs remain available.

## Migration Plan

1. Verify blocks 13, 24–30, and 40–43 are applied and identify their exact immutable mode, worker-session observation, safe diagnostic, and render-test contracts.
2. Add the singleton read-only snapshot and raw-state adapter without changing existing pages.
3. Route authoritative processing-worker transitions through it, then add Dashboard/NavMenu rendering and shared CSS.
4. Add focused state and UI tests plus concise public docs; run the normal test suite and strict OpenSpec checks.
5. Rollback removes the status projection and rendering only. There is no settings or data migration and no durable status to clean up.
