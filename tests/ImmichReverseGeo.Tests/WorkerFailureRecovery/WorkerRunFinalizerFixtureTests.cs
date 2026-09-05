using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerEventStateBridge;
using ImmichReverseGeo.Web.WorkerFailureRecovery;
using ImmichReverseGeo.Tests.WorkerProcessFixture;
using System.Reflection;

namespace ImmichReverseGeo.Tests.WorkerFailureRecovery;

[TestClass]
[TestCategory("Change30")]
public class WorkerRunFinalizerFixtureTests
{
    private static readonly WorkerRunFailureCategory[] PreReadyCategories =
    [
        WorkerRunFailureCategory.PreReadyEndOfStream,
        WorkerRunFailureCategory.StartupCrash
    ];

    private static readonly FixtureScenario[] Matrix =
    [
        new("ready", true, [], ProcessingRunOutcome.Completed, [WorkerRunFailureCategory.Terminal], 0),
        new("no-work", true, [], ProcessingRunOutcome.Completed, [WorkerRunFailureCategory.Terminal], 0),
        new("success", true, [], ProcessingRunOutcome.Completed, [WorkerRunFailureCategory.Terminal], 0),
        new("pre-ready-crash", false, ["--exit-code", "42"], ProcessingRunOutcome.Failed, PreReadyCategories, 42),
        new("post-ready-crash", true, ["--exit-code", "42"], ProcessingRunOutcome.Failed, [WorkerRunFailureCategory.UnmappedExit], 42),
        new("malformed", true, ["--malformed-kind", "json"], ProcessingRunOutcome.Failed, [WorkerRunFailureCategory.MalformedFrame], 0, true),
        new("oversize", true, [], ProcessingRunOutcome.Failed, [WorkerRunFailureCategory.OversizedFrame], 0),
        new("unknown", true, ["--unknown-kind", "version"], ProcessingRunOutcome.Failed, [WorkerRunFailureCategory.UnknownOrIncompatible], 0),
        new("invalid-sequence", true, ["--sequence-fault", "gap"], ProcessingRunOutcome.Failed, [WorkerRunFailureCategory.Sequence], 0),
        new("terminal-mismatch", true, ["--terminal", "completed", "--exit-code", "4"], ProcessingRunOutcome.Completed, [WorkerRunFailureCategory.Terminal], 4, ExpectedAnomaly: WorkerRunAnomaly.TerminalExitMismatch),
        new("stderr-flood", true, ["--stderr-bytes", "262177"], ProcessingRunOutcome.Completed, [WorkerRunFailureCategory.Terminal], 0, false, 262177),
        new("raw-exit", false, ["--exit-code", "0"], ProcessingRunOutcome.Failed, PreReadyCategories, 0),
        new("raw-exit", false, ["--exit-code", "2"], ProcessingRunOutcome.Failed, PreReadyCategories, 2),
        new("raw-exit", false, ["--exit-code", "3"], ProcessingRunOutcome.Failed, PreReadyCategories, 3),
        new("raw-exit", false, ["--exit-code", "4"], ProcessingRunOutcome.Failed, PreReadyCategories, 4),
        new("raw-exit", false, ["--exit-code", "5"], ProcessingRunOutcome.Failed, PreReadyCategories, 5),
        new("raw-exit", false, ["--exit-code", "6"], ProcessingRunOutcome.Failed, PreReadyCategories, 6),
        new("raw-exit", false, ["--exit-code", "130"], ProcessingRunOutcome.Failed, PreReadyCategories, 130),
        new("raw-exit", false, ["--exit-code", "42"], ProcessingRunOutcome.Failed, PreReadyCategories, 42)
    ];

    [TestMethod]
    [DynamicData(nameof(FinalizerMatrix), DynamicDataDisplayName = nameof(MatrixDisplayName))]
    public async Task RealFixture_FinalizerProducesOneSafeReceiptAfterPhysicalEvidenceFinality(object item)
    {
        var scenario = (FixtureScenario)item;
        var observed = await RunAsync(scenario);

        Assert.AreEqual(scenario.Outcome, observed.Result.Outcome, $"{scenario.Label}-outcome");
        CollectionAssert.Contains(scenario.AllowedCategories, observed.Decision.Category, $"{scenario.Label}-causal-category");
        Assert.AreEqual(scenario.ExitCode, observed.Completion.ExitCode, $"{scenario.Label}-raw-exit");
        Assert.AreSame(observed.Request, observed.Receipt.Request, $"{scenario.Label}-receipt-request");
        Assert.AreSame(observed.Request, observed.Receipt.Result.Request, $"{scenario.Label}-receipt-result-request");
        Assert.AreSame(observed.Receipt.Result, observed.Result, $"{scenario.Label}-single-terminal-result");
        Assert.IsFalse(observed.Decision.Retry, $"{scenario.Label}-no-retry");
        Assert.IsFalse(observed.State.IsRunning, $"{scenario.Label}-ui-inactive");
        Assert.IsTrue(string.IsNullOrEmpty(observed.State.CurrentActivity), $"{scenario.Label}-activities-cleared");
        Assert.AreEqual(1, CountLog(observed.State, "Run complete."), $"{scenario.Label}-single-summary");
        AssertSafeFailure(observed.State.LastError, scenario.Outcome, scenario.Label);
        Assert.AreEqual(1, observed.ProcessDisposeCalls, $"{scenario.Label}-session-disposed-once");
        Assert.IsFalse(observed.FixtureRegistered, $"{scenario.Label}-fixture-cleaned");

        if (scenario.RequireFaultMonitor)
        {
            Assert.IsTrue(observed.FaultMonitorCalls > 0, $"{scenario.Label}-fault-monitor-invoked");
        }

        Assert.AreEqual(scenario.ExpectedAnomaly, observed.Decision.Anomalies & scenario.ExpectedAnomaly, $"{scenario.Label}-expected-anomaly");

        if (scenario.StandardErrorBytes is { } bytes)
        {
            Assert.AreEqual(bytes, observed.Completion.StandardErrorTail.TotalBytes, $"{scenario.Label}-stderr-total");
            Assert.IsTrue(observed.Completion.StandardErrorTail.IsTruncated, $"{scenario.Label}-stderr-truncated");
            Assert.IsFalse(observed.Completion.StandardErrorTail.TotalBytesSaturated, $"{scenario.Label}-stderr-not-saturated");
            Assert.IsTrue(observed.Completion.StandardErrorTail.Text.EndsWith("\nfixture-stderr-suffix\n", StringComparison.Ordinal), $"{scenario.Label}-stderr-suffix");
        }
    }

    public static IEnumerable<object[]> FinalizerMatrix() => Matrix.Select(scenario => new object[] { scenario });

    public static string MatrixDisplayName(MethodInfo _, object[] data) => ((FixtureScenario)data[0]).Label;

    private static async Task<ObservedRun> RunAsync(FixtureScenario scenario)
    {
        var evidenceGate = new ChildWorkerEvidenceFinalityGate();
        var lease = new WorkerProcessFixtureLease
        {
            LauncherOptions = new ChildWorkerLauncherOptions
            {
                TimeProvider = TimeProvider.System,
                EvidenceFinalityGate = evidenceGate
            }
        };
        var state = new ProcessingState();
        var reporter = new ProcessingStateEventReporter(state);
        WorkerEventStateBridge? bridge = null;
        ObservedRun? observed = null;

        await using (lease)
        {
            try
            {
                Assert.IsTrue(reporter.Arm(lease.Request), $"{scenario.Label}-reporter-armed");
                state.MarkPending();
                bridge = new WorkerEventStateBridgeFactory(reporter).Create(lease.Request);
                var session = await lease.LaunchAsync(scenario.Name, bridge, scenario.Capture, scenario.Options);
                var finalizer = new WorkerRunFinalizer(lease.Request, reporter, TimeProvider.System, evidenceGate);
                var faultMonitorCalls = 0;
                var result = await finalizer.Start(
                    session,
                    bridge,
                    fault =>
                    {
                        faultMonitorCalls++;
                        _ = session.RequestTermination(new ChildWorkerTerminationRequest(
                            fault.ObservedAt,
                            ChildWorkerTerminationIntent.FaultContainment,
                            fault.Reason));
                    },
                    static () => false).WaitAsync(WorkerProcessFixtureLease.Watchdog);
                var settlement = await session.Settlement.WaitAsync(WorkerProcessFixtureLease.Watchdog);
                Assert.IsNotNull(finalizer.Evidence, $"{scenario.Label}-frozen-evidence");
                Assert.IsNotNull(finalizer.Decision, $"{scenario.Label}-final-decision");
                Assert.IsNotNull(reporter.GetFinalizationReceipt(lease.Request), $"{scenario.Label}-receipt");
                var evidence = finalizer.Evidence!;
                var decision = finalizer.Decision!;
                var receipt = reporter.GetFinalizationReceipt(lease.Request)!;

                Assert.IsNotNull(evidence.Completion, $"{scenario.Label}-completion-evidence");
                var completion = evidence.Completion!;
                Assert.AreSame(completion, settlement, $"{scenario.Label}-result-waits-physical-pumps");
                Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(evidence.Completion.StandardOutputFinality, $"{scenario.Label}-stdout-final");
                Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(evidence.Completion.StandardErrorFinality, $"{scenario.Label}-stderr-final");
                Assert.AreEqual(1, lease.ProcessDisposeCalls, $"{scenario.Label}-finalizer-session-disposed");

                if (scenario.Capture)
                {
                    lease.AssertExactCapture();
                }

                observed = new ObservedRun(
                    scenario.Label,
                    lease.Request,
                    state,
                    result,
                    receipt,
                    decision,
                    completion,
                    faultMonitorCalls,
                    lease.ProcessDisposeCalls,
                    lease.IsRegistered);
            }
            finally
            {
                evidenceGate.Release();
                if (observed is null && bridge is not null)
                {
                    await bridge.DisposeAsync();
                }
            }
        }

        Assert.IsNotNull(observed, $"{scenario.Label}-observed");
        return observed! with
        {
            ProcessDisposeCalls = lease.ProcessDisposeCalls,
            FixtureRegistered = lease.IsRegistered
        };
    }

    private static int CountLog(ProcessingState state, string value) =>
        state.GetRecentLog().Count(line => line.Contains(value, StringComparison.Ordinal));

    private static void AssertSafeFailure(string? lastError, ProcessingRunOutcome outcome, string label)
    {
        if (outcome == ProcessingRunOutcome.Completed)
        {
            Assert.IsNull(lastError, $"{label}-no-error");
            return;
        }

        Assert.IsFalse(string.IsNullOrWhiteSpace(lastError), $"{label}-safe-error-present");
        Assert.IsTrue(lastError.Length <= 256, $"{label}-safe-error-bounded");
        Assert.IsFalse(lastError.Contains("fixture:", StringComparison.OrdinalIgnoreCase), $"{label}-stderr-not-exposed");
        Assert.IsFalse(lastError.Contains("StackTrace", StringComparison.Ordinal), $"{label}-stack-not-exposed");
    }

    private sealed record FixtureScenario(
        string Name,
        bool Capture,
        string[] Options,
        ProcessingRunOutcome Outcome,
        WorkerRunFailureCategory[] AllowedCategories,
        int ExitCode,
        bool RequireFaultMonitor = false,
        int? StandardErrorBytes = null,
        WorkerRunAnomaly ExpectedAnomaly = WorkerRunAnomaly.None)
    {
        internal string Label => Options.Length == 0 ? Name : $"{Name}:{string.Join(':', Options)}";
    }

    private sealed record ObservedRun(
        string Label,
        ProcessingRunRequest Request,
        ProcessingState State,
        ProcessingRunResult Result,
        ProcessingRunFinalizationReceipt Receipt,
        WorkerRunDecision Decision,
        ChildWorkerCompletionObservation Completion,
        int FaultMonitorCalls,
        int ProcessDisposeCalls,
        bool FixtureRegistered);
}
