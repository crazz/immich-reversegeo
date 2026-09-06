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
        var gate = new CountBackedScheduledRunWorkGate(_ => Task.FromResult(0L));

        bool hasWork = await gate.HasWorkAsync(CancellationToken.None).WaitAsync(_bound);

        Assert.IsFalse(hasWork, "empty-eligibility-is-no-work");
    }

    [TestMethod]
    public async Task HasWorkAsync_ReturnsTrueForAPositiveEligibilityCountAsync()
    {
        var gate = new CountBackedScheduledRunWorkGate(_ => Task.FromResult(7L));

        bool hasWork = await gate.HasWorkAsync(CancellationToken.None).WaitAsync(_bound);

        Assert.IsTrue(hasWork, "positive-eligibility-is-work");
    }

    [TestMethod]
    public async Task HasWorkAsync_ForwardsTheExactTokenAndCallsTheCountOnceAsync()
    {
        using var source = new CancellationTokenSource();
        CancellationToken observedToken = default;
        int countCalls = 0;
        var gate = new CountBackedScheduledRunWorkGate(token =>
        {
            observedToken = token;
            countCalls++;
            return Task.FromResult(1L);
        });

        bool hasWork = await gate.HasWorkAsync(source.Token).WaitAsync(_bound);

        Assert.IsTrue(hasWork, "single-positive-count-result");
        Assert.AreEqual(source.Token, observedToken, "exact-cancellation-token");
        Assert.AreEqual(1, countCalls, "one-count-call-per-gate-invocation");
    }

    [TestMethod]
    public void Constructor_DoesNotInvokeTheCountOperation()
    {
        int countCalls = 0;

        _ = new CountBackedScheduledRunWorkGate(_ =>
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
        var gate = new CountBackedScheduledRunWorkGate(_ => completion.Task);

        Task<bool> decision = gate.HasWorkAsync(CancellationToken.None);
        try
        {
            Assert.IsFalse(decision.IsCompleted, "gate-awaits-pending-count");
        }
        finally
        {
            completion.TrySetResult(1L);
        }

        Assert.IsTrue(await decision.WaitAsync(_bound), "gate-maps-completed-positive-count");
    }

    [TestMethod]
    public async Task HasWorkAsync_PropagatesTheOriginalCountFailureAsync()
    {
        var expected = new InvalidOperationException("count-failure-sentinel");
        var gate = new CountBackedScheduledRunWorkGate(_ => Task.FromException<long>(expected));

        InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => gate.HasWorkAsync(CancellationToken.None).WaitAsync(_bound));

        Assert.AreSame(expected, actual, "count-failure-identity");
    }

    [TestMethod]
    public async Task HasWorkAsync_PropagatesTheOriginalCancellationTokenAsync()
    {
        using var expectedSource = new CancellationTokenSource();
        expectedSource.Cancel();
        var gate = new CountBackedScheduledRunWorkGate(
            _ => Task.FromCanceled<long>(expectedSource.Token));

        OperationCanceledException actual = await Assert.ThrowsAsync<OperationCanceledException>(
            () => gate.HasWorkAsync(CancellationToken.None).WaitAsync(_bound));

        Assert.AreEqual(expectedSource.Token, actual.CancellationToken, "count-cancellation-token");
    }

    [TestMethod]
    public void Constructor_RejectsAMissingCountOperation()
    {
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new CountBackedScheduledRunWorkGate(null!));
    }
}
