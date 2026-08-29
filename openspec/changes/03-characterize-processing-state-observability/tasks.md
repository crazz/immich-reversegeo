## 1. Run snapshot characterization

- [ ] 1.1 Extend `tests/ImmichReverseGeo.Tests/ProcessingStateTests.cs` with a seeded prior-run case proving `StartRun(total)` publishes the new total, resets processed/skipped/error counters, clears `LastError`, marks the run active, records a start timestamp within captured UTC bounds, and retains the prior completion timestamp and log snapshot.
- [ ] 1.2 Add direct increment coverage proving processed and skipped counts advance independently and multiple errors advance the error count, replace `LastError` with the newest message, and append matching `[ERROR]` log entries.
- [ ] 1.3 Add a completion case proving inactive state, a completion timestamp within captured UTC bounds and not before the start, cleared activity, and retention of the final total, counters, latest error, and prior log snapshot.

## 2. Scoped activity characterization

- [ ] 2.1 Preserve and extend the equal-label overlap test so one disposed scope leaves the label visible and only the final matching disposal clears it.
- [ ] 2.2 Add a deterministic distinct-label case that begins A then B, disposes B, and observes A without asserting selection among multiple possible survivors.
- [ ] 2.3 Prove scope disposal is idempotent and that disposing a pre-completion scope after `CompleteRun` cannot restore activity.

## 3. Log and notification characterization

- [ ] 3.1 Append at least 101 uniquely numbered entries, read a `GetRecentLog()` snapshot, and prove exactly the newest 100 remain in insertion order by matching message suffixes rather than wall-clock prefixes.
- [ ] 3.2 Add table-driven or helper-based checks that `MarkPending`, `StartRun`, each counter/error increment, `AppendLog`, `SetActivity`, activity-scope begin/end, and `CompleteRun` each raise `OnChanged` at least once, resetting the observation flag after setup and never asserting exact callback counts.
- [ ] 3.3 Keep existing `ProcessingPipelineTests.cs` behavior checks passing; do not change production state, UI consumers, or introduce a reporter abstraction in this block.

## 4. Verification

- [ ] 4.1 Run `dotnet test --project tests/ImmichReverseGeo.Tests/ImmichReverseGeo.Tests.csproj --filter "FullyQualifiedName~ProcessingStateTests"`.
- [ ] 4.2 Run `npm run test` and confirm the repository's default Integration and Performance exclusions remain in effect.
