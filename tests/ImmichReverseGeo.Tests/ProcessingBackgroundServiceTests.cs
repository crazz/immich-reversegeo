using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class ProcessingBackgroundServiceTests
{
    [TestMethod]
    public async Task RunOnceAsync_WhenExactEligibilityCountIsZero_CompletesWithoutFurtherOperations()
    {
        var state = new ProcessingState();
        var countCompletion = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var countCalls = 0;

        Task<long> GetUnprocessedCountAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            countCalls++;
            return countCompletion.Task;
        }

        var operations = new ProcessingBackgroundService.ProcessingOperations(
            GetUnprocessedCountAsync,
            () => throw UnexpectedOperation("configuration read"),
            () => throw UnexpectedOperation("skipped-record loading"),
            (_, _, _) => throw UnexpectedOperation("batch retrieval"),
            (_, _, _, _, _) => throw UnexpectedOperation("administrative-area resolution"),
            (_, _, _, _) => throw UnexpectedOperation("airport lookup"),
            _ => throw UnexpectedOperation("skipped-record write"),
            (_, _, _) => throw UnexpectedOperation("location write"));

        var passTask = ProcessingBackgroundService.RunOnceAsync(
            NullLogger<ProcessingBackgroundService>.Instance,
            state,
            operations,
            CancellationToken.None);

        Assert.AreEqual(1, countCalls);
        Assert.IsFalse(passTask.IsCompleted, "The processing pass must await the exact-count operation.");

        countCompletion.SetResult(0);
        await passTask;

        Assert.AreEqual(1, countCalls);
        Assert.AreEqual(0, state.TotalUnprocessed);
        Assert.AreEqual(0, state.ProcessedThisRun);
        Assert.AreEqual(0, state.SkippedThisRun);
        Assert.AreEqual(0, state.ErrorsThisRun);
        Assert.IsFalse(state.IsRunning);
        Assert.IsNull(state.LastError);
        Assert.IsNotNull(state.LastRunStarted);
        Assert.IsNotNull(state.LastRunCompleted);

        const string NothingToProcessMessage =
            "Run started — nothing to process, all assets already have location data.";
        const string CompletionMessage = "Run complete. Processed=0 Skipped=0 Errors=0";
        var log = state.GetRecentLog();
        var nothingToProcessIndex = FindMessageIndex(log, NothingToProcessMessage);
        var completionIndex = FindMessageIndex(log, CompletionMessage);

        Assert.IsTrue(nothingToProcessIndex >= 0, "The nothing-to-process message was not logged.");
        Assert.IsTrue(completionIndex >= 0, "The zero-count completion summary was not logged.");
        Assert.IsTrue(
            nothingToProcessIndex < completionIndex,
            "The nothing-to-process message must precede the completion summary.");
    }

    private static AssertFailedException UnexpectedOperation(string operation)
    {
        return new AssertFailedException($"Unexpected {operation} after a zero eligibility count.");
    }

    private static int FindMessageIndex(IReadOnlyList<string> log, string message)
    {
        for (var index = 0; index < log.Count; index++)
        {
            if (log[index].EndsWith(message, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }
}
