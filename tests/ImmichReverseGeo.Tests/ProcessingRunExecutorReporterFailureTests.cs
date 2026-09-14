using ImmichReverseGeo.Core.Processing;

namespace ImmichReverseGeo.Tests;

[TestClass]
[TestCategory("Change14")]
public class ProcessingRunExecutorReporterFailureTests
{
    [TestMethod]
    public async Task ExecuteAsync_ResolverReporterFailure_PropagatesOriginalWithoutSourceOrFailedConversion()
    {
        var asset = ExecutorFixture.Asset(1);
        var failure = new TestSinkException("resolver-log-failure");
        var fixture = new ExecutorFixture().EnableReporter().EnableCount().EnableSnapshots().EnablePages();
        fixture.SetPages([asset], []);
        fixture.ResolveBehavior = async (current, session, token) =>
        {
            await session.ReportLogAsync(ProcessingLogLevel.Trace, "resolver-log", token);
            return ExecutorFixture.Resolution(new("Country", "State", "City"));
        };
        fixture.EventBehavior = (processingEvent, token) => processingEvent is LogEmitted { Message: "resolver-log" }
            ? ValueTask.FromException(failure)
            : ValueTask.CompletedTask;

        var observed = await Assert.ThrowsExactlyAsync<TestSinkException>(() =>
            fixture.Executor.ExecuteAsync(fixture.Request, fixture.Reporter, CancellationToken.None).WaitAsync(ExecutorFixture.Bound));

        Assert.AreSame(failure, observed);
        Assert.AreEqual(0, fixture.Writes.Count);
        Assert.AreEqual(0, fixture.Reporter.Events.OfType<ProgressChanged>().Count());
        Assert.AreEqual(0, fixture.Reporter.Attempts.OfType<RunFinished>().Count());
        Assert.AreEqual(0, fixture.Reporter.Events.OfType<LogEmitted>().Count(item => item.Level == ProcessingLogLevel.Error));
        fixture.Reporter.Correlation.Complete();
        fixture.Reporter.CompleteRejectedExceptions();
        Assert.AreEqual(0, fixture.Reporter.Correlation.PendingCount);
    }

    public static IEnumerable<object[]> OpenCases =>
    [
        ["open-start-ordinary"],
        ["open-start-oom"]
    ];

    [TestMethod]
    [DynamicData(nameof(OpenCases))]
    public async Task ExecuteAsync_OpenRunStartedReporterFailure_PropagatesOriginalWithoutSessionOrTerminalAttempt(string caseId)
    {
        var failure = caseId == "open-start-oom"
            ? (Exception)new OutOfMemoryException("reporter-open-start-oom")
            : new TestSinkException("reporter-open-start-failure");
        var fixture = new ExecutorFixture().EnableReporter();
        var reporter = new RecordingFaultReporter((processingEvent, token) =>
            processingEvent is RunStarted ? ValueTask.FromException(failure) : ValueTask.CompletedTask);
        fixture.Reporter = reporter;

        var observed = await CaptureAsync(() =>
            fixture.Executor.ExecuteAsync(fixture.Request, reporter, CancellationToken.None).WaitAsync(ExecutorFixture.Bound));
        Assert.AreEqual(failure.GetType(), observed.GetType());

        Assert.AreSame(failure, observed);
        fixture.Verify(caseId, null, observed, failure);
        Assert.AreEqual(1, reporter.Attempts.OfType<RunStarted>().Count());
        Assert.AreEqual(0, reporter.Events.Count);
        Assert.AreEqual(0, reporter.Attempts.OfType<RunFinished>().Count());
        Assert.AreEqual(0, fixture.CountCalls);
    }

    public static IEnumerable<object[]> MidstreamCases =>
    [
        ["reporter-eligibility"],
        ["reporter-log"],
        ["reporter-activity-start"],
        ["reporter-activity-end"],
        ["reporter-disposition"],
        ["reporter-cleanup"],
        ["reporter-midstream-oom"]
    ];

    [TestMethod]
    [DynamicData(nameof(MidstreamCases))]
    public async Task ExecuteAsync_MidstreamReporterFailureMatrix_BreaksSessionAndNeverAttemptsTerminalRecursion(string caseId)
    {
        var asset = ExecutorFixture.Asset(1);
        var failure = caseId == "reporter-midstream-oom"
            ? (Exception)new OutOfMemoryException("reporter-midstream-oom")
            : new TestSinkException($"{caseId}-failure");
        var fixture = new ExecutorFixture().EnableReporter().EnableCount();
        if (caseId != "reporter-eligibility")
        {
            fixture.EnableSnapshots().EnablePages();
            fixture.SetPages([asset], []);
        }
        if (caseId is "reporter-disposition" or "reporter-cleanup")
        {
            fixture.EnableWrite();
        }
        if (caseId is "reporter-activity-start" or "reporter-activity-end" or "reporter-cleanup" or "reporter-disposition")
        {
            fixture.ResolveBehavior = async (current, session, token) =>
            {
                if (caseId is "reporter-activity-start" or "reporter-activity-end" or "reporter-cleanup")
                {
                    var activity = await session.BeginActivityAsync("resolver-activity", token);
                    if (caseId == "reporter-activity-end")
                    {
                        await activity.DisposeAsync();
                    }
                }

                return ExecutorFixture.Resolution(new("Country", "State", "City"));
            };
        }
        fixture.EventBehavior = (processingEvent, token) => ShouldFail(caseId, processingEvent)
            ? ValueTask.FromException(failure)
            : ValueTask.CompletedTask;

        var observed = await CaptureAsync(() =>
            fixture.Executor.ExecuteAsync(fixture.Request, fixture.Reporter, CancellationToken.None).WaitAsync(ExecutorFixture.Bound));
        Assert.AreEqual(failure.GetType(), observed.GetType());

        Assert.AreSame(failure, observed);
        fixture.Verify(caseId, null, observed, failure);
        Assert.AreEqual(0, fixture.Reporter.Attempts.OfType<RunFinished>().Count());
        Assert.AreEqual(0, fixture.Reporter.Events.OfType<RunFinished>().Count());
        if (caseId == "reporter-disposition")
        {
            Assert.AreEqual(1, fixture.Writes.Count);
            Assert.AreEqual(0, fixture.Reporter.Events.OfType<ProgressChanged>().Count());
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_ReporterFailureAfterPersistence_RetainsEffectAndNeverCompensatesOrFinishes()
    {
        var asset = ExecutorFixture.Asset(1);
        var failure = new TestSinkException("disposition failure");
        var fixture = new ExecutorFixture().EnableReporter().EnableCount().EnableSnapshots().EnablePages().EnableAdmin().EnableWrite();
        fixture.SetPages([asset], []);
        fixture.EventBehavior = (processingEvent, token) => processingEvent is ProgressChanged
            ? ValueTask.FromException(failure)
            : ValueTask.CompletedTask;

        var observed = await Assert.ThrowsExactlyAsync<TestSinkException>(() =>
            fixture.Executor.ExecuteAsync(fixture.Request, fixture.Reporter, CancellationToken.None).WaitAsync(ExecutorFixture.Bound));

        Assert.AreSame(failure, observed);
        Assert.AreEqual(asset.Id, fixture.Writes.Single().AssetId);
        Assert.AreEqual(1, fixture.Reporter.Attempts.OfType<ProgressChanged>().Count());
        Assert.AreEqual(0, fixture.Reporter.Events.OfType<ProgressChanged>().Count());
        Assert.AreEqual(0, fixture.Reporter.Attempts.OfType<RunFinished>().Count());
        Assert.AreEqual(1, fixture.Writes.Count);
        fixture.Reporter.Correlation.Complete();
        fixture.Reporter.CompleteRejectedExceptions();
        Assert.AreEqual(0, fixture.Reporter.Correlation.PendingCount);
    }

    private static async Task<Exception> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            throw new AssertFailedException("Expected execution to throw.");
        }
        catch (AssertFailedException)
        {
            throw;
        }
        catch (Exception failure)
        {
            return failure;
        }
    }

    private static bool ShouldFail(string caseId, ProcessingEvent processingEvent)
    {
        return caseId switch
        {
            "reporter-eligibility" => processingEvent is EligibilityDetermined,
            "reporter-log" or "reporter-midstream-oom" => processingEvent is LogEmitted { Level: ProcessingLogLevel.Information },
            "reporter-activity-start" => processingEvent is ActivityStarted,
            "reporter-activity-end" or "reporter-cleanup" => processingEvent is ActivityEnded,
            "reporter-disposition" => processingEvent is ProgressChanged,
            _ => throw new AssertFailedException($"Unknown case {caseId}")
        };
    }
}
