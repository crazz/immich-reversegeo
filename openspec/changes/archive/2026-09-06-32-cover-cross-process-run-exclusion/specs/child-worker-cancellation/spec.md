## MODIFIED Requirements

### Requirement: Cooperative completion preserves terminal and stream finality
Cancellation SHALL NOT fabricate or rewrite a worker terminal event. A valid cancelled terminal and orderly exit SHALL remain cooperative evidence, while any other valid terminal, absent terminal, contradictory raw exit, protocol/sink observation, or stdin failure SHALL remain independent raw evidence for block 30. Immediately after a fully validated exact-correlated terminal is recorded and before sink callback admission, the terminal-input completion action SHALL seal new controller writes and invoke the existing one-time half-close owner after an already admitted canonical write/flush settles. A pre-exit physical close failure SHALL preserve that terminal while recording `TerminalInputCloseFailed` through existing bounded InputTransport containment; post-exit cleanup remains best effort. Successful terminal input completion SHALL not latch Stop, send a cancel, create a grace deadline, kill a process, add an input/control-token cancellation path, or replace terminal/raw-exit authority; its failure joins the existing containment deadline and escalation owner. A terminal event alone MUST NOT release process ownership. The cancellation lifecycle SHALL wait for raw process exit and finality of both stdout and stderr pumps before reporting settled cleanup.

#### Scenario: Cancelled worker exits within grace
- **WHEN** a worker accepts cancel, emits a valid cancelled terminal, exits 130, and closes both output streams before the deadline
- **THEN** the terminal and exit are preserved, no kill is attempted, trailing output is drained, and resources are released once

#### Scenario: Terminal arrives but process remains alive
- **WHEN** any terminal frame is accepted but the worker has not exited by the grace deadline
- **THEN** the terminal remains preserved and the still-live process remains eligible for escalation

#### Scenario: Cancel transport fails but process exits
- **WHEN** cancel write or flush fails and the process subsequently exits before grace
- **THEN** no kill is attempted and both the transport failure and complete raw exit/drain observations remain available

#### Scenario: Normal terminal requires peer EOF
- **WHEN** a fully validated terminal is recorded while the child remains alive waiting for more controller input
- **THEN** the terminal-owned one-time half-close lets the child observe peer EOF while terminal authority, Stop policy, and raw evidence remain unchanged

### Requirement: Stop, completion, and disposal share one resource lifecycle
Stop, natural completion, and asynchronous disposal SHALL converge on the session's existing process-exit and stdout/stderr-drain lifecycle. After a fully validated exact-correlated terminal is recorded, the separately tracked terminal-input completion path SHALL seal writes and half-close stdin exactly once before raw process exit when needed to let the worker observe peer EOF. A physical pre-exit close failure SHALL publish the existing terminal-preventing observation and enter bounded InputTransport containment without self-joining, retry, terminal replacement, or a new evidence owner; post-exit cleanup is best effort. This path remains separate from Stop without creating cancellation intent, a grace deadline, a kill request, or a cancellation classification. After exit and both drains, the owner SHALL close stdin if it was not already terminal-closed and dispose cancellation timers/sources, redirected streams, process adapter, bridge/session resources, and other owned handles exactly once. Concurrent waits or disposal calls SHALL observe the same settled tasks. If escalation fails while the process remains alive, ownership and stopping state SHALL be retained until later exit or a separately owned host policy acts; resources MUST NOT be released as though exit occurred.

#### Scenario: Stop and disposal race after cooperative exit
- **WHEN** multiple callers await Stop and dispose the same session as cooperative completion settles
- **THEN** all observe one completion and every owned resource is disposed exactly once after exit and stream finality

#### Scenario: Kill succeeds with trailing diagnostics
- **WHEN** tree termination is accepted while stdout or stderr still has readable trailing bytes
- **THEN** disposal waits for both drains and preserves the bounded observations before closing handles

#### Scenario: Normal terminal leaves child input open
- **WHEN** a fully validated terminal is recorded but the child remains alive waiting for more controller input
- **THEN** the one terminal-owned half-close lets the child observe peer EOF, while the owner still waits for actual exit and both stream drains before resource release

#### Scenario: Terminal input close fails before process exit
- **WHEN** a fully validated terminal has been recorded, the process is still live, and the physical controller-input close fails
- **THEN** the terminal remains accepted, `TerminalInputCloseFailed` enters existing bounded InputTransport containment, and resource ownership remains until physical finality
