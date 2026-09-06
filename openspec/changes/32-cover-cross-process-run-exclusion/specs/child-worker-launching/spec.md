## MODIFIED Requirements

### Requirement: Accepted protocol events are delivered in order
Every stdout frame accepted by the shared codec and stateful stream validator SHALL be offered exactly once to a caller-provided asynchronous accepted-event sink in stream order. Invalid frames SHALL NOT be delivered. Immediately after a fully validated exact-correlated terminal is recorded and before sink callback admission, the session SHALL independently seal new controller-input writes and start its separately tracked terminal-input completion action. That successful action SHALL allow a canonical write already admitted through the sole writer to settle, then invoke the existing one-time controller-input half-close owner; it SHALL not be awaited by the stdout pump, project an event, create cancellation policy, deadline, or escalation. Before raw process exit, a physical close failure SHALL record `TerminalInputCloseFailed`, preserve the accepted terminal, and enter the existing bounded containment lifecycle with InputTransport classification; it SHALL not replace terminal authority, fault the shared close task, self-join, or retry. Post-exit cleanup remains best effort. The first protocol failure SHALL stop further sink callbacks while raw stdout drainage continues. Sink failure SHALL be recorded as a raw session observation, SHALL stop further sink callbacks, and SHALL NOT stop stdout/stderr drainage, process-exit observation, or accepted-terminal input completion. The launcher SHALL NOT project events into `ProcessingState` or assign UI/domain meaning.

#### Scenario: Worker emits a normal run lifecycle
- **WHEN** ready, run events, and one terminal frame pass shared validation
- **THEN** the sink receives each accepted event once in sequence order, terminal input completion starts before terminal callback admission, and completion retains the accepted terminal event

#### Scenario: Event sink rejects an accepted event
- **WHEN** the caller sink fails while handling an accepted event
- **THEN** the first sink failure is retained, no later sink callback occurs, and both redirected streams continue draining through process completion

#### Scenario: Terminal callback is suppressed or fails
- **WHEN** a fully validated exact-correlated terminal is recorded after callbacks were suppressed or its callback admission fails
- **THEN** the separately tracked terminal-input completion still seals writes and invokes the existing one-time half-close owner; a pre-exit physical close failure follows the typed bounded InputTransport containment path while post-exit cleanup remains best effort

#### Scenario: Earlier sink failure precedes terminal input-close failure
- **WHEN** an earlier accepted event has recorded the first sink failure and suppressed later callbacks, then a fully validated exact-correlated terminal starts its input completion and the physical close fails before exit
- **THEN** the first sink failure remains the first raw observation, the terminal-input close failure is retained as an independent monotonic session fact, and the finalizer continues monitoring it to join the existing same-session containment lifecycle without a duplicate close, timer, Stop request, or terminal rewrite

### Requirement: Session disposal joins the owned cancellation lifecycle
The session SHALL implement idempotent asynchronous disposal. Disposal SHALL immediately suppress future sink callbacks and settle an unaccepted startup as disposed. If the exact process is still live, disposal SHALL start or join its existing cancellation operation, preserving the first Stop deadline and accepted-only cancel delivery. If process exit is already confirmed, disposal SHALL join settlement without creating another Stop request or deadline. A terminal-input completion already started from a fully validated exact-correlated terminal SHALL remain separately tracked; disposal joins the same attempt and SHALL not create a second close or reinterpret it as Stop. Pre-exit physical close failure is typed bounded containment, while post-exit cleanup is best effort. The owner SHALL await actual process exit, both existing stream drains, and the cancellation deadline callback before closing controller stdin if that stdin has not already been terminal-closed. It SHALL then join remaining control work and readiness timer callbacks before disposing other redirected streams, cancellation sources, and the process adapter exactly once. Raw completion SHALL remain independently observable, while Stop and disposal SHALL await resource settlement. A failed tree-kill attempt while the process remains live SHALL retain ownership and leave settlement incomplete.

#### Scenario: Completed session is disposed repeatedly
- **WHEN** asynchronous disposal is invoked more than once after confirmed process exit
- **THEN** every caller joins the same resource settlement, resources are released once, and no new cancellation command, deadline, or controller-input close is created

#### Scenario: Live session is disposed before request acceptance
- **WHEN** disposal begins while the worker is waiting for readiness
- **THEN** future sink callbacks and execute delivery are suppressed, startup settles as disposed, stdin remains owned until exit and drain finality, and the single cancellation deadline may escalate against the exact live process without sending an unaccepted cancel command

#### Scenario: An admitted sink callback is active when disposal begins
- **WHEN** disposal races with a callback that already crossed admission
- **THEN** that callback is allowed to settle, later callbacks are suppressed, and resource disposal waits for its owning stream pump and process finality
