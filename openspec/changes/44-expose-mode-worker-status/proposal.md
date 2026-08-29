## Why

The current Web UI can show processing progress without telling an operator which deployment policy is active or whether a child worker is actually starting, running, cancelling, or failed. This makes Web-only scheduling behavior and worker failures easy to misread, especially after a Blazor reconnect or page reload.

## What Changes

- Add a read-only deployment summary to Web-hosted UI with the exact operator labels **Standard** and **Web-only**; Run-once has no Web UI.
- Explain that Standard permits internal scheduling while Web-only disables internal scheduling by deployment policy without changing saved schedule values or disabling manual runs.
- Add a read-only ProcessAssets worker lifecycle summary with the exact labels **Idle**, **Starting**, **Running**, **Cancelling**, and **Failed**, derived from authoritative processing coordinator/worker-session observations rather than Razor inference or processing counters.
- Retain a safe Failed presentation after an unexpected worker failure, but expose no PID, run ID, raw exit code, protocol detail, command line, secret, or unbounded exception/stderr content.
- Preserve current ProcessAssets state across component creation, Blazor reconnect, and browser reload within the running Web host; a full host restart rebuilds the mode snapshot and starts with no claimed processing worker. Later `CoordinateLookup`/`CacheMutation` pages own their state, and block 50 owns separate generic coordinator diagnostics without PID/JobId UI.
- Add accessible live status/failure announcements and responsive styling in the shared `app.css`, with no new settings or controls.
- Document the visible mode, schedule policy, and worker labels, including how Web-only differs from a disabled saved schedule.

## Capabilities

### New Capabilities
- `deployment-mode-worker-status`: Defines the safe, read-only deployment-policy and ProcessAssets child-worker lifecycle information shown in Web-hosted modes; it is not a generalized worker-job card.

### Modified Capabilities
- None.

## Impact

This change consumes block 40's immutable resolved deployment-mode snapshot and the finalized coordinator/launcher/failure facts from blocks 13 and 24–30. Implementation affects the Web-facing status projection, `ProcessingState` notification integration or a sibling singleton read model, `Dashboard.razor`, `NavMenu.razor`, shared `wwwroot/app.css`, focused MSTest coverage, and concise public documentation. It does not add configuration, persistence files, protocol fields, worker controls, scheduling behavior, or block 45's cross-mode composition matrix.
