## Purpose

Defines the stable process-exit taxonomy and lifecycle contract used to distinguish orderly worker outcomes from platform-specific abrupt termination while preserving terminal protocol authority.

## ADDED Requirements

### Requirement: Orderly worker outcomes use a closed portable exit taxonomy
The worker SHALL return 0 for completed work including no eligible work, 2 for invalid invocation/request/controller-input protocol, 3 exclusively for global advisory-lock contention, 4 for executor/domain failure including a caught execution-path out-of-memory failure, 5 for startup/configuration/dependency/host-lifecycle failure, 6 for worker-output protocol generation or stdout transport failure including broken pipe, and 130 for cooperative cancellation or host shutdown. Configuration or dependency failure SHALL NOT be classified as invalid invocation, and unrelated failures SHALL NOT use code 3. The mapped values SHALL remain nonnegative and no greater than 255.

#### Scenario: No eligible work completes
- **WHEN** an accepted worker request completes with no eligible assets
- **THEN** the process returns exit code 0

#### Scenario: Invalid request is rejected
- **WHEN** stdin supplies an empty, malformed, oversized, incompatible, or semantically invalid initial request
- **THEN** the process returns exit code 2 and starts no run

#### Scenario: Advisory lock is busy
- **WHEN** block 31's non-blocking global advisory-lock acquisition reports contention
- **THEN** the process returns exit code 3 and no unrelated condition is mapped to 3

#### Scenario: Executor reports domain failure
- **WHEN** an accepted executor returns Failed or a caught execution-path out-of-memory failure reaches the managed outcome boundary
- **THEN** the process returns exit code 4 unless a higher-precedence later failure occurs

#### Scenario: Required dependency cannot initialize
- **WHEN** syntactically valid worker startup cannot load configuration or initialize a required dependency
- **THEN** the process returns exit code 5 rather than 2 or 4

#### Scenario: Protocol output pipe breaks
- **WHEN** readiness, an event, or the terminal frame fails block-21 mapping/lifecycle validation/serialization or cannot be written, flushed, or disposed reliably on stdout
- **THEN** the process returns exit code 6

#### Scenario: Cooperative shutdown is observed
- **WHEN** the Generic Host stopping token requests shutdown before or during a request and no higher-precedence failure occurs
- **THEN** the process returns exit code 130

### Requirement: Abrupt termination remains an unmapped platform observation
The worker SHALL assign mapped exit codes only through orderly managed completion. Forced process-tree termination, operating-system kill, fail-fast, stack overflow, unhandled exception, and out-of-memory termination that cannot reach the managed boundary SHALL NOT be normalized to a taxonomy code. This taxonomy SHALL make no promise that an abrupt raw platform status differs from every mapped value. A no-terminal mapped status SHALL be only one process-classification observation, not proof of managed completion or a domain result.

#### Scenario: Process is forcibly terminated
- **WHEN** the worker is killed before its managed completion boundary runs
- **THEN** no worker-selected taxonomy code is asserted and the raw platform observation remains outside this mapper

#### Scenario: Out-of-memory failure is unhandled
- **WHEN** an out-of-memory condition prevents managed outcome mapping or terminal completion
- **THEN** the termination remains unmapped rather than being promised as exit code 4

### Requirement: Mapped outcomes have deterministic precedence
When multiple orderly conditions are observed before process completion, the worker SHALL select the highest-precedence outcome in this order: output transport failure (6), host infrastructure/startup/configuration/dependency/cleanup failure (5), invalid invocation/request/controller-input protocol (2), busy contention (3), executor/domain failure (4), cancellation or host shutdown (130), then completion (0). Failure to write stderr diagnostics SHALL NOT change the selected outcome. Abrupt unmapped termination SHALL bypass this precedence rather than enter it.

#### Scenario: Terminal flush fails after executor completion
- **WHEN** the executor completes but flushing its terminal frame fails
- **THEN** output transport failure takes precedence and the process returns 6

#### Scenario: Host disposal fails after a terminal was flushed
- **WHEN** a valid terminal frame was flushed and later non-output host disposal fails
- **THEN** the process returns 5 while retaining the terminal event as the run authority

#### Scenario: Shutdown races with committed failure
- **WHEN** cooperative shutdown is observed after a higher-precedence mapped failure has been committed
- **THEN** the higher-precedence outcome remains selected

### Requirement: Terminal protocol events remain authoritative for accepted runs
A successfully flushed terminal protocol event SHALL remain authoritative for the accepted run's UI/domain state. In the absence of a higher-precedence process condition, completed SHALL correspond to exit 0, cancelled to exit 130, and failed to exit 3, 4, or 5 according to the causal class. A post-acceptance invalid input/protocol outcome SHALL NOT cancel, mutate, suppress, or replace the executor/reporter terminal; it SHALL select exit 2 and MAY coexist with completed, cancelled, or failed domain state. Exit 6 SHALL mean terminal delivery is absent, partial, or uncertain unless a terminal had already flushed before a later output-disposal failure. A terminal/exit mismatch caused by independent input finality or a later lifecycle fault SHALL preserve the valid terminal result and expose the independent process classification for downstream handling.

#### Scenario: Accepted run completes normally
- **WHEN** an accepted run flushes a completed terminal and cleanup succeeds
- **THEN** the terminal is authoritative and the process returns 0

#### Scenario: Accepted run is cancelled
- **WHEN** an accepted run cooperatively cancels, flushes a cancelled terminal, and cleanup succeeds
- **THEN** the terminal is authoritative and the process returns 130

#### Scenario: Accepted run is busy before domain work
- **WHEN** a future block-31 typed lock gate reports contention as the first executor step after run-started but before eligibility, snapshots, mutation, or heavy geodata work
- **THEN** the exactly-once executor/reporter path emits its existing failed terminal with safe busy detail and the process returns 3 without adding a busy terminal type

#### Scenario: Invalid control follows an accepted request
- **WHEN** a post-acceptance input frame is malformed, incompatible, out of sequence, wrongly correlated, or a duplicate execute
- **THEN** the normal executor/reporter terminal attempt remains authoritative, the process returns 2, and no replacement terminal or implicit cancellation is created

#### Scenario: Valid terminal precedes later process fault
- **WHEN** a valid terminal frame was flushed before a disposal or output-finalization fault
- **THEN** the terminal remains authoritative and the nonzero exit remains available as an independent process-integrity classification

### Requirement: Pre-request outcomes create no run terminal
Invalid private invocation before host construction, startup/configuration/dependency failure before readiness, readiness transport failure, EOF or invalid input before request acceptance, and host shutdown before acceptance SHALL create no processing run and SHALL emit no terminal event. A healthy worker SHALL flush ready before waiting for the first request. Clean EOF before an execute request SHALL be classified as invalid request code 2; an unexpected stdin I/O failure SHALL be classified as host infrastructure code 5; inability to flush ready SHALL be classified as output transport code 6; and pre-request host shutdown SHALL be classified as 130. Expected input-read cancellation, disposal exception, or equivalent teardown caused by terminal finalization, host stop, or lease disposal SHALL be neutral and SHALL NOT select code 5 or replace an earlier input outcome.

#### Scenario: Clean EOF follows readiness
- **WHEN** ready was flushed and stdin closes before a complete execute request
- **THEN** the worker emits no terminal and returns 2

#### Scenario: Startup fails before readiness
- **WHEN** required configuration or dependency initialization fails before ready can be published
- **THEN** the worker emits neither ready nor terminal and returns 5

#### Scenario: Readiness cannot be flushed
- **WHEN** stdout fails while publishing or flushing ready
- **THEN** no valid readiness or terminal is claimed and the process returns 6 without retrying the write

#### Scenario: Host stops before request acceptance
- **WHEN** host shutdown is requested before an execute request is accepted
- **THEN** no terminal is fabricated and the process returns 130

#### Scenario: Structured shutdown unblocks stdin
- **WHEN** terminal finalization, host stop, or lease disposal cancels or disposes a pending stdin read
- **THEN** the expected teardown is neutral and does not select 5 or replace a previously recorded input outcome

#### Scenario: Shutdown follows a flushed terminal
- **WHEN** host stopping is first observed after a terminal frame was successfully flushed
- **THEN** shutdown does not retroactively select 130 or change the terminal result

### Requirement: Process result is assigned after output and lifecycle finalization
The worker SHALL determine typed finality facts during execution but SHALL assign the actual process result only after the existing owners complete or fail: block 21 terminal drain/flush/emitter finality and block 20 request-lease settlement, asynchronous scope disposal, application stop, and host/provider disposal. Those owners SHALL hand typed facts to one outcome accumulator and SHALL NOT be duplicated by a second cleanup path. The block-20 finality adapter SHALL catch a recognized block-21 mapping/lifecycle-validation/serialization/write/flush/broken-state result and return a discriminated output-transport fact without throwing; that typed return SHALL select 6. If the generic block-20 terminal/finality hook throws or permits an exception to escape instead of returning that discriminated fact, it SHALL retain block-20 host-infrastructure classification 5. Services SHALL NOT call process-terminating APIs or assign the global process exit code. The top-level worker role SHALL apply late-failure precedence and return the final integer only after owned cleanup completes.

#### Scenario: Successful terminal is flushed before return
- **WHEN** an accepted run reaches an orderly terminal
- **THEN** the worker awaits terminal flush and required disposal before the entry point returns its final integer

#### Scenario: Cleanup changes the final classification
- **WHEN** a lower-precedence primary outcome is followed by a higher-precedence cleanup failure
- **THEN** the final code reflects the cleanup failure and is assigned only after that failure is observed

### Requirement: Nonzero orderly exits provide safe stderr diagnostics
For each orderly nonzero exit, the worker SHALL make one best-effort final exit-summary write containing a stable outcome token, lifecycle phase, and bounded predefined safe message. That marked final summary SHALL be distinct from any earlier ordinary logs or optional safe block-21/block-22 diagnostics and SHALL use an injected top-level stderr writer that remains alive until after host/provider disposal. The diagnostic SHALL NOT echo raw arguments, stdin bytes, protocol payloads, configuration values, credentials, connection strings, SQL, exception messages, stack traces, or stdout protocol frames. Stdout SHALL remain protocol-only. Stderr failure SHALL neither be retried nor alter the exit code.

#### Scenario: Invalid request diagnostic is emitted
- **WHEN** an invalid initial request selects exit code 2
- **THEN** stderr identifies the invalid-request outcome and input phase without reproducing the request

#### Scenario: Broken stdout is diagnosed
- **WHEN** a broken pipe selects exit code 6
- **THEN** stderr receives a bounded safe output-transport diagnostic and stdout is not retried

### Requirement: Exit classifications do not imply automatic retry
No mapped or unmapped outcome SHALL by itself authorize automatic retry. In particular, busy code 3 and output transport code 6 SHALL classify the completed attempt only. Any future retry behavior SHALL require a separate policy and idempotency contract.

#### Scenario: Busy attempt exits
- **WHEN** a worker returns exit code 3
- **THEN** the code classifies only that attempt and conveys no authorization to start another attempt

#### Scenario: Transport attempt exits
- **WHEN** a worker returns exit code 6 or terminates abruptly outside the mapper
- **THEN** the outcome conveys no claim that rerunning the request is safe
