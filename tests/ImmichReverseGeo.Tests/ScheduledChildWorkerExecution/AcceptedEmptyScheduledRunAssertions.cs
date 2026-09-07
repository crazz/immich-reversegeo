using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests.ScheduledChildWorkerExecution;

internal static class AcceptedEmptyScheduledRunAssertions
{
    internal static void AssertLocalEmptyOutcome(
        ProcessingState state,
        ProcessingRunCoordinator coordinator,
        string assertionPrefix)
    {
        Assert.AreEqual(0L, state.TotalUnprocessed, assertionPrefix + "-total-unprocessed");
        Assert.AreEqual(0, state.ProcessedThisRun, assertionPrefix + "-processed");
        Assert.AreEqual(0, state.SkippedThisRun, assertionPrefix + "-skipped");
        Assert.AreEqual(0, state.ErrorsThisRun, assertionPrefix + "-errors");
        Assert.IsNull(state.LastError, assertionPrefix + "-last-error");
        Assert.IsNull(state.CurrentActivity, assertionPrefix + "-activity");
        Assert.IsFalse(state.IsRunning, assertionPrefix + "-idle");
        Assert.IsNull(coordinator.ActiveRequest, assertionPrefix + "-active-request-released");
    }
}
