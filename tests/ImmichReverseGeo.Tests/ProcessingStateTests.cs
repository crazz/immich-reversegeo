using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class ProcessingStateTests
{
    [TestMethod]
    public void StartRun_ReplacesPriorRunValuesAndRetainsCompletionAndLogs()
    {
        var state = new ProcessingState();
        state.StartRun(7);
        state.IncrementProcessed();
        state.IncrementSkipped();
        state.IncrementError("prior error");
        state.AppendLog("prior log");
        state.CompleteRun();

        var priorCompletion = state.LastRunCompleted;
        var priorLog = state.GetRecentLog();
        var beforeStart = DateTime.UtcNow;
        state.StartRun(42);
        var afterStart = DateTime.UtcNow;

        Assert.AreEqual(42L, state.TotalUnprocessed);
        Assert.AreEqual(0L, state.ProcessedThisRun);
        Assert.AreEqual(0L, state.SkippedThisRun);
        Assert.AreEqual(0L, state.ErrorsThisRun);
        Assert.IsNull(state.LastError);
        Assert.IsTrue(state.IsRunning);
        Assert.IsNotNull(state.LastRunStarted);
        Assert.IsTrue(state.LastRunStarted >= beforeStart);
        Assert.IsTrue(state.LastRunStarted <= afterStart);
        Assert.AreEqual(priorCompletion, state.LastRunCompleted);
        CollectionAssert.AreEqual(priorLog.ToArray(), state.GetRecentLog().ToArray());
    }

    [TestMethod]
    public void IncrementOutcomes_UpdatesIndependentCountersAndNewestErrorLog()
    {
        var state = new ProcessingState();
        state.StartRun(5);

        state.IncrementProcessed();
        state.IncrementProcessed();
        state.IncrementSkipped();
        state.IncrementError("first error");
        state.IncrementError("latest error");

        Assert.AreEqual(2L, state.ProcessedThisRun);
        Assert.AreEqual(1L, state.SkippedThisRun);
        Assert.AreEqual(2L, state.ErrorsThisRun);
        Assert.AreEqual("latest error", state.LastError);

        var log = state.GetRecentLog();
        Assert.IsTrue(log.Any(entry => entry.EndsWith("[ERROR] first error", StringComparison.Ordinal)));
        Assert.IsTrue(log.Any(entry => entry.EndsWith("[ERROR] latest error", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void CompleteRun_ClearsTransientStateAndRetainsFinalSnapshot()
    {
        var state = new ProcessingState();
        state.StartRun(8);
        state.IncrementProcessed();
        state.IncrementSkipped();
        state.IncrementError("final error");
        state.AppendLog("final log");
        var scope = state.BeginActivity("Downloading data");
        var start = state.LastRunStarted;
        var logBeforeCompletion = state.GetRecentLog();
        var beforeCompletion = DateTime.UtcNow;

        state.CompleteRun();

        var afterCompletion = DateTime.UtcNow;
        Assert.IsFalse(state.IsRunning);
        Assert.IsNotNull(state.LastRunCompleted);
        Assert.IsTrue(state.LastRunCompleted >= beforeCompletion);
        Assert.IsTrue(state.LastRunCompleted <= afterCompletion);
        Assert.IsTrue(state.LastRunCompleted >= start);
        Assert.IsNull(state.CurrentActivity);
        Assert.AreEqual(8L, state.TotalUnprocessed);
        Assert.AreEqual(1L, state.ProcessedThisRun);
        Assert.AreEqual(1L, state.SkippedThisRun);
        Assert.AreEqual(1L, state.ErrorsThisRun);
        Assert.AreEqual("final error", state.LastError);
        CollectionAssert.AreEqual(logBeforeCompletion.ToArray(), state.GetRecentLog().ToArray());

        scope.Dispose();
    }

    [TestMethod]
    public void BeginActivity_KeepsActivityVisibleUntilLastScopeEnds()
    {
        var state = new ProcessingState();

        var scope1 = state.BeginActivity("Downloading Overture divisions for Spain (ESP)...");
        var scope2 = state.BeginActivity("Downloading Overture divisions for Spain (ESP)...");

        Assert.AreEqual("Downloading Overture divisions for Spain (ESP)...", state.CurrentActivity);

        scope1.Dispose();
        Assert.AreEqual("Downloading Overture divisions for Spain (ESP)...", state.CurrentActivity);

        scope2.Dispose();
        Assert.IsNull(state.CurrentActivity);
    }

    [TestMethod]
    public void BeginActivity_WhenLaterDistinctScopeEnds_ShowsOnlyRemainingScope()
    {
        var state = new ProcessingState();
        var first = state.BeginActivity("A");
        var second = state.BeginActivity("B");

        Assert.AreEqual("B", state.CurrentActivity);

        second.Dispose();

        Assert.AreEqual("A", state.CurrentActivity);
        first.Dispose();
    }

    [TestMethod]
    public void ActivityScope_DisposalIsIdempotentAndCannotRestoreCompletedActivity()
    {
        var state = new ProcessingState();
        var scope = state.BeginActivity("Downloading data");

        scope.Dispose();
        scope.Dispose();
        Assert.IsNull(state.CurrentActivity);

        var preCompletionScope = state.BeginActivity("Finishing data");
        state.CompleteRun();
        preCompletionScope.Dispose();
        preCompletionScope.Dispose();

        Assert.IsNull(state.CurrentActivity);
    }

    [TestMethod]
    public void GetRecentLog_RetainsNewestHundredEntriesInInsertionOrder()
    {
        var state = new ProcessingState();

        for (var index = 1; index <= 101; index++)
        {
            state.AppendLog($"entry-{index:D3}");
        }

        var snapshot = state.GetRecentLog();
        Assert.AreEqual(100, snapshot.Count);

        for (var index = 0; index < snapshot.Count; index++)
        {
            Assert.IsTrue(snapshot[index].EndsWith($"entry-{index + 2:D3}", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void ObservableMutations_RaiseOnChanged()
    {
        var state = new ProcessingState();

        AssertRaisesOnChanged(state, state.MarkPending);
        AssertRaisesOnChanged(state, () => state.StartRun(3));
        AssertRaisesOnChanged(state, state.IncrementProcessed);
        AssertRaisesOnChanged(state, state.IncrementSkipped);
        AssertRaisesOnChanged(state, () => state.IncrementError("error"));
        AssertRaisesOnChanged(state, () => state.AppendLog("log"));
        AssertRaisesOnChanged(state, () => state.SetActivity("activity"));

        IDisposable? scope = null;
        AssertRaisesOnChanged(state, () => scope = state.BeginActivity("scoped activity"));
        Assert.IsNotNull(scope);
        AssertRaisesOnChanged(state, scope.Dispose);

        state.BeginActivity("completion activity");
        AssertRaisesOnChanged(state, state.CompleteRun);
    }

    [TestMethod]
    public void ProjectionOperations_ReplaceCountersAndNotifyWithoutDiagnosticDoubleCount()
    {
        var state = new ProcessingState();
        state.StartRun(10);
        state.IncrementProcessed();
        state.IncrementSkipped();
        state.IncrementError("prior");
        var notifications = 0;
        state.OnChanged += () => notifications++;

        state.ApplyProgress(2, 3, 4);
        Assert.IsTrue(notifications > 0);
        Assert.AreEqual(2L, state.ProcessedThisRun);
        Assert.AreEqual(3L, state.SkippedThisRun);
        Assert.AreEqual(4L, state.ErrorsThisRun);

        notifications = 0;
        state.ReportErrorDiagnostic("latest");
        Assert.IsTrue(notifications > 0);
        Assert.AreEqual(4L, state.ErrorsThisRun);
        Assert.AreEqual("latest", state.LastError);
        Assert.IsTrue(state.GetRecentLog().Last().EndsWith("[ERROR] latest", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ApplyProgress_ExchangesOnlyWholeImmutableSnapshotsUnderConcurrency()
    {
        var state = new ProcessingState();
        var first = (Processed: 11L, Skipped: 12L, Errors: 13L);
        var second = (Processed: 21L, Skipped: 22L, Errors: 23L);
        using var start = new ManualResetEventSlim();
        var writer = Task.Run(() =>
        {
            start.Wait();
            for (var index = 0; index < 10_000; index++)
            {
                var value = index % 2 == 0 ? first : second;
                state.ApplyProgress(value.Processed, value.Skipped, value.Errors);
            }
        });
        var reader = Task.Run(() =>
        {
            start.Wait();
            for (var index = 0; index < 10_000; index++)
            {
                var snapshot = state.ReadProgressSnapshot();
                Assert.IsTrue(snapshot == new ProcessingState.ProgressSnapshot(first.Processed, first.Skipped, first.Errors)
                    || snapshot == new ProcessingState.ProgressSnapshot(second.Processed, second.Skipped, second.Errors)
                    || snapshot == ProcessingState.ProgressSnapshot.Empty);
            }
        });

        start.Set();
        await Task.WhenAll(writer, reader);
    }

    private static void AssertRaisesOnChanged(ProcessingState state, Action mutation)
    {
        var changed = false;
        Action handler = () => changed = true;
        state.OnChanged += handler;

        try
        {
            changed = false;
            mutation();
            Assert.IsTrue(changed);
        }
        finally
        {
            state.OnChanged -= handler;
        }
    }
}
