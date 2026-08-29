## Context

See `proposal.md` for motivation and the delta spec for required behavior. `ProcessingState` synchronously exposes the singleton snapshot consumed by Dashboard, Logs, and NavMenu. Counters use `Interlocked`; state, activity counts, and log snapshots use locks; log entries receive a UTC time-of-day prefix; and `IncrementError` calls `AppendLog` before its own notification. This direct state characterization has no implementation dependency on blocks 1 or 2; those blocks cover complementary pass and lifecycle observations.

## Goals / Non-Goals

**Goals:**
- Characterize state semantics directly in the existing focused MSTest class with deterministic, storage-free tests.
- Preserve the values and notifications a later in-process event adapter must reproduce.
- Separate contractual invariants from incidental timestamp text, callback multiplicity, and dictionary enumeration order.

**Non-Goals:**
- Change `ProcessingState`, Razor consumers, thread-safety, logging format, or production composition.
- Re-test scheduler admission, pending-state stale values, pass cancellation/failure, or lock recovery owned by blocks 1 and 2.
- Add reporter/event abstractions, Razor rendering tests, concurrent stress tests, or assertions against the live `RecentLog` queue.

## Decisions

- Put all new characterization in `ProcessingStateTests.cs`. Direct state tests isolate this synchronous contract without PostgreSQL, SQLite, geodata, host composition, or Razor timing. Driving the same cases through components or `ProcessingBackgroundService` would duplicate block 2 and obscure failures.
- Seed prior totals, counters, errors, completion timing, logs, and activity through public mutations, then call `StartRun` or `CompleteRun` and assert the resulting snapshot. This captures reset versus retention explicitly rather than inspecting fields.
- Bracket `StartRun` and `CompleteRun` with `DateTime.UtcNow` values and assert inclusive bounds and ordering. Exact timestamps or elapsed durations would be flaky and are not observable requirements.
- Cover activity ordering only where the survivor is unambiguous: begin A, begin B, end B, then observe A. Combine that with equal-label reference counting, double disposal, and completion-before-disposal. Do not select among multiple remaining labels because `Dictionary.Keys.Last()` enumeration is an implementation detail.
- Append at least 101 uniquely numbered log messages, read via `GetRecentLog()`, strip or ignore the fixed timestamp prefix, and assert count, first/last suffixes, and insertion order. The public `RecentLog` queue remains a later review concern, not a contract target.
- For notifications, subscribe immediately before each public mutation under test, reset a boolean after setup, and assert at least one callback. Do not assert exact counts because compound operations such as `IncrementError` legitimately notify more than once.

## Risks / Trade-offs

- [Direct unit tests do not prove Razor rerendering] → Keep component rendering outside this characterization; all three consumers already subscribe to the same event or read the same snapshot.
- [Retaining terminal counters, errors, totals, and logs constrains the later adapter] → This retention is currently UI-visible and is the purpose of the baseline; intentional future changes require a separate behavior change.
- [Sequential tests do not prove every concurrent interleaving] → Characterize deterministic observable semantics here and leave synchronization redesign or stress coverage out of scope.
- [Wall-clock prefixes can cross a second boundary] → Assert message suffixes and ordering, never the generated prefix.

## Migration Plan

1. Extend only the state-focused tests; production code and UI remain unchanged.
2. Run the focused test class, then the default repository test suite.
3. Roll back by reverting the added tests; there is no runtime or data migration.
