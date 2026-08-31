using System.Reflection;
using System.Runtime.ExceptionServices;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ProcessingStateEventReporterTerminalRecoveryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 1, 0, 0, TimeSpan.Zero);

    [TestMethod]
    [DataRow(ProcessingRunOutcome.Completed, 1)]
    [DataRow(ProcessingRunOutcome.Completed, 2)]
    [DataRow(ProcessingRunOutcome.Completed, 3)]
    [DataRow(ProcessingRunOutcome.Completed, 4)]
    [DataRow(ProcessingRunOutcome.Cancelled, 1)]
    [DataRow(ProcessingRunOutcome.Cancelled, 2)]
    [DataRow(ProcessingRunOutcome.Cancelled, 3)]
    [DataRow(ProcessingRunOutcome.Cancelled, 4)]
    [DataRow(ProcessingRunOutcome.Cancelled, 5)]
    [DataRow(ProcessingRunOutcome.Failed, 1)]
    [DataRow(ProcessingRunOutcome.Failed, 2)]
    [DataRow(ProcessingRunOutcome.Failed, 3)]
    [DataRow(ProcessingRunOutcome.Failed, 4)]
    [DataRow(ProcessingRunOutcome.Failed, 5)]
    [DataRow(ProcessingRunOutcome.Failed, 6)]
    public async Task TerminalMutationCallbackFailure_RecoversEveryOrderedMutationReleasesArmAndRethrowsOriginal(
        ProcessingRunOutcome outcome,
        int failurePosition)
    {
        var state = new ProcessingState();
        var reporter = new ProcessingStateEventReporter(state);
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual);
        state.MarkPending();
        Assert.IsTrue(reporter.Arm(request));
        var session = await reporter.OpenRunAsync(request, Now);
        await session.DetermineEligibilityAsync(2);
        var firstActivity = await session.BeginActivityAsync("first");
        var secondActivity = await session.BeginActivityAsync("second");
        var failure = new InvalidOperationException($"terminal callback {outcome} {failurePosition}");
        var callback = new ThrowOnExactCall(failurePosition, failure);
        state.OnChanged += callback.Invoke;
        var result = Result(request, outcome);

        var observed = Assert.ThrowsExactly<InvalidOperationException>(() => InvokeTerminal(reporter, result));

        Assert.AreSame(failure, observed);
        Assert.AreEqual(outcome switch
        {
            ProcessingRunOutcome.Completed => 5,
            ProcessingRunOutcome.Cancelled => 6,
            ProcessingRunOutcome.Failed => failurePosition == 1 ? 6 : 7,
            _ => throw new AssertFailedException("Unexpected outcome")
        }, callback.Calls);
        Assert.IsFalse(state.IsRunning);
        Assert.IsNull(state.CurrentActivity);
        Assert.AreEqual($"Fatal: {failure.Message}", state.LastError);
        Assert.IsNotNull(state.LastRunCompleted);
        var messages = Messages(state);
        Assert.AreEqual(1, messages.Count(message => message.StartsWith("Run complete. Processed=", StringComparison.Ordinal)));
        if (outcome == ProcessingRunOutcome.Cancelled)
        {
            Assert.AreEqual(1, messages.Count(message => message == "Run cancelled."));
        }
        if (outcome == ProcessingRunOutcome.Failed)
        {
            Assert.AreEqual(1, messages.Count(message => message == "[ERROR] Fatal: domain failure"));
        }

        InvokeTerminal(reporter, result);
        Assert.AreEqual(1, Messages(state).Count(message => message.StartsWith("Run complete. Processed=", StringComparison.Ordinal)));
        var next = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Scheduled);
        Assert.IsTrue(reporter.Arm(next), "Terminal recovery must release exact reporter ownership.");
        await firstActivity.DisposeAsync();
        await secondActivity.DisposeAsync();
    }

    private static void InvokeTerminal(ProcessingStateEventReporter reporter, ProcessingRunResult result)
    {
        var method = typeof(ProcessingStateEventReporter).GetMethod("ProjectTerminal", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new AssertFailedException("ProjectTerminal was not found.");
        try
        {
            method.Invoke(reporter, [result]);
        }
        catch (TargetInvocationException wrapper) when (wrapper.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(wrapper.InnerException).Throw();
        }
    }

    private static ProcessingRunResult Result(ProcessingRunRequest request, ProcessingRunOutcome outcome)
    {
        return new ProcessingRunResult(
            request,
            Now,
            Now,
            0,
            0,
            0,
            0,
            outcome,
            outcome == ProcessingRunOutcome.Failed ? "domain failure" : null);
    }

    private static string[] Messages(ProcessingState state)
    {
        return state.GetRecentLog()
            .Select(line => line[(line.IndexOf("] ", StringComparison.Ordinal) + 2)..])
            .ToArray();
    }

    private sealed class ThrowOnExactCall(int throwOnCall, Exception failure)
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public void Invoke()
        {
            if (Interlocked.Increment(ref _calls) == throwOnCall)
            {
                throw failure;
            }
        }
    }
}
