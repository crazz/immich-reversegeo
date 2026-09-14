using ImmichReverseGeo.Core.ApplicationRole;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using ImmichReverseGeo.Web.RunOnce;

namespace ImmichReverseGeo.Tests.RunOnceDeploymentMode;

[TestClass]
[TestCategory("Change43")]
public sealed class RunOnceProcessOutcomeTests
{
    [TestMethod]
    [DataRow(WorkerProcessExitCodes.Completed, false)]
    [DataRow(WorkerProcessExitCodes.Busy, true)]
    [DataRow(WorkerProcessExitCodes.ExecutorFailure, true)]
    [DataRow(WorkerProcessExitCodes.InfrastructureFailure, true)]
    [DataRow(WorkerProcessExitCodes.Cancelled, true)]
    public void ProcessBoundary_ReturnsExistingSubsetAndWritesOnlyBoundedNonzeroSummary(
        int expectedExit,
        bool expectsSummary)
    {
        var stderr = new StringWriter();
        var calls = 0;

        int actual = RunOnceProcess.Run(
            DeploymentMode.RunOnce,
            [],
            TextWriter.Null,
            stderr,
            (_, _, _, _, _, outcomes) =>
            {
                calls++;
                outcomes.Add(Fact(expectedExit));
                return Task.FromResult(expectedExit);
            },
            _ => null);

        Assert.AreEqual(expectedExit, actual);
        Assert.AreEqual(1, calls);
        if (expectsSummary)
        {
            StringAssert.StartsWith(stderr.ToString(), RunOnceProcessExitBoundary.FinalSummaryMarker);
            Assert.IsTrue(stderr.ToString().TrimEnd().Length <= RunOnceProcessExitBoundary.MaximumLength);
            Assert.IsFalse(stderr.ToString().TrimStart().StartsWith('{'));
        }
        else
        {
            Assert.AreEqual(string.Empty, stderr.ToString());
        }
    }

    [TestMethod]
    public void FailedFinalSummaryWriteIsBestEffortAndNeverRetriesOrSelectsCodeSix()
    {
        var calls = 0;
        int actual = RunOnceProcess.Run(
            DeploymentMode.RunOnce,
            [],
            TextWriter.Null,
            new ThrowingWriter(),
            (_, _, _, _, _, outcomes) =>
            {
                calls++;
                outcomes.Add(WorkerProcessExitFact.Busy());
                return Task.FromResult(WorkerProcessExitCodes.Busy);
            },
            _ => null);

        Assert.AreEqual(WorkerProcessExitCodes.Busy, actual);
        Assert.AreEqual(1, calls);
        Assert.AreNotEqual(WorkerProcessExitCodes.OutputTransportFailure, actual);
    }

    private static WorkerProcessExitFact Fact(int code)
    {
        return code switch
        {
            WorkerProcessExitCodes.Completed => WorkerProcessExitFact.Completed(),
            WorkerProcessExitCodes.Busy => WorkerProcessExitFact.Busy(),
            WorkerProcessExitCodes.ExecutorFailure => WorkerProcessExitFact.ExecutionFailure(),
            WorkerProcessExitCodes.InfrastructureFailure => WorkerProcessExitFact.CleanupInfrastructure(),
            WorkerProcessExitCodes.Cancelled => WorkerProcessExitFact.ShutdownCancelled(),
            _ => throw new ArgumentOutOfRangeException(nameof(code))
        };
    }

    private sealed class ThrowingWriter : StringWriter
    {
        public override void WriteLine(string? value) => throw new IOException("stderr unavailable");
    }
}
