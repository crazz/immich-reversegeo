using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;

namespace ImmichReverseGeo.Tests.WorkerProcessExitOutcomes;

[TestClass]
[TestCategory("Change23")]
public sealed class WorkerProcessExitOutcomeTests
{
    [TestMethod]
    public void ClosedOutcomes_ExposeTheSevenLiteralPortableCodes()
    {
        var codes = WorkerProcessExitOutcome.All.Select(outcome => outcome.ExitCode).Order().ToArray();

        CollectionAssert.AreEqual(new[] { 0, 2, 3, 4, 5, 6, 130 }, codes);
        Assert.AreEqual(7, codes.Distinct().Count());
    }

    [TestMethod]
    public void ClosedFacts_ExposeOnlyThePredefinedLiteralDiagnostics()
    {
        var facts = new[]
        {
            (WorkerProcessExitFact.StartupInfrastructure(), 5, "infrastructure-failure", "startup", "worker infrastructure failed"),
            (WorkerProcessExitFact.TransportInfrastructure(), 5, "infrastructure-failure", "transport", "worker infrastructure failed"),
            (WorkerProcessExitFact.InputInfrastructure(), 5, "infrastructure-failure", "input", "worker infrastructure failed"),
            (WorkerProcessExitFact.InputInvalid(), 2, "invalid-input", "input", "worker invocation or input is invalid"),
            (WorkerProcessExitFact.ExecutionInfrastructure(), 5, "infrastructure-failure", "execution", "worker infrastructure failed"),
            (WorkerProcessExitFact.ExecutionFailure(), 4, "executor-failure", "execution", "worker executor failed"),
            (WorkerProcessExitFact.OutputTransport(), 6, "output-transport-failure", "output", "worker output transport failed"),
            (WorkerProcessExitFact.ShutdownCancelled(), 130, "cancelled", "shutdown", "worker cancellation or shutdown observed"),
            (WorkerProcessExitFact.CleanupInfrastructure(), 5, "infrastructure-failure", "cleanup", "worker infrastructure failed"),
            (WorkerProcessExitFact.Completed(), 0, "completed", "execution", "worker completed"),
            (WorkerProcessExitFact.Busy(), 3, "busy", "execution", "worker advisory lock is busy")
        };

        foreach (var (fact, code, token, phase, message) in facts)
        {
            Assert.AreEqual(code, fact.Outcome.ExitCode);
            Assert.AreEqual(token, fact.Outcome.Token);
            Assert.AreEqual(token, fact.Diagnostic.Token);
            Assert.AreEqual(phase, fact.Diagnostic.Phase);
            Assert.AreEqual(message, fact.Diagnostic.Message);
            Assert.AreEqual($"worker-exit-summary outcome={token} phase={phase} message={message}", fact.Diagnostic.FormatFinalSummary());
        }
    }

    [TestMethod]
    public void Combine_UsesExplicitLiteralStablePairWinnersInBothOrders()
    {
        var pairs = new[]
        {
            ("startup/transport", WorkerProcessExitFact.StartupInfrastructure(), WorkerProcessExitFact.TransportInfrastructure(), 5, "infrastructure-failure", "transport"),
            ("startup/input", WorkerProcessExitFact.StartupInfrastructure(), WorkerProcessExitFact.InputInvalid(), 5, "infrastructure-failure", "startup"),
            ("startup/execution", WorkerProcessExitFact.StartupInfrastructure(), WorkerProcessExitFact.ExecutionFailure(), 5, "infrastructure-failure", "startup"),
            ("startup/output", WorkerProcessExitFact.StartupInfrastructure(), WorkerProcessExitFact.OutputTransport(), 6, "output-transport-failure", "output"),
            ("startup/shutdown", WorkerProcessExitFact.StartupInfrastructure(), WorkerProcessExitFact.ShutdownCancelled(), 5, "infrastructure-failure", "startup"),
            ("startup/cleanup", WorkerProcessExitFact.StartupInfrastructure(), WorkerProcessExitFact.CleanupInfrastructure(), 5, "infrastructure-failure", "cleanup"),
            ("startup/completed", WorkerProcessExitFact.StartupInfrastructure(), WorkerProcessExitFact.Completed(), 5, "infrastructure-failure", "startup"),
            ("startup/busy", WorkerProcessExitFact.StartupInfrastructure(), WorkerProcessExitFact.Busy(), 5, "infrastructure-failure", "startup"),
            ("transport/input", WorkerProcessExitFact.TransportInfrastructure(), WorkerProcessExitFact.InputInvalid(), 5, "infrastructure-failure", "transport"),
            ("transport/execution", WorkerProcessExitFact.TransportInfrastructure(), WorkerProcessExitFact.ExecutionFailure(), 5, "infrastructure-failure", "transport"),
            ("transport/output", WorkerProcessExitFact.TransportInfrastructure(), WorkerProcessExitFact.OutputTransport(), 6, "output-transport-failure", "output"),
            ("transport/shutdown", WorkerProcessExitFact.TransportInfrastructure(), WorkerProcessExitFact.ShutdownCancelled(), 5, "infrastructure-failure", "transport"),
            ("transport/cleanup", WorkerProcessExitFact.TransportInfrastructure(), WorkerProcessExitFact.CleanupInfrastructure(), 5, "infrastructure-failure", "cleanup"),
            ("transport/completed", WorkerProcessExitFact.TransportInfrastructure(), WorkerProcessExitFact.Completed(), 5, "infrastructure-failure", "transport"),
            ("transport/busy", WorkerProcessExitFact.TransportInfrastructure(), WorkerProcessExitFact.Busy(), 5, "infrastructure-failure", "transport"),
            ("input/execution", WorkerProcessExitFact.InputInvalid(), WorkerProcessExitFact.ExecutionFailure(), 2, "invalid-input", "input"),
            ("input/output", WorkerProcessExitFact.InputInvalid(), WorkerProcessExitFact.OutputTransport(), 6, "output-transport-failure", "output"),
            ("input/shutdown", WorkerProcessExitFact.InputInvalid(), WorkerProcessExitFact.ShutdownCancelled(), 2, "invalid-input", "input"),
            ("input/cleanup", WorkerProcessExitFact.InputInvalid(), WorkerProcessExitFact.CleanupInfrastructure(), 5, "infrastructure-failure", "cleanup"),
            ("input/completed", WorkerProcessExitFact.InputInvalid(), WorkerProcessExitFact.Completed(), 2, "invalid-input", "input"),
            ("input/busy", WorkerProcessExitFact.InputInvalid(), WorkerProcessExitFact.Busy(), 2, "invalid-input", "input"),
            ("execution/output", WorkerProcessExitFact.ExecutionFailure(), WorkerProcessExitFact.OutputTransport(), 6, "output-transport-failure", "output"),
            ("execution/shutdown", WorkerProcessExitFact.ExecutionFailure(), WorkerProcessExitFact.ShutdownCancelled(), 4, "executor-failure", "execution"),
            ("execution/cleanup", WorkerProcessExitFact.ExecutionFailure(), WorkerProcessExitFact.CleanupInfrastructure(), 5, "infrastructure-failure", "cleanup"),
            ("execution/completed", WorkerProcessExitFact.ExecutionFailure(), WorkerProcessExitFact.Completed(), 4, "executor-failure", "execution"),
            ("execution/busy", WorkerProcessExitFact.ExecutionFailure(), WorkerProcessExitFact.Busy(), 3, "busy", "execution"),
            ("output/shutdown", WorkerProcessExitFact.OutputTransport(), WorkerProcessExitFact.ShutdownCancelled(), 6, "output-transport-failure", "output"),
            ("output/cleanup", WorkerProcessExitFact.OutputTransport(), WorkerProcessExitFact.CleanupInfrastructure(), 6, "output-transport-failure", "output"),
            ("output/completed", WorkerProcessExitFact.OutputTransport(), WorkerProcessExitFact.Completed(), 6, "output-transport-failure", "output"),
            ("output/busy", WorkerProcessExitFact.OutputTransport(), WorkerProcessExitFact.Busy(), 6, "output-transport-failure", "output"),
            ("shutdown/cleanup", WorkerProcessExitFact.ShutdownCancelled(), WorkerProcessExitFact.CleanupInfrastructure(), 5, "infrastructure-failure", "cleanup"),
            ("shutdown/completed", WorkerProcessExitFact.ShutdownCancelled(), WorkerProcessExitFact.Completed(), 130, "cancelled", "shutdown"),
            ("shutdown/busy", WorkerProcessExitFact.ShutdownCancelled(), WorkerProcessExitFact.Busy(), 3, "busy", "execution"),
            ("cleanup/completed", WorkerProcessExitFact.CleanupInfrastructure(), WorkerProcessExitFact.Completed(), 5, "infrastructure-failure", "cleanup"),
            ("cleanup/busy", WorkerProcessExitFact.CleanupInfrastructure(), WorkerProcessExitFact.Busy(), 5, "infrastructure-failure", "cleanup"),
            ("completed/busy", WorkerProcessExitFact.Completed(), WorkerProcessExitFact.Busy(), 3, "busy", "execution")
        };

        foreach (var (name, left, right, code, token, phase) in pairs)
        {
            AssertWinner(name, left, right, code, token, phase);
            AssertWinner($"{name} reversed", right, left, code, token, phase);
        }
    }

    [TestMethod]
    public void Combine_IsIdempotentForEveryClosedFact()
    {
        foreach (var fact in WorkerProcessExitFact.All)
        {
            Assert.AreSame(fact, WorkerProcessExitFact.Combine(fact, fact));
        }
    }

    [TestMethod]
    public void Accumulator_DistinguishesUnclassifiedSentinelFromExplicitCompletion()
    {
        var accumulator = new WorkerProcessExitOutcomeAccumulator();

        Assert.IsFalse(accumulator.HasFact, "worker-accumulator-unclassified-sentinel");
        Assert.AreSame(WorkerProcessExitFact.Completed(), accumulator.Fact, "worker-accumulator-sentinel-is-not-authoritative");

        accumulator.Add(WorkerProcessExitFact.Completed());

        Assert.IsTrue(accumulator.HasFact, "worker-accumulator-explicit-completion-observed");
        Assert.AreSame(WorkerProcessExitFact.Completed(), accumulator.Fact, "worker-accumulator-explicit-completion-reference");
    }

    [TestMethod]
    public void Accumulator_PreservesMultiFactBusyWinnerInBothOrders()
    {
        var forward = Accumulate(
        [
            WorkerProcessExitFact.Completed(),
            WorkerProcessExitFact.Busy(),
            WorkerProcessExitFact.ShutdownCancelled(),
            WorkerProcessExitFact.TransportInfrastructure(),
            WorkerProcessExitFact.OutputTransport()
        ]);
        var reverse = Accumulate(
        [
            WorkerProcessExitFact.OutputTransport(),
            WorkerProcessExitFact.TransportInfrastructure(),
            WorkerProcessExitFact.ShutdownCancelled(),
            WorkerProcessExitFact.Busy(),
            WorkerProcessExitFact.Completed()
        ]);

        Assert.AreEqual(6, forward.Outcome.ExitCode);
        Assert.AreEqual("output-transport-failure", forward.Diagnostic.Token);
        Assert.AreSame(forward, reverse);
    }

    [TestMethod]
    public void Diagnostic_UsesTheLiteralOneLineGrammarAnd160CharacterBound()
    {
        AssertLiteralMaximumLength(WorkerProcessExitDiagnostic.MaximumLength);
        AssertLiteralMarker(WorkerProcessExitDiagnostic.FinalSummaryMarker);

        foreach (var fact in WorkerProcessExitFact.All)
        {
            var diagnostic = fact.Diagnostic;
            var summary = diagnostic.FormatFinalSummary();

            Assert.IsTrue(summary.Length <= 160);
            Assert.AreEqual($"worker-exit-summary outcome={diagnostic.Token} phase={diagnostic.Phase} message={diagnostic.Message}", summary);
            Assert.IsFalse(summary.Contains('\r'));
            Assert.IsFalse(summary.Contains('\n'));
        }
    }

    private static void AssertLiteralMaximumLength(int maximumLength)
    {
        Assert.AreEqual(160, maximumLength, "worker-exit-summary-maximum-length");
    }

    private static void AssertLiteralMarker(string marker)
    {
        Assert.AreEqual("worker-exit-summary", marker, "worker-exit-summary-marker");
    }

    private static void AssertWinner(string name, WorkerProcessExitFact left, WorkerProcessExitFact right, int code, string token, string phase)
    {
        var winner = WorkerProcessExitFact.Combine(left, right);

        Assert.AreEqual(code, winner.Outcome.ExitCode, name);
        Assert.AreEqual(token, winner.Diagnostic.Token, name);
        Assert.AreEqual(phase, winner.Diagnostic.Phase, name);
    }

    private static WorkerProcessExitFact Accumulate(IEnumerable<WorkerProcessExitFact> facts)
    {
        var accumulator = new WorkerProcessExitOutcomeAccumulator();

        foreach (var fact in facts)
        {
            accumulator.Add(fact);
        }

        return accumulator.Fact;
    }
}
