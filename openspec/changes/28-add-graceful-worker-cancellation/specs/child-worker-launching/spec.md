## RENAMED Requirements

- FROM: `### Requirement: Session disposal is single-owner and non-escalating`
- TO: `### Requirement: Session disposal joins the owned cancellation lifecycle`

## MODIFIED Requirements

### Requirement: Session disposal joins the owned cancellation lifecycle
The session SHALL implement idempotent asynchronous disposal. Disposal SHALL immediately suppress future sink callbacks and settle an unaccepted startup as disposed. If the exact process is still live, disposal SHALL start or join its existing cancellation operation, preserving the first Stop deadline and accepted-only cancel delivery. If process exit is already confirmed, disposal SHALL join settlement without creating another Stop request or deadline. The owner SHALL await actual process exit, both existing stream drains, and the cancellation deadline callback before closing controller stdin. It SHALL then join remaining control work and readiness timer callbacks before disposing other redirected streams, cancellation sources, and the process adapter exactly once. Raw completion SHALL remain independently observable, while Stop and disposal SHALL await resource settlement. A failed tree-kill attempt while the process remains live SHALL retain ownership and leave settlement incomplete.

#### Scenario: Completed session is disposed repeatedly
- **WHEN** asynchronous disposal is invoked more than once after confirmed process exit
- **THEN** every caller joins the same resource settlement, resources are released once, and no new cancellation command or deadline is created

#### Scenario: Live session is disposed before request acceptance
- **WHEN** disposal begins while the worker is waiting for readiness
- **THEN** future sink callbacks and execute delivery are suppressed, startup settles as disposed, stdin remains owned until exit and drain finality, and the single cancellation deadline may escalate against the exact live process without sending an unaccepted cancel command

#### Scenario: An admitted sink callback is active when disposal begins
- **WHEN** disposal races with a callback that already crossed admission
- **THEN** that callback is allowed to settle, later callbacks are suppressed, and resource disposal waits for its owning stream pump and process finality
