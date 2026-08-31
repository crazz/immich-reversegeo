using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;

namespace ImmichReverseGeo.Tests;

[TestClass]
[TestCategory("Change14")]
public class ProcessingRunExecutorTerminalInvariantTests
{
    [TestMethod]
    public async Task ExecuteAsync_FixedUtcAndTerminalOrder_CloseActivitiesBeforeOneFinishThenReturnSameResult()
    {
        var asset = ExecutorFixture.Asset(1);
        var fixture = new ExecutorFixture().EnableReporter().EnableCount().EnableSnapshots().EnablePages().EnableAdmin().EnableWrite();
        fixture.SetPages([asset], []);
        fixture.ResolveBehavior = async (current, session, token) =>
        {
            await using var activity = await session.BeginActivityAsync("resolver-activity", token);
            return ExecutorFixture.Resolution(new("Country", "State", "City"));
        };

        var result = await fixture.Executor.ExecuteAsync(fixture.Request, fixture.Reporter, CancellationToken.None).WaitAsync(ExecutorFixture.Bound);
        fixture.AssertTerminal(result);
        fixture.Verify("fixed-utc-terminal-order", result, runToken: CancellationToken.None);

        ExecutorAssertions.Completed(result, 1, 1, 0, 0);
        Assert.AreEqual(FixedUtcTimeProvider.Start, result.StartedAtUtc);
        Assert.AreEqual(FixedUtcTimeProvider.End, result.EndedAtUtc);
        var events = fixture.Reporter.EventObservations.ToArray();
        var started = events.Single(item => item.Event is ActivityStarted).Sequence;
        var ended = events.Single(item => item.Event is ActivityEnded).Sequence;
        var finished = events.Single(item => item.Event is RunFinished).Sequence;
        Assert.IsTrue(started < ended && ended < finished);
        Assert.AreEqual(1, events.Count(item => item.Event is RunFinished));
        Assert.AreSame(result, ((RunFinished)events.Single(item => item.Event is RunFinished).Event).Result);
    }

    public static IEnumerable<object[]> Cases =>
    [
        ["terminal-cancelled-partial"],
        ["terminal-failed-partial"]
    ];

    [TestMethod]
    [DynamicData(nameof(Cases))]
    public async Task ExecuteAsync_TerminalInvariantRows_ValidateCancelledAndFailedPartialResults(string caseId)
    {
        var first = ExecutorFixture.Asset(1);
        var second = ExecutorFixture.Asset(2);
        using var cancellation = new CancellationTokenSource();
        var fixture = new ExecutorFixture().EnableReporter().EnableCount(2).EnableSnapshots().EnablePages().EnableAdmin().EnableWrite();
        fixture.Config.Processing.BatchSize = 1;
        fixture.Config.Processing.BatchDelayMs = caseId == "terminal-failed-partial" ? 11 : 0;
        fixture.SetPages([first], [second], []);
        fixture.EventBehavior = (processingEvent, token) =>
        {
            if (caseId == "terminal-cancelled-partial" && processingEvent is ProgressChanged)
            {
                cancellation.Cancel();
            }

            return ValueTask.CompletedTask;
        };
        if (caseId == "terminal-failed-partial")
        {
            fixture.DelayBehavior = (delay, token) => Task.FromException(new InvalidOperationException("pass failure"));
        }

        var result = await fixture.Executor.ExecuteAsync(fixture.Request, fixture.Reporter, cancellation.Token).WaitAsync(ExecutorFixture.Bound);
        fixture.AssertTerminal(result);
        fixture.Verify(caseId, result, runToken: cancellation.Token);

        ExecutorAssertions.Counts(result, 1, 1, 0, 0);
        Assert.AreSame(fixture.Request, result.Request);
        Assert.AreEqual(FixedUtcTimeProvider.Start, result.StartedAtUtc);
        Assert.AreEqual(FixedUtcTimeProvider.End, result.EndedAtUtc);
        if (caseId == "terminal-cancelled-partial")
        {
            Assert.AreEqual(ProcessingRunOutcome.Cancelled, result.Outcome);
            Assert.IsNull(result.FailureMessage);
        }
        else
        {
            Assert.AreEqual("terminal-failed-partial", caseId);
            Assert.AreEqual(ProcessingRunOutcome.Failed, result.Outcome);
            Assert.AreEqual("pass failure", result.FailureMessage);
            Assert.AreEqual(0, result.FailedCount);
        }

        Assert.AreEqual(1, fixture.Writes.Count);
        Assert.AreEqual(1, fixture.Reporter.Events.OfType<RunFinished>().Count());
        Assert.AreSame(result, fixture.Reporter.Events.OfType<RunFinished>().Single().Result);
    }
}
