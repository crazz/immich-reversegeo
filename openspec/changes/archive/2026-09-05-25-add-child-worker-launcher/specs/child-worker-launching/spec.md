## Purpose

Defines reliable Web-side startup, protocol handshaking, stream drainage, observation, and ownership for one child worker while preserving later policy boundaries.

## ADDED Requirements

### Requirement: Launch returns typed process ownership
The launcher SHALL consume one validated block-24 `WorkerCommandInvocation` and one immutable processing request for production worker launches. Its process factory SHALL consume the general shell-free `ChildProcessStartDescriptor`, allowing test support to start a fixture through the same mechanics without treating fixture arguments as a production worker invocation. The launcher SHALL return either a typed start failure with no session or a session that exclusively owns the started process and its redirected standard streams. A started session SHALL expose the operating-system process ID and the request's exact run ID; “job ID” SHALL be only an alias for that run ID. The platform process object SHALL NOT be exposed to callers.

#### Scenario: Operating-system start succeeds
- **WHEN** the command descriptor starts a child process successfully
- **THEN** the launch result contains one owned session with the child PID and the exact request run ID

#### Scenario: Operating-system start fails
- **WHEN** process creation rejects the descriptor or throws before ownership is established
- **THEN** the launch result reports a typed safe start failure, contains no session, and does not start stream pumps or write an execute request

#### Scenario: Launch cancellation races with successful start
- **WHEN** caller cancellation is requested after the process factory has returned a started child but before the launch call returns
- **THEN** the launch call still returns the started owned session rather than abandoning the process without an owner

### Requirement: Redirected streams are drained without deadlock
Immediately after a successful process start, the session SHALL begin independent asynchronous byte pumps for stdout and stderr and SHALL begin observing process exit. It SHALL NOT wait for readiness, request writing, terminal delivery, or process exit before starting either pump. Session completion SHALL wait for process exit and finality of both pumps, and output volume on one redirected stream SHALL NOT prevent consumption of the other.

#### Scenario: Worker fills stderr while emitting stdout
- **WHEN** a worker emits protocol frames on stdout while producing more stderr than a pipe buffer can hold
- **THEN** stdout parsing, stderr drainage, request handshaking, and exit observation continue without either pipe blocking the other

#### Scenario: Process exits before stream EOF is observed
- **WHEN** the process exit signal is observed while redirected bytes remain readable
- **THEN** session completion waits until both stdout and stderr reach finality and retains those trailing observations

### Requirement: Readiness gates execute delivery
The session SHALL incrementally frame stdout using the Phase 3 byte limit and SHALL pass complete frames through the shared codec and stream validator. The first accepted event SHALL be `lifecycle/ready` with sequence 1. A configurable internal readiness deadline, driven by an injectable `TimeProvider` and defaulting to exactly 30 seconds, SHALL produce a typed timeout observation if valid readiness is not accepted in time. Only after valid readiness SHALL the session serialize exactly one controller-to-worker execute request, write it completely, flush it, and report startup accepted. The session SHALL keep stdin open after that flush for block-28 control commands. Start, readiness, framing, validation, request-write, and request-flush failures SHALL remain distinct observations.

#### Scenario: Ready is accepted before execute
- **WHEN** the first complete stdout frame is a valid ready event before the deadline and its sink callback succeeds
- **THEN** the session writes and flushes one execute request, reports startup accepted, and leaves stdin open

#### Scenario: Ready sink callback fails
- **WHEN** ready passes protocol validation but its accepted-event sink callback fails before execute is written
- **THEN** startup reports sink failure, writes no execute request, suppresses later callbacks, and continues draining both output streams

#### Scenario: Ready does not arrive in time
- **WHEN** no valid ready event is accepted before the readiness deadline
- **THEN** startup reports ready timeout and no execute bytes are written

#### Scenario: Pre-ready output is invalid
- **WHEN** the first complete stdout frame is malformed, incompatible, oversized, out of sequence, or not ready
- **THEN** startup reports the exact raw protocol observation, writes no execute request, and continues draining both streams to avoid deadlock

#### Scenario: Execute flush fails
- **WHEN** ready was accepted but the execute frame cannot be fully written and flushed
- **THEN** startup reports request transport failure and does not claim that the worker accepted the request

### Requirement: Accepted protocol events are delivered in order
Every stdout frame accepted by the shared codec and stateful stream validator SHALL be offered exactly once to a caller-provided asynchronous accepted-event sink in stream order. Invalid frames SHALL NOT be delivered. The first protocol failure SHALL stop further sink callbacks while raw stdout drainage continues. Sink failure SHALL be recorded as a raw session observation, SHALL stop further sink callbacks, and SHALL NOT stop stdout/stderr drainage or process-exit observation. The launcher SHALL NOT project events into `ProcessingState` or assign UI/domain meaning.

#### Scenario: Worker emits a normal run lifecycle
- **WHEN** ready, run events, and one terminal frame pass shared validation
- **THEN** the sink receives each accepted event once in sequence order and completion retains the accepted terminal event

#### Scenario: Event sink rejects an accepted event
- **WHEN** the caller sink fails while handling an accepted event
- **THEN** the first sink failure is retained, no later sink callback occurs, and both redirected streams continue draining through process completion

### Requirement: Session completion preserves bounded raw observations
The session completion result SHALL preserve the raw process exit code when available, whether process exit was observed, the accepted terminal event when one exists, startup finality, stdout finality, the first protocol or sink observation, and a bounded stderr tail. Stderr SHALL be drained without limit but only its final 65,536 bytes SHALL be retained, together with whether earlier bytes were truncated; decoding a diagnostic snapshot SHALL use replacement for incomplete or invalid trailing UTF-8 rather than failing the drain. These observations SHALL NOT classify a crash, infer a missing-terminal cause, override a terminal event, authorize retry, or bridge into processing state.

#### Scenario: Worker exits normally after terminal
- **WHEN** a valid terminal event is accepted and the process exits with code 0 after both streams close
- **THEN** completion contains that terminal, raw exit code 0, clean stream finality, and the bounded stderr tail without adding a crash classification

#### Scenario: Stderr exceeds retention capacity
- **WHEN** the worker writes more than 65,536 stderr bytes
- **THEN** all stderr is drained, only the final 65,536 bytes are retained, and completion marks the tail as truncated

### Requirement: Waiting cancellation is separate from worker cancellation
Cancellation supplied by a caller waiting for startup or completion SHALL cancel only that wait and SHALL NOT send a protocol cancel command, close a live accepted session, kill the process, or cancel the internal stream pumps. Once launch returns a started session, the caller SHALL retain responsibility for disposing that session even if one of its waits is cancelled. Block 28 SHALL own cooperative cancel, grace-period, and forced-termination policy.

#### Scenario: Caller stops waiting after process start
- **WHEN** caller cancellation occurs while the owned session is waiting for ready, request completion, or exit
- **THEN** the caller wait is cancelled while session ownership, pumps, and observable lifecycle remain active

### Requirement: Session disposal is single-owner and non-escalating
The session SHALL implement idempotent asynchronous disposal. Disposal SHALL close controller stdin, suppress future sink callbacks, await the already-running exit and stream-finality observation, and dispose redirected streams and the process adapter exactly once. It SHALL NOT send a cancel command or forcibly terminate a process; therefore disposal MAY remain incomplete until the worker exits, pending block 28's bounded graceful/forced policy. Completion and disposal SHALL share one lifecycle rather than race duplicate waits or readers.

#### Scenario: Completed session is disposed repeatedly
- **WHEN** asynchronous disposal is invoked more than once after process completion
- **THEN** resources are released once and every caller observes the same settled lifecycle

#### Scenario: Live session is disposed before request acceptance
- **WHEN** disposal begins while the worker is waiting for input
- **THEN** controller stdin closes, pumps remain active until process/stream finality, and no cancel command or forced termination is attempted
