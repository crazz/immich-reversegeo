using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests.ScheduledChildWorkerExecution;

[TestClass]
[TestCategory("Change35")]
[TestCategory("Change58")]
public sealed class GateContractTests
{
    private static readonly TimeSpan _bound = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task DetectionReturnsFalseWhenNoEligibleAssetExists()
    {
        var gate = new ExistenceProcessingWorkDetector(_ => Task.FromResult(false));

        ProcessingWorkDetectionResult result = await gate.DetectAsync(ProcessingWorkDetectorStub.Request(), CancellationToken.None).WaitAsync(_bound);

        Assert.IsFalse(result.HasWork, "absent-eligibility-is-no-work");
    }

    [TestMethod]
    public async Task DetectionReturnsTrueWhenAnEligibleAssetExists()
    {
        var gate = new ExistenceProcessingWorkDetector(_ => Task.FromResult(true));

        ProcessingWorkDetectionResult result = await gate.DetectAsync(ProcessingWorkDetectorStub.Request(), CancellationToken.None).WaitAsync(_bound);

        Assert.IsTrue(result.HasWork, "present-eligibility-is-work");
    }

    [TestMethod]
    public async Task DetectionForwardsTheExactTokenAndProbesOnce()
    {
        using var source = new CancellationTokenSource();
        CancellationToken observedToken = default;
        int countCalls = 0;
        var gate = new ExistenceProcessingWorkDetector(token =>
        {
            observedToken = token;
            countCalls++;
            return Task.FromResult(true);
        });

        ProcessingWorkDetectionResult result = await gate.DetectAsync(ProcessingWorkDetectorStub.Request(), source.Token).WaitAsync(_bound);

        Assert.IsTrue(result.HasWork, "single-positive-existence-result");
        Assert.AreEqual(source.Token, observedToken, "exact-cancellation-token");
        Assert.AreEqual(1, countCalls, "one-existence-read-per-gate-invocation");
    }

    [TestMethod]
    public void ConstructorDoesNotInvokeTheExistenceOperation()
    {
        int countCalls = 0;

        _ = new ExistenceProcessingWorkDetector(_ =>
        {
            countCalls++;
            return Task.FromResult(true);
        });

        Assert.AreEqual(0, countCalls, "construction-does-not-query-eligibility");
    }

    [TestMethod]
    public async Task DetectionAwaitsTheExistenceOperation()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new ExistenceProcessingWorkDetector(_ => completion.Task);

        Task<ProcessingWorkDetectionResult> decision = gate.DetectAsync(ProcessingWorkDetectorStub.Request(), CancellationToken.None);
        try
        {
            Assert.IsFalse(decision.IsCompleted, "gate-awaits-pending-existence-read");
        }
        finally
        {
            completion.TrySetResult(true);
        }

        Assert.IsTrue((await decision.WaitAsync(_bound)).HasWork, "gate-maps-completed-positive-existence-read");
    }

    [TestMethod]
    public async Task DetectionPropagatesTheOriginalExistenceFailure()
    {
        var expected = new InvalidOperationException("existence-failure-sentinel");
        var gate = new ExistenceProcessingWorkDetector(_ => Task.FromException<bool>(expected));

        InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => gate.DetectAsync(ProcessingWorkDetectorStub.Request(), CancellationToken.None).WaitAsync(_bound));

        Assert.AreSame(expected, actual, "existence-failure-identity");
    }

    [TestMethod]
    public async Task DetectionPropagatesTheOriginalCancellationToken()
    {
        using var expectedSource = new CancellationTokenSource();
        expectedSource.Cancel();
        var gate = new ExistenceProcessingWorkDetector(
            _ => Task.FromCanceled<bool>(expectedSource.Token));

        OperationCanceledException actual = await Assert.ThrowsAsync<OperationCanceledException>(
            () => gate.DetectAsync(ProcessingWorkDetectorStub.Request(), CancellationToken.None).WaitAsync(_bound));

        Assert.AreEqual(expectedSource.Token, actual.CancellationToken, "existence-cancellation-token");
    }

    [TestMethod]
    public void ConstructorRejectsAMissingExistenceOperation()
    {
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new ExistenceProcessingWorkDetector(null!));
    }
}
