## Context

See [proposal.md](proposal.md) and [specs/processing-work-detector-observability/spec.md](specs/processing-work-detector-observability/spec.md). Change 57 defines one advisory `IProcessingWorkDetector` call at the admitted Standard scheduled predispatch seam, with immutable request/result diagnostics and exceptional cancellation/failure semantics. Change 58 replaces its count-backed adapter with a current-full-eligibility existence query while retaining the exact Dashboard and worker counts. Apply must bind to the final landed change-58 strategy and must not alter the parallel change-58 artifacts or behavior.

The active project uses `ILogger` but has no `Meter`, exporter, or metrics configuration. Change 58's opt-in integration/performance evidence may execute `EXPLAIN (ANALYZE, BUFFERS)`, but normal runtime SQL execution does not expose PostgreSQL rows scanned, buffers, physical reads, or plan/index use. Change 60 owns the redacted production maintainer procedure; normal processing must never run `EXPLAIN`.

## Goals / Non-Goals

**Goals:**

- Produce one terminal, structured, low-cardinality measurement for every detector call and every terminal path.
- Make elapsed time deterministic in tests and safe under concurrent singleton use.
- Correlate runtime duration with the exact bounded strategy literal `postgres-exists-v1` and scheduled trigger context.
- Preserve the exact detector result, exception, cancellation, and dispatch behavior from changes 57–58.

**Non-Goals:**

- No timeout introduction or timeout-duration change, retries, fallback, query/predicate/index change, exact count, worker protocol, schedule, configuration, UI, `ProcessingState`, or user-facing log-ring change.
- No SQL text, query plan, coordinates, asset/request/run IDs, connection strings, credentials, database names, host names, parameter values, exception messages, or stack traces in the terminal measurement.
- No metrics package/exporter, tracing system, runtime `EXPLAIN`, row-scan estimate, buffer count, or documentation procedure; change 60 owns query-plan troubleshooting.

## Decisions

### 1. Instrument the detector boundary with an injected monotonic clock

Add one `InstrumentedProcessingWorkDetector` decorator around the finalized existence-backed detector at the same dependency-light control-plane seam. Capture `TimeProvider.GetTimestamp()` immediately before awaiting the detector and compute elapsed time through `TimeProvider.GetElapsedTime()` at terminal completion. Register `TimeProvider.System` in production and inject a deterministic fake in tests. Do not use wall-clock subtraction or a shared mutable `Stopwatch`.

All invocation state—start timestamp, outcome, and successful result—remains local to the async call. A single `finally`-owned emission path records the terminal event, so concurrent calls cannot overwrite one another and success, cancellation, and failure cannot double-log. The wrapper rethrows the original cancellation or failure unchanged and returns the original result unchanged. Alternative: time only the repository query, rejected because validation/adapter overhead and all exceptional paths would not share one exact-once detector-call contract.

### 2. Use one closed event schema

Use exactly `EventId(5901, "ProcessingWorkDetectorCompleted")` for the one terminal event, with these fields:

- `duration_ms`: elapsed monotonic duration;
- `outcome`: exactly `HasWork`, `NoWork`, `Cancelled`, or `Failed`;
- `strategy`: exactly the bounded literal `postgres-exists-v1`, which versions the finalized PostgreSQL EXISTS query contract without a CLR type name or separate free-form version;
- `trigger`: `Scheduled`;
- `purpose` and `coverage`: the existing bounded logical request values from change 57;
- `fallback_used`: the existing bounded result diagnostic on successful results;
- `database_operation`: exactly the bounded operation-family literal `eligibility-existence-probe`, rather than SQL text; and
- `database_roundtrips`: `1` only for `HasWork`/`NoWork`, because change 58 guarantees one completed existence operation on successful detection. Omit it for cancellation/failure rather than guessing whether command execution began.

Do not add row counts, rows scanned, SQL hashes, arbitrary tags, request identity, raw trigger text, exception data, or connection attributes. Rows scanned and buffer/physical-read evidence are explicitly unavailable from normal runtime telemetry and are not represented as invented zero/null metrics. Change 60 maps `postgres-exists-v1` and `eligibility-existence-probe` to an explicit redacted maintainer query-plan procedure.

### 3. Keep four outcomes and preserve cancellation semantics

A successful `HasWork = true` result maps to `HasWork`; false maps to `NoWork`. Catch terminal exceptions once and inspect the exact caller token supplied to `DetectAsync`: classify the event as `Cancelled` if and only if `cancellationToken.IsCancellationRequested` is true, then rethrow unchanged. If that token is not requested, classify every exception—including `OperationCanceledException`, `TimeoutException`, Npgsql/database command timeout, connection failure, and SQL/schema/decoding failure—as `Failed`, then rethrow unchanged. There is no `TimedOut` outcome or cancellation-cause field.

Block 59 creates no timer, linked token, command-timeout setting, retry, fallback, exception translation, or cancellation policy. The terminal event never attaches the exception object or renders its type/message. Existing separately owned safe failure presentation remains unchanged. Alternative: infer cancellation from exception type or provider timeout shape, rejected because only the caller token authoritatively distinguishes requested cancellation from failure.

### 4. Emit every terminal event and escalate the same event by level

Do not sample: scheduled detector cadence is bounded, and sampling would violate exact-once operational evidence. Emit successful and cancelled calls below the slow threshold at `Information`. Emit `Failed` calls and any call with `duration_ms >= 1000` at `Warning`. The slow path changes the level of the same terminal event; it does not emit an additional warning. The one-second threshold is an internal named constant covered by boundary tests, not a new setting.

Alternative: log normal completions at Debug, rejected because default operators could not distinguish routine no-work from missing telemetry. Alternative: emit an Information completion plus a Warning for slow calls, rejected because it creates two terminal measurements for one invocation.

### 5. Stay log-only until the application has a metrics pipeline

Use structured `ILogger` state and the existing logging providers. Do not add `System.Diagnostics.Metrics`, an exporter, or a bespoke in-memory metric store solely for this detector. The event schema is intentionally suitable for downstream log aggregation. A later project-wide observability change may derive metrics from the same bounded dimensions without changing detector behavior.

### 6. Verify structure, redaction, timing, and concurrency directly

Use a fake `TimeProvider` that advances timestamps deterministically and a capturing structured `ILogger` sink that inspects event ID, level, template, and named state values rather than rendered text alone. Use change-57 detector fakes for work, no-work, matching cancellation, unmatched cancellation, and hostile exceptions whose messages contain synthetic SQL, coordinates, IDs, credentials, and connection strings. Assert those sentinels never appear in template, state, or rendered terminal output.

Concurrency tests gate two detector calls independently with signals, advance per-call timing deterministically, release them out of order, and assert one correctly attributed terminal event per call without sleeps. Focused tests also prove threshold boundary behavior, one successful roundtrip field, omission on cancellation/failure, unchanged result identity/value, and unchanged propagated exception/cancellation.

## Risks / Trade-offs

- [A decorator is accidentally registered recursively or beside the uninstrumented detector] → Register one inner concrete strategy and expose one instrumented `IProcessingWorkDetector`; add DI identity/invocation-count tests.
- [Log fields drift into high-cardinality data] → Keep closed constants/enums, hostile-sentinel redaction tests, and no exception attachment.
- [Two logs are emitted for slow/failing calls] → Centralize emission in one terminal path and escalate that event's level rather than logging a second warning.
- [Success roundtrip count is mistaken for rows scanned or I/O cost] → Name it `database_roundtrips`, emit only the guaranteed successful value, and defer plans/buffers/reads to change 60.
- [Concurrent fake time produces ambiguous attribution] → Keep timestamps invocation-local and use gated signals/per-invocation scripted fake timestamps rather than sleeps.
- [The applied change-58 symbols differ from its finalized plan] → Bind to the landed symbols while preserving exact `postgres-exists-v1` telemetry semantics; adapt only instrumentation composition, not the predicate or lifecycle.

## Migration Plan

1. Verify changes 57 and 58 are applied and identify the single Standard scheduled detector registration; use the already-finalized bounded literals `postgres-exists-v1` and `eligibility-existence-probe`.
2. Add the injected monotonic timing/logging observer and one closed terminal event schema around that detector without changing its request/result contract.
3. Register exactly one instrumented detector path in Standard composition; retain every manual, Web-only, Run-once, private-worker, startup-laziness, and heavy-dependency boundary.
4. Add deterministic fake-time, structured-sink, redaction, threshold, cancellation/failure, exact-once, and concurrency tests.
5. Run focused detector/coordinator/composition tests, the normal test suite, strict OpenSpec validation/status, and a block-59-only scope review. Rollback removes only the observer/decorator registration; no data, schema, settings, protocol, UI, or behavior migration exists.
