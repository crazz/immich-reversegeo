using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerEventStateBridge;
using Microsoft.Extensions.DependencyInjection;

namespace ImmichReverseGeo.Tests;

[TestClass]
[TestCategory("Change27")]
public sealed class WorkerEventStateBridgeLifecycleTests
{
    [TestMethod]
    [DataRow(0L)]
    [DataRow(4L)]
    public async Task ReadyAndRunStarted_PreservePendingUntilEligibility(long total)
    {
        await using var test = new BridgeTestCase(previousRun: true);
        var pending = test.Snapshot();
        await test.ReadyAsync();
        Assert.AreEqual(pending, test.Snapshot());
        Assert.IsTrue(test.Bridge.IsReady);
        Assert.AreSame(test.Request, test.Bridge.Request);

        await test.SendAsync(new RunStarted(test.Request, BridgeTestCase.StartedAt));
        Assert.AreEqual(pending, test.Snapshot(), "Run-started claims correlation without resetting visible state.");
        await test.SendAsync(new EligibilityDetermined(test.Request, total));
        Assert.IsTrue(test.State.IsRunning);
        Assert.AreEqual(total, test.State.TotalUnprocessed);
        Assert.AreEqual(0L, test.State.ProcessedThisRun);
        Assert.AreEqual(0L, test.State.SkippedThisRun);
        Assert.AreEqual(0L, test.State.ErrorsThisRun);
        Assert.IsNull(test.State.LastError);
        Assert.AreEqual(pending.Completed, test.State.LastRunCompleted);
        Assert.IsTrue(test.Logs.Any(line => line.EndsWith("prior history", StringComparison.Ordinal)));
        StringAssert.EndsWith(test.Logs.Last(), total == 0
            ? "Run started — nothing to process, all assets already have location data."
            : "Run started. 4 assets to process.");
        Assert.IsTrue(test.Notifications > pending.Notifications);
        await test.FinishAsync(ProcessingRunOutcome.Completed);
    }

    [TestMethod]
    [DataRow(ProcessingRunOutcome.Cancelled)]
    [DataRow(ProcessingRunOutcome.Failed)]
    public async Task TerminalBeforeEligibility_PreservesPriorCountsAndStart(ProcessingRunOutcome outcome)
    {
        await using var test = new BridgeTestCase(previousRun: true);
        var pending = test.Snapshot();
        await test.ReadyAsync();
        await test.SendAsync(new RunStarted(test.Request, BridgeTestCase.StartedAt));
        await test.FinishAsync(outcome);
        Assert.IsFalse(test.State.IsRunning);
        Assert.AreEqual(pending.Started, test.State.LastRunStarted);
        Assert.AreEqual(pending.Total, test.State.TotalUnprocessed);
        Assert.AreEqual(pending.Updated, test.State.ProcessedThisRun);
        Assert.AreEqual(pending.Skipped, test.State.SkippedThisRun);
        Assert.AreEqual(pending.Errors + (outcome == ProcessingRunOutcome.Failed ? 1 : 0), test.State.ErrorsThisRun);
        Assert.AreEqual(outcome == ProcessingRunOutcome.Failed ? "Fatal: terminal failure" : pending.Error, test.State.LastError);
        Assert.IsTrue(test.Bridge.IsTerminal);
        Assert.IsFalse(test.Adapter.IsArmed(test.Request));
        Assert.IsFalse(test.Logs.Any(line => line.Contains("Run started", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow(ProcessingRunOutcome.Completed)]
    [DataRow(ProcessingRunOutcome.Cancelled)]
    [DataRow(ProcessingRunOutcome.Failed)]
    public async Task AbsoluteProgressAndHandledDiagnostic_PreserveOrdinaryVersusFatalAccounting(ProcessingRunOutcome outcome)
    {
        await using var test = new BridgeTestCase();
        await test.BeginAsync(4);
        await test.ProgressAsync(1, 0, 0);
        await test.ProgressAsync(1, 1, 0);
        await test.ProgressAsync(1, 2, 0);
        await test.SendAsync(new LogEmitted(test.Request, ProcessingLogLevel.Error, "handled asset"));
        Assert.AreEqual(0L, test.State.ErrorsThisRun, "A diagnostic must not count a disposition.");
        await test.ProgressAsync(1, 2, 1);
        Assert.AreEqual(1L, test.State.ProcessedThisRun, "Visible processed counts writes, not all four dispositions.");
        Assert.AreEqual(2L, test.State.SkippedThisRun);
        Assert.AreEqual(1L, test.State.ErrorsThisRun);
        Assert.AreEqual("handled asset", test.State.LastError);
        Assert.AreEqual(1, test.Logs.Count(line => line.EndsWith("[ERROR] handled asset", StringComparison.Ordinal)));

        var notifications = new List<BridgeStateSnapshot>();
        test.State.OnChanged += () => notifications.Add(test.Snapshot());
        await test.FinishAsync(outcome, 1, 2, 1);
        var errors = outcome == ProcessingRunOutcome.Failed ? 2L : 1L;
        Assert.AreEqual(errors, test.State.ErrorsThisRun);
        Assert.AreEqual(outcome == ProcessingRunOutcome.Failed ? "Fatal: terminal failure" : "handled asset", test.State.LastError);
        StringAssert.EndsWith(test.Logs.Last(), $"Run complete. Processed=1 Skipped=2 Errors={errors}");
        Assert.AreEqual(outcome == ProcessingRunOutcome.Cancelled ? 1 : 0, test.Logs.Count(line => line.EndsWith("Run cancelled.", StringComparison.Ordinal)));
        Assert.AreEqual(outcome == ProcessingRunOutcome.Failed ? 1 : 0, test.Logs.Count(line => line.EndsWith("[ERROR] Fatal: terminal failure", StringComparison.Ordinal)));
        Assert.IsTrue(notifications.Any(snapshot => !snapshot.Running && !snapshot.Logs.EndsWith($"Run complete. Processed=1 Skipped=2 Errors={errors}", StringComparison.Ordinal)),
            "Completion must notify before the final summary is appended.");
        var completed = test.Snapshot();
        await test.Bridge.DisposeAsync();
        await test.Bridge.DisposeAsync();
        Assert.AreEqual(completed, test.Snapshot());
    }

    [TestMethod]
    public async Task TypedLogLevels_PreserveDecorationOrderingAndNewestHundredRetention()
    {
        await using var test = new BridgeTestCase();
        await test.BeginAsync(0);
        foreach (var (level, message, suffix) in new[]
        {
            (ProcessingLogLevel.Trace, "trace", "trace"),
            (ProcessingLogLevel.Information, "info", "info"),
            (ProcessingLogLevel.Warning, "warning", "[WARN] warning"),
            (ProcessingLogLevel.Error, "error", "[ERROR] error")
        })
        {
            var before = test.Notifications;
            await test.SendAsync(new LogEmitted(test.Request, level, message));
            StringAssert.EndsWith(test.Logs.Last(), suffix);
            Assert.IsTrue(test.Notifications > before, "Each accepted log must notify before callback acceptance.");
        }

        Assert.AreEqual("error", test.State.LastError);
        Assert.AreEqual(0L, test.State.ErrorsThisRun);
        for (var index = 0; index < 105; index++)
        {
            await test.SendAsync(new LogEmitted(test.Request, ProcessingLogLevel.Information, $"ordered-{index:D3}"));
        }
        Assert.AreEqual(100, test.Logs.Length);
        for (var index = 0; index < 100; index++)
        {
            StringAssert.EndsWith(test.Logs[index], $"ordered-{index + 5:D3}");
        }
        await test.FinishAsync(ProcessingRunOutcome.Completed);
    }

    [TestMethod]
    public async Task ActivityIds_PreserveEqualLabelsAndOutOfOrderEnds()
    {
        await using var test = new BridgeTestCase();
        await test.BeginAsync(0);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        await test.SendAsync(new ActivityStarted(test.Request, first, "same"));
        await test.SendAsync(new ActivityStarted(test.Request, second, "same"));
        await test.SendAsync(new ActivityEnded(test.Request, first));
        Assert.AreEqual("same", test.State.CurrentActivity);
        await test.SendAsync(new ActivityStarted(test.Request, third, "different"));
        Assert.AreEqual("different", test.State.CurrentActivity);
        await test.SendAsync(new ActivityEnded(test.Request, third));
        Assert.AreEqual("same", test.State.CurrentActivity);
        await test.SendAsync(new ActivityEnded(test.Request, second));
        Assert.IsNull(test.State.CurrentActivity);
        await test.FinishAsync(ProcessingRunOutcome.Completed);
    }

    [TestMethod]
    public async Task NonterminalDisposal_ClearsOnlyOwnedActivitiesAndCannotAffectLaterArm()
    {
        await using var test = new BridgeTestCase();
        await test.BeginAsync(0);
        await test.SendAsync(new ActivityStarted(test.Request, Guid.NewGuid(), "old activity"));
        var before = test.Snapshot();
        await test.Bridge.DisposeAsync();
        Assert.IsNull(test.State.CurrentActivity);
        Assert.IsTrue(test.State.IsRunning);
        Assert.AreEqual(before.Completed, test.State.LastRunCompleted);
        Assert.AreEqual(before.Logs, string.Join('\n', test.Logs));
        Assert.AreEqual(before.Error, test.State.LastError);
        Assert.IsTrue(test.Adapter.IsArmed(test.Request), "Abandonment does not claim terminal ownership release.");
        Assert.IsInstanceOfType<WorkerEventStateBridgeObservation.NonterminalDisposed>(test.Bridge.FirstObservation);

        Assert.IsTrue(await test.Adapter.TryProjectAsync(new RunFinished(test.Request, test.Result(ProcessingRunOutcome.Cancelled)), CancellationToken.None));
        var next = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual);
        test.State.MarkPending();
        Assert.IsTrue(test.Adapter.Arm(next));
        Assert.IsTrue(await test.Adapter.TryProjectAsync(new EligibilityDetermined(next, 0), CancellationToken.None));
        Assert.IsTrue(await test.Adapter.TryProjectAsync(new ActivityStarted(next, Guid.NewGuid(), "next activity"), CancellationToken.None));
        var later = test.Snapshot();
        await test.Bridge.DisposeAsync();
        Assert.AreEqual(later, test.Snapshot(), "Late old-bridge disposal must not clear the new run's activity.");
        Assert.IsTrue(test.Adapter.AbandonProjectedActivities(next));
    }

    [TestMethod]
    public async Task RegisteredFactory_UsesTheExactAdmittedSingletonAdapterWithoutOpeningReporterSession()
    {
        var services = new ServiceCollection();
        services.AddProcessingControlPlaneServices();
        await using var provider = services.BuildServiceProvider();
        var state = provider.GetRequiredService<ProcessingState>();
        var adapter = provider.GetRequiredService<ProcessingStateEventReporter>();
        var factory = provider.GetRequiredService<WorkerEventStateBridgeFactory>();
        Assert.AreSame(adapter, provider.GetRequiredService<IProcessingEventReporter>());
        Assert.AreSame(factory, provider.GetRequiredService<WorkerEventStateBridgeFactory>());
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual);
        state.MarkPending();
        Assert.IsTrue(adapter.Arm(request));
        await using var bridge = factory.Create(request);
        IWorkerProtocolEventSink sink = bridge;
        await sink.AcceptAsync(WorkerProtocolMapper.Ready(1, BridgeTestCase.ReadyAt), CancellationToken.None);
        await sink.AcceptAsync(WorkerProtocolMapper.Map(new RunStarted(request, BridgeTestCase.StartedAt), 2), CancellationToken.None);
        await sink.AcceptAsync(WorkerProtocolMapper.Map(new EligibilityDetermined(request, 7), 3, BridgeTestCase.StartedAt.AddTicks(3)), CancellationToken.None);
        Assert.AreEqual(7L, state.TotalUnprocessed, "The factory must project into the registered singleton state, not a parallel adapter.");
        Assert.IsTrue(adapter.IsArmed(request));
    }
}
