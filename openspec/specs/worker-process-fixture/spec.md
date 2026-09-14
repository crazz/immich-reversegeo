# Worker Process Fixture

## Purpose

Defines a hermetic real-process fixture whose deterministic v1 worker-stream scenarios make launcher and later lifecycle behavior verifiable without production processing dependencies.

## Requirements

### Requirement: Hermetic test executable availability
The test suite SHALL provide a dedicated fixture executable that is built and staged with the launcher tests for normal build, test, and test-publish outputs on supported Windows, Linux, and macOS hosts. Fixture startup SHALL NOT enter the production worker role, construct the production dependency-injection graph, access PostgreSQL, or load geodata.

#### Scenario: Fixture is available after a build
- **WHEN** launcher tests run from a supported build or staged publish output without rebuilding the fixture on demand
- **THEN** they resolve an absolute fixture executable and working directory from test build metadata rather than searching the current directory or PATH

#### Scenario: Fixture remains test-only
- **WHEN** the production application is built or published without the test project
- **THEN** no fixture scenario selector or fixture executable is added to the production worker invocation or production output

### Requirement: Fixture uses finalized v1 streams
The fixture SHALL use the shared finalized v1 controller-input and worker-output contracts, canonical encoding, codec, sequence validation, run correlation, frame limit, and exit-code constants wherever the selected scenario is valid. It SHALL NOT copy a second protocol model or call production worker services.

#### Scenario: Valid stream reuse
- **WHEN** a valid fixture scenario emits ready, run events, or a terminal event and accepts execute or cancel input
- **THEN** the bytes conform to the same v1 contract consumed by the launcher and the accepted run identifier is the execute request identifier

#### Scenario: Deliberately invalid bytes
- **WHEN** a fault scenario must violate framing, compatibility, sequence, or terminal consistency
- **THEN** only that declared fault is produced intentionally and the fixture does not replace the launcher's production codec or validator

### Requirement: Shell-free launcher invocation
Launcher tests SHALL invoke the real fixture through block-25 process mechanics using a test-created general `ChildProcessStartDescriptor`, redirected standard input/output/error, discrete argument tokens, and an absolute working directory. The fixture descriptor is not a block-24 `WorkerCommandInvocation` and has no production-worker selector requirement. Fixture scenario selection SHALL be test-only and SHALL NOT relax or alter the production command builder's exact `--internal-worker` invocation.

#### Scenario: Scenario selection reaches only the fixture
- **WHEN** a test selects a scenario and unique test resources
- **THEN** those values are passed as discrete fixture arguments in a test-created descriptor and production worker command resolution is not called or modified

### Requirement: Deterministic normal scenarios
The fixture SHALL provide stable ready, success, no-work, and request-capture scenarios. Successful scenarios SHALL emit ready first, await and decode exactly one execute request, record its exact received frame when capture is requested, and only then emit the configured valid ordered run events and terminal event.

#### Scenario: Successful processing stream
- **WHEN** the success scenario receives a valid execute request
- **THEN** it emits a valid nonempty lifecycle/progress/terminal sequence and exits with the completed exit code

#### Scenario: No-work processing stream
- **WHEN** the no-work scenario receives a valid execute request
- **THEN** it emits run-started, zero eligibility, a completed terminal with zero counts, and exits with the completed exit code

#### Scenario: Exact request capture
- **WHEN** request capture is enabled and the fixture accepts execute
- **THEN** it atomically records the exact received frame in that test's isolated capture location before emitting the configured post-request handshake event

### Requirement: Deterministic protocol and crash scenarios
The fixture SHALL provide separately selectable pre-ready exit, post-ready exit without terminal, malformed frame, oversized frame, unknown protocol message, invalid sequence, and terminal/exit mismatch scenarios. Each scenario SHALL expose a positive protocol or process handshake before any test action that depends on reaching that state, except a pre-ready exit whose observed exit is the handshake.

#### Scenario: Pre-ready crash
- **WHEN** the pre-ready crash scenario starts
- **THEN** it emits no ready event, writes its configured bounded diagnostic if any, and exits with the configured code

#### Scenario: Post-ready crash
- **WHEN** the post-ready crash scenario accepts execute
- **THEN** it exposes the post-request handshake and exits with the configured code without emitting a terminal event

#### Scenario: Malformed or oversized output
- **WHEN** malformed or oversized output is selected
- **THEN** the fixture writes the exact configured invalid bytes or a frame exceeding the v1 byte limit and then follows the scenario's declared exit behavior

#### Scenario: Unknown or invalid sequence output
- **WHEN** an unknown-message or invalid-sequence scenario is selected
- **THEN** the fixture emits a valid ready handshake followed by the isolated compatibility or ordering violation

#### Scenario: Terminal and exit disagree
- **WHEN** a terminal-mismatch scenario is selected
- **THEN** the fixture emits a valid terminal, preserves it as the last stdout frame, and exits with the separately configured contradictory raw code

### Requirement: Standard error pressure and raw exits
The fixture SHALL be able to write more than the launcher's retained standard-error capacity while continuing stdout protocol progress, and SHALL support each mapped v1 exit code plus at least one unmapped in-range process exit code. Data volume and exit choice SHALL be deterministic from immutable scenario inputs.

#### Scenario: Standard error flood
- **WHEN** the stderr-flood scenario runs beside a valid success stream
- **THEN** it writes a known prefix, deterministic body larger than 65,536 bytes, and known suffix without waiting for stderr consumption before protocol completion

#### Scenario: Exit-code matrix
- **WHEN** a raw-exit scenario selects completed, invalid-input, busy, execution-failed, host-failed, output-failed, cancelled, or an unmapped code
- **THEN** the operating system reports exactly that selected code and the fixture does not classify its meaning for the launcher

### Requirement: Reusable cancellation behaviors
The fixture SHALL provide cooperative-cancel and unresponsive scenarios for reuse by block 28. The cooperative scenario SHALL accept execute, expose an armed handshake, await a valid correlated cancel frame, emit a valid cancelled terminal, and exit 130. The unresponsive scenario SHALL expose an armed handshake and then ignore cancel and stdin closure until externally terminated.

#### Scenario: Cooperative cancellation
- **WHEN** an armed cooperative fixture receives a valid correlated cancel
- **THEN** it emits exactly one valid cancelled terminal and exits 130 without a timing delay

#### Scenario: Unresponsive worker
- **WHEN** an armed unresponsive fixture receives cancel or its stdin is closed
- **THEN** it remains alive and produces no terminal until the test or a later production policy terminates it

### Requirement: Parallel isolation and orphan-free cleanup
Every fixture run SHALL use a unique test-owned resource root and SHALL avoid fixed ports, mutable process-wide scenario state, shared capture files, and timing sleeps. Tests SHALL register each started fixture process for unconditional cleanup that closes input, terminates the fixture process tree when it has not exited, drains launcher completion, and disposes the session with bounded watchdogs used only to fail or clean up, never to coordinate expected behavior.

#### Scenario: Concurrent fixture runs
- **WHEN** fixture tests execute concurrently with different run identifiers and resource roots
- **THEN** their arguments, captures, streams, handshakes, diagnostics, and cleanup do not interfere

#### Scenario: Test fails after fixture start
- **WHEN** a test aborts or an assertion fails while its fixture is still running
- **THEN** test cleanup terminates that registered fixture process tree and waits for process and stream finality so no fixture process is left behind

### Requirement: Block 26 verification boundaries
Block 26 tests SHALL assert executable availability, real block 25 process adaptation, handshake/request delivery, accepted-event ordering, stream drainage, raw terminal/exit/stderr observations, and cleanup. They SHALL NOT assert block 24 resolver policy already owned by block 24, cancellation/grace/escalation policy owned by block 28, crash/protocol/terminal-exit classification or UI projection owned by block 30, or PostgreSQL cross-process exclusion owned by block 32.

#### Scenario: Raw evidence without future policy
- **WHEN** a cancellation, crash, invalid protocol, mismatch, or arbitrary exit fixture scenario is exercised in block 26
- **THEN** the test asserts only block 25 raw observations and fixture cleanup, leaving later policy outcomes to their owning blocks
