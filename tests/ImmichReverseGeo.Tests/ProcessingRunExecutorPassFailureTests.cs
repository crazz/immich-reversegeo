using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Overture.Models;

namespace ImmichReverseGeo.Tests;

[TestClass]
[TestCategory("Change14")]
public class ProcessingRunExecutorPassFailureTests
{
    public static IEnumerable<object[]> PassCases =>
    [
        ["pass-count"],
        ["pass-skipped-snapshot"],
        ["pass-config"],
        ["pass-batch"],
        ["pass-delay"]
    ];

    [TestMethod]
    [DynamicData(nameof(PassCases))]
    public async Task ExecuteAsync_PassLevelFailureMatrix_FinishesFailedOnceWithoutFatalAssetIncrement(string caseId)
    {
        var asset = ExecutorFixture.Asset(1);
        var fixture = new ExecutorFixture().EnableReporter();
        var message = $"{caseId}-failure";
        if (caseId != "pass-count")
        {
            fixture.EnableCount();
        }
        if (caseId is not ("pass-count" or "pass-skipped-snapshot"))
        {
            fixture.EnableSnapshots();
        }
        if (caseId is "pass-batch" or "pass-delay")
        {
            fixture.EnablePages();
        }
        if (caseId == "pass-delay")
        {
            fixture.EnableAdmin().EnableWrite();
        }
        fixture.SetPages([asset], []);
        if (caseId == "pass-count")
        {
            fixture.CountBehavior = token => Task.FromException<long>(new InvalidOperationException(message));
        }
        else if (caseId == "pass-skipped-snapshot")
        {
            fixture.SkippedBehavior = () => Task.FromException<HashSet<Guid>>(new InvalidOperationException(message));
        }
        else if (caseId == "pass-config")
        {
            fixture.ConfigBehavior = () => Task.FromException<AppConfig>(new InvalidOperationException(message));
        }
        else if (caseId == "pass-batch")
        {
            fixture.BatchBehavior = (cursor, size, call, token) => Task.FromException<List<AssetRecord>>(new InvalidOperationException(message));
        }
        else
        {
            Assert.AreEqual("pass-delay", caseId);
            fixture.Config.Processing.BatchDelayMs = 11;
            fixture.DelayBehavior = (delay, token) => Task.FromException(new InvalidOperationException(message));
        }

        var result = await fixture.Executor.ExecuteAsync(fixture.Request, fixture.Reporter, CancellationToken.None).WaitAsync(ExecutorFixture.Bound);
        fixture.AssertTerminal(result);
        fixture.Verify(caseId, result);

        Assert.AreEqual(ProcessingRunOutcome.Failed, result.Outcome);
        Assert.AreEqual(message, result.FailureMessage);
        ExecutorAssertions.Counts(result, caseId == "pass-delay" ? 1 : 0, caseId == "pass-delay" ? 1 : 0, 0, 0);
        Assert.AreEqual(1, fixture.Logger.Entries.Count(item => item.Level == Microsoft.Extensions.Logging.LogLevel.Error && item.Message == "Fatal error during processing run"));
        Assert.AreEqual(1, fixture.Reporter.Events.OfType<RunFinished>().Count());
        if (caseId != "pass-delay")
        {
            Assert.AreEqual(0, fixture.Writes.Count);
        }
    }

    public static IEnumerable<object[]> OomCases =>
    [
        ["oom-update"],
        ["oom-skipped-insert"],
        ["oom-admin"],
        ["oom-airport"],
        ["oom-count"],
        ["oom-skipped-snapshot"],
        ["oom-configuration"],
        ["oom-batch"],
        ["oom-delay"]
    ];

    [TestMethod]
    [DynamicData(nameof(OomCases))]
    public async Task ExecuteAsync_NonReporterOutOfMemoryMatrix_FinishesFailedWithoutOrdinaryDispositionAndRetainsEffects(string caseId)
    {
        var prior = ExecutorFixture.Asset(1);
        var failed = ExecutorFixture.Asset(2);
        var hasPrior = caseId is "oom-update" or "oom-skipped-insert" or "oom-admin" or "oom-airport" or "oom-delay";
        var fixture = new ExecutorFixture().EnableReporter();
        if (caseId != "oom-count")
        {
            fixture.EnableCount(hasPrior ? 2 : 1);
        }
        if (caseId is not ("oom-count" or "oom-skipped-snapshot"))
        {
            fixture.EnableSnapshots();
        }
        if (caseId is "oom-batch" or "oom-update" or "oom-skipped-insert" or "oom-admin" or "oom-airport" or "oom-delay")
        {
            fixture.EnablePages();
        }
        if (hasPrior)
        {
            fixture.EnableAdmin().EnableWrite();
        }
        fixture.Config.Processing.MaxDegreeOfParallelism = 1;
        fixture.Config.Processing.BatchSize = 1;
        fixture.Config.Processing.UseAirportInfrastructure = caseId == "oom-airport";
        fixture.Config.Processing.BatchDelayMs = caseId == "oom-delay" ? 11 : 0;
        if (hasPrior)
        {
            fixture.SetPages([prior], [failed], []);
        }
        else
        {
            fixture.SetPages([failed], []);
        }
        var message = caseId;
        if (caseId == "oom-count")
        {
            fixture.CountBehavior = token => Task.FromException<long>(new OutOfMemoryException(message));
        }
        else if (caseId == "oom-skipped-snapshot")
        {
            fixture.SkippedBehavior = () => Task.FromException<HashSet<Guid>>(new OutOfMemoryException(message));
        }
        else if (caseId == "oom-configuration")
        {
            fixture.ConfigBehavior = () => Task.FromException<AppConfig>(new OutOfMemoryException(message));
        }
        else if (caseId == "oom-batch")
        {
            fixture.BatchBehavior = (cursor, size, call, token) => Task.FromException<List<AssetRecord>>(new OutOfMemoryException(message));
        }
        fixture.ResolveBehavior = (asset, session, token) =>
        {
            if (asset.Id == failed.Id && caseId == "oom-admin")
            {
                throw new OutOfMemoryException(message);
            }

            if (asset.Id == failed.Id && caseId == "oom-skipped-insert")
            {
                return Task.FromResult<ImmichReverseGeo.Web.Services.AdministrativeAreaResolution?>(null);
            }

            return Task.FromResult<ImmichReverseGeo.Web.Services.AdministrativeAreaResolution?>(ExecutorFixture.Resolution(new("Country", "State", $"City-{asset.Latitude}")));
        };
        fixture.AirportBehavior = (asset, token) => asset.Id == failed.Id
            ? Task.FromException<OvertureInfrastructureLookupDiagnostics>(new OutOfMemoryException(message))
            : Task.FromResult(ExecutorFixture.EmptyAirport());
        fixture.WriteBehavior = (assetId, geo, token) => assetId == failed.Id && caseId == "oom-update"
            ? Task.FromException(new OutOfMemoryException(message))
            : Task.CompletedTask;
        fixture.AddSkippedBehavior = assetId => Task.FromException(new OutOfMemoryException(message));
        fixture.DelayBehavior = (delay, token) => Task.FromException(new OutOfMemoryException(message));

        var result = await fixture.Executor.ExecuteAsync(fixture.Request, fixture.Reporter, CancellationToken.None).WaitAsync(ExecutorFixture.Bound);
        fixture.AssertTerminal(result);
        fixture.Verify(caseId, result);

        Assert.AreEqual(ProcessingRunOutcome.Failed, result.Outcome);
        Assert.AreEqual(message, result.FailureMessage);
        ExecutorAssertions.Counts(result, hasPrior ? 1 : 0, hasPrior ? 1 : 0, 0, 0);
        Assert.AreEqual(0, fixture.Reporter.Events.OfType<LogEmitted>().Count(item => item.Level == ProcessingLogLevel.Error));
        Assert.AreEqual(0, fixture.Reporter.Events.OfType<ProgressChanged>().Count(item => item.Progress.FailedCount > 0));
        Assert.AreEqual(hasPrior ? 1 : 0, fixture.Writes.Count);
        Assert.AreEqual(1, fixture.Logger.Entries.Count(item => item.Level == Microsoft.Extensions.Logging.LogLevel.Error));
        if (hasPrior)
        {
            var priorAccepted = fixture.Reporter.EventObservations.Single(item =>
                item.Event is ProgressChanged { Progress.UpdatedCount: 1 }).Sequence;
            var fatalBoundary = caseId == "oom-delay"
                ? fixture.Calls.Single(item => item.Call.Kind == ExecutorCallKind.Delay).Sequence
                : fixture.Calls.Single(item => item.Call is { Kind: ExecutorCallKind.Batch, Ordinal: 2 }).Sequence;
            var finished = fixture.Reporter.EventObservations.Single(item => item.Event is RunFinished).Sequence;
            Assert.IsTrue(priorAccepted < fatalBoundary && fatalBoundary < finished, "Prior effect acceptance must precede the fatal OOM boundary and terminal.");
        }
    }
}
