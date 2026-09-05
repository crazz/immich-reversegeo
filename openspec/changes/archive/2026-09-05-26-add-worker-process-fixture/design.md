## Context

See proposal.md for motivation and `specs/worker-process-fixture/spec.md` for observable behavior. Blocks 15, 17, and 21–23 finalize the v1 envelope, codecs, validators, one-MiB stdout frame limit, one-shot execute plus repeated cancel input, terminal authority, and exit codes. Block 24 produces only the production application's exact `--internal-worker` descriptor and deliberately rejects MSTest-host discovery. Block 25 consumes an immutable descriptor, owns a real process and all redirected streams, sends execute after accepted ready, retains a 65,536-byte stderr tail, and exposes raw startup/completion observations without cancellation or crash policy.

A fixture guarded inside the production executable would make fixture modes reachable from production and could accidentally enter the Web/worker composition root. A fixture hidden inside the MSTest executable is also incompatible with block 24's explicit test-host rejection and creates unstable runner invocation.

## Goals / Non-Goals

**Goals:**

- Exercise the production block 25 process adapter, pumps, handshake, request write, observations, and disposal at an actual OS boundary.
- Make each expected state visible through protocol, process-exit, or atomic capture handshakes; deadlines are watchdogs only.
- Produce reusable fixture modes for blocks 28, 30, and 32 while keeping each later block's policy assertions separate.
- Ensure fixture binaries are predictably staged and fixture processes are isolated and reaped on every test outcome.

**Non-Goals:**

- Re-test block 24 runtime identity/resolution or loosen its production argument contract.
- Instantiate the production worker host, executor, dependency injection, database, geodata, or UI state.
- Add production cancel/grace/kill behavior, crash classification, terminal/exit reconciliation, retry, or PostgreSQL locking.
- Treat fixture output as a new protocol extension or add fixture-only production branches.

## Decisions

### 1. Use a dedicated executable test project

Add `tests/ImmichReverseGeo.WorkerProcessFixture/ImmichReverseGeo.WorkerProcessFixture.csproj` as a small `net10.0` console executable with an apphost. Its entry point performs strict fixture-only argument parsing and then runs one scenario; it never references production `Program` or a composition root.

The executable references only the project that owns the finalized protocol contracts/codecs/validators and uses those APIs for valid frames and controller input. It does not duplicate DTOs or wire constants. If protocol APIs are internal, grant narrowly scoped test-fixture visibility rather than copying them. A project reference does not authorize resolving any worker service; the fixture entry point constructs only codec/stream primitives.

**Alternatives considered:** A production guarded mode was rejected because it enlarges production invocation and DI risk. A test-assembly mode was rejected because Microsoft.Testing.Platform owns that process and block 24 forbids treating it as the application. A script was rejected because JSON, UTF-8, exit, and process behavior would vary by installed shell/runtime.

### 2. Stage a self-identifying apphost beside test output

Add the fixture to the solution and a build-only project reference from `ImmichReverseGeo.Tests`. An explicit MSBuild staging target copies the apphost and all runtime/dependency files into a fixed `worker-process-fixture/` subdirectory for both Build and Publish. Build/publish fails if the expected apphost is absent. The locator computes the exact OS-specific filename from `AppContext.BaseDirectory`; it does not scan directories or PATH, and it validates an absolute existing file before constructing a descriptor.

This makes `dotnet build` followed by `dotnet test --no-build` reliable and avoids depending on `Environment.ProcessPath`, `DOTNET_HOST_PATH`, current directory, or the MSTest host layout. Production builds that do not include the test project neither build nor publish the fixture.

**Alternative considered:** Launching `dotnet fixture.dll` was rejected because locating the exact host from an MTP process adds a second runtime-discovery problem and can accidentally reuse the test host.

### 3. Invoke block-25 process mechanics with a test-created general descriptor

Test support creates a general immutable `ChildProcessStartDescriptor` consumed by the process factory: exact apphost path, fixed fixture working directory, redirected streams, `UseShellExecute=false`, `CreateNoWindow=true`, and discrete unquoted fixture arguments. The test does not call or change the block 24 production resolver, whose only legal result remains the same-application `--internal-worker` command.

Scenario selection is by strict immutable arguments such as `--scenario <token>`, `--resource-root <absolute-path>`, and narrowly typed optional byte-count/exit-code values. No process-wide environment mutation is used, so parallel tests cannot race. Arguments contain only generated non-secret test paths and values.

**Alternative considered:** Encoding a scenario in inherited environment was rejected because parallel tests would mutate shared state and block 24 requires unchanged full-environment inheritance for production commands.

### 4. Use a closed scenario catalog with shared valid primitives

The fixture exposes a closed enum-like catalog:

| Scenario | Deterministic behavior | Block 26 assertion boundary |
|---|---|---|
| `ready` | Emit ready, accept/capture execute, then complete as a minimal valid no-work run | Startup and execute handshake |
| `success` | Ready; accept execute; run-started, eligibility, progress/activity/log as configured; completed terminal; exit 0 | Ordered accepted callbacks, terminal, raw exit |
| `no-work` | Ready; accept execute; run-started, eligibility 0, completed zero-count terminal; exit 0 | Canonical no-work stream |
| `pre-ready-crash` | Optional bounded stderr marker, no stdout, configured immediate exit | Raw startup/exit evidence only |
| `post-ready-crash` | Ready; accept/capture execute; emit an armed event; exit without terminal | Raw missing-terminal evidence only |
| `malformed` | Ready, then exact invalid UTF-8/JSON/framing bytes | First protocol observation and continued drainage |
| `oversize` | Ready, then deterministic frame larger than 1,048,576 bytes excluding LF | Oversize observation and drainage |
| `unknown` | Ready, then one canonically encoded envelope with an unknown version/category/type choice | Compatibility observation only |
| `invalid-sequence` | Ready sequence 1, then a valid known event with a gap/replay selected by subcase | Validator observation only |
| `terminal-mismatch` | Valid final terminal, then a contradictory selected process exit | Preserve both raw facts; no classification |
| `stderr-flood` | Valid success stream while synchronously writing known stderr prefix/body/suffix beyond 65,536 bytes | No pipe deadlock and exact bounded tail metadata |
| `raw-exit` | Pre- or post-ready exit using 0, 2, 3, 4, 5, 6, 130, or a fixed unmapped code | Exact OS exit capture only |
| `cooperative-cancel` | Ready; execute; armed log; accept correlated cancel; cancelled terminal; exit 130 | Fixture conformance only; block 28 owns launcher cancel policy |
| `unresponsive` | Ready; execute; armed log; optionally observe cancel with a valid log, but never terminal/exit until killed | Fixture conformance and cleanup only; block 28 owns escalation |

Valid output is created with the shared mapper/codec. Invalid modes create the smallest raw mutation after a valid ready, making the intended fault unambiguous. Terminal mismatch never emits bytes after the terminal; only the OS code disagrees. Raw-exit mode uses `Environment.Exit`/return rather than fail-fast so tests remain portable and do not create crash dumps.

### 5. Make request capture atomic and handshake-driven

The fixture incrementally reads stdin with the shared bounded controller codec/validator. When capture is requested, it writes the exact frame bytes to a temporary file under the unique resource root, flushes/closes it, and atomically renames it to the declared capture filename. Only afterward does it emit the next accepted run event. Awaiting that event therefore proves capture availability without file polling.

For cancellation modes, a normal safe log event with a generated fixture marker signals `armed`; cooperative mode then waits for a valid correlated cancel. Unresponsive mode can emit a second marker after observing cancel but deliberately does not terminate. Protocol events, capture-file publication, and process exit are the positive handshakes. Finite test deadlines detect hangs and trigger cleanup but never decide when expected work should proceed.

### 6. Separate fixture conformance from launcher policy tests

A minimal direct-process conformance harness verifies strict argument rejection, execute capture, cooperative cancel, unresponsive behavior, and exact exit selection when block 25 has no API to send cancel. Block 26's launcher tests use the production launcher and adapter for all scenarios relevant to startup, output, stderr, exit, and cleanup. They assert raw observations only.

Block 25 remains responsible for starting/draining/observing. Block 28 later drives the two cancellation modes through its new session control/escalation API. Block 30 later classifies pre/post-ready crashes, malformed/oversized/unknown/sequence faults, missing terminal, and terminal/exit contradictions and projects outcomes. Block 32 may reuse the staged executable architecture but adds PostgreSQL and process-exclusion behavior in its own fixture extension or executable; block 26 adds no database mode.

### 7. Register every process before awaiting behavior and reap it unconditionally

A test-owned fixture lease wraps each launcher result or direct `Process`. Immediately after start it records the process identifier, unique resource root, and available handle. Cleanup is idempotent and runs from `finally`/MSTest cleanup:

1. close fixture stdin when accessible;
2. if raw exit has not completed, acquire/retain the process handle and call `Kill(entireProcessTree: true)`;
3. await process exit plus launcher stdout/stderr finality;
4. dispose the block 25 session exactly once;
5. delete the unique resource root after handles close.

The launcher session intentionally has no kill API until block 28, so block 26's test-only reaper may use `Process.GetProcessById` immediately from the exposed PID. It confirms the process is still the registered live fixture before killing and treats already-exited/not-found as success. The fixture never spawns descendants, but tree kill protects future fixture evolution. A bounded cleanup watchdog fails the test and reports the PID; an assembly-level last-chance registry attempts to reap any lease left by interrupted per-test cleanup. This is test hygiene, not production termination policy.

### 8. Isolate parallel tests completely

Each case receives a cryptographically uninteresting GUID-named directory beneath the test temp root, unique capture names, run ID, marker tokens, and process. No fixed port, global environment variable, static scenario switch, shared file, or shared fixture process is used. Parameterized cases may run in parallel. Assertions consume only that lease's sink, files, PID, and observations.

## Risks / Trade-offs

- **[MSBuild output staging differs across build and publish]** → Add explicit Build and Publish staging targets plus tests that resolve and execute the staged apphost; fail early with the expected path.
- **[A protocol API is not referenceable from the fixture project]** → Use narrow test visibility or move only the already-finalized protocol primitive to its intended shared owner; do not duplicate contracts or reference a composition root.
- **[OS exit-code reporting differs for externally killed processes]** → Assert exact values only for fixture-selected managed exits; forced-kill tests assert termination/finality, not a portable numeric kill code.
- **[Negative behavior needs a deadline]** → Require an armed/observed handshake first; use the deadline only to establish that no terminal/exit occurred and to trigger cleanup, never as a delay before action.
- **[PID reuse could target an unrelated process during cleanup]** → Retain a process handle as early as possible, validate the registered fixture identity/state, and make kill tolerant of already-exited processes.
- **[Stderr flood can make test logs large]** → Generate bytes algorithmically in the child and assert only total count, truncation flag, and known retained suffix.
- **[Fixture capabilities can drift into production semantics]** → Keep the project under tests, use a closed fixture CLI, and prohibit production project references to the fixture.

## Migration Plan

1. Add the fixture project and solution/build/publish staging without changing production outputs.
2. Add shared-protocol-only fixture entry point and scenario catalog.
3. Add isolated locator, descriptor factory, handshake/capture support, and process lease/reaper.
4. Add fixture conformance tests, then real launcher scenario tests.
5. Verify focused tests, normal default-exclusion tests, build-then-test-without-build, test publish/staging, and strict OpenSpec validation.

Rollback removes the test project, test build references/staging, fixture helpers, and fixture tests; production binaries and runtime behavior are unaffected.
