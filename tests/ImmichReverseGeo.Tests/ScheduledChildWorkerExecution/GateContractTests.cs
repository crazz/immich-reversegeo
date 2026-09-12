using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests.ScheduledChildWorkerExecution;

[TestClass]
[TestCategory("Change35")]
public sealed class GateContractTests
{
    private static readonly TimeSpan _bound = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task HasWorkAsync_ReturnsFalseForAnEmptyEligibilityCountAsync()
    {
        var gate = new CountBackedProcessingWorkDetector(_ => Task.FromResult(0L));

        ProcessingWorkDetectionResult result = await gate.DetectAsync(ProcessingWorkDetectorStub.Request(), CancellationToken.None).WaitAsync(_bound);

        Assert.IsFalse(result.HasWork, "empty-eligibility-is-no-work");
    }

    [TestMethod]
    public async Task HasWorkAsync_ReturnsTrueForAPositiveEligibilityCountAsync()
    {
        var gate = new CountBackedProcessingWorkDetector(_ => Task.FromResult(7L));

        ProcessingWorkDetectionResult result = await gate.DetectAsync(ProcessingWorkDetectorStub.Request(), CancellationToken.None).WaitAsync(_bound);

        Assert.IsTrue(result.HasWork, "positive-eligibility-is-work");
    }

    [TestMethod]
    public async Task HasWorkAsync_ForwardsTheExactTokenAndCallsTheCountOnceAsync()
    {
        using var source = new CancellationTokenSource();
        CancellationToken observedToken = default;
        int countCalls = 0;
        var gate = new CountBackedProcessingWorkDetector(token =>
        {
            observedToken = token;
            countCalls++;
            return Task.FromResult(1L);
        });

        ProcessingWorkDetectionResult result = await gate.DetectAsync(ProcessingWorkDetectorStub.Request(), source.Token).WaitAsync(_bound);

        Assert.IsTrue(result.HasWork, "single-positive-count-result");
        Assert.AreEqual(source.Token, observedToken, "exact-cancellation-token");
        Assert.AreEqual(1, countCalls, "one-count-call-per-gate-invocation");
    }

    [TestMethod]
    public void Constructor_DoesNotInvokeTheCountOperation()
    {
        int countCalls = 0;

        _ = new CountBackedProcessingWorkDetector(_ =>
        {
            countCalls++;
            return Task.FromResult(1L);
        });

        Assert.AreEqual(0, countCalls, "construction-does-not-query-eligibility");
    }

    [TestMethod]
    public async Task HasWorkAsync_AwaitsTheCountOperationAsync()
    {
        var completion = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new CountBackedProcessingWorkDetector(_ => completion.Task);

        Task<ProcessingWorkDetectionResult> decision = gate.DetectAsync(ProcessingWorkDetectorStub.Request(), CancellationToken.None);
        try
        {
            Assert.IsFalse(decision.IsCompleted, "gate-awaits-pending-count");
        }
        finally
        {
            completion.TrySetResult(1L);
        }

        Assert.IsTrue((await decision.WaitAsync(_bound)).HasWork, "gate-maps-completed-positive-count");
    }

    [TestMethod]
    public async Task HasWorkAsync_PropagatesTheOriginalCountFailureAsync()
    {
        var expected = new InvalidOperationException("count-failure-sentinel");
        var gate = new CountBackedProcessingWorkDetector(_ => Task.FromException<long>(expected));

        InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => gate.DetectAsync(ProcessingWorkDetectorStub.Request(), CancellationToken.None).WaitAsync(_bound));

        Assert.AreSame(expected, actual, "count-failure-identity");
    }

    [TestMethod]
    public async Task HasWorkAsync_PropagatesTheOriginalCancellationTokenAsync()
    {
        using var expectedSource = new CancellationTokenSource();
        expectedSource.Cancel();
        var gate = new CountBackedProcessingWorkDetector(
            _ => Task.FromCanceled<long>(expectedSource.Token));

        OperationCanceledException actual = await Assert.ThrowsAsync<OperationCanceledException>(
            () => gate.DetectAsync(ProcessingWorkDetectorStub.Request(), CancellationToken.None).WaitAsync(_bound));

        Assert.AreEqual(expectedSource.Token, actual.CancellationToken, "count-cancellation-token");
    }

    [TestMethod]
    public void Constructor_RejectsAMissingCountOperation()
    {
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new CountBackedProcessingWorkDetector(null!));
    }
}
