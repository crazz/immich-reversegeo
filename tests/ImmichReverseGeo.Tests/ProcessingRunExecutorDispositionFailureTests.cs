using ImmichReverseGeo.Core.Processing;

namespace ImmichReverseGeo.Tests;

[TestClass]
[TestCategory("Change14")]
public class ProcessingRunExecutorDispositionFailureTests
{
    public static IEnumerable<object[]> Cases =>
    [
        ["ordinary-admin"],
        ["ordinary-airport"]
    ];

    [TestMethod]
    [DynamicData(nameof(Cases))]
    public async Task ExecuteAsync_OrdinaryPerAssetFailureMatrix_ReportsOneErrorOneFailedAndContinuesPeer(string caseId)
    {
        var failed = ExecutorFixture.Asset(1);
        var peer = ExecutorFixture.Asset(2);
        var fixture = new ExecutorFixture().EnableReporter().EnableCount(2).EnableSnapshots().EnablePages().EnableAdmin().EnableWrite();
        fixture.Config.Processing.MaxDegreeOfParallelism = 1;
        fixture.Config.Processing.BatchSize = 1;
        fixture.Config.Processing.UseAirportInfrastructure = caseId == "ordinary-airport";
        fixture.SetPages([failed], [peer], []);
        fixture.ResolveBehavior = (asset, session, token) =>
        {
            if (asset.Id == failed.Id && caseId == "ordinary-admin")
            {
                throw new InvalidOperationException("admin failure");
            }

            return Task.FromResult<ImmichReverseGeo.Web.Services.AdministrativeAreaResolution?>(
                ExecutorFixture.Resolution(new("Country", "State", asset.Id == peer.Id ? "PeerCity" : "FailedCity")));
        };
        fixture.AirportBehavior = (asset, token) => asset.Id == failed.Id
            ? Task.FromException<ImmichReverseGeo.Overture.Models.OvertureInfrastructureLookupDiagnostics>(new InvalidOperationException("airport failure"))
            : Task.FromResult(ExecutorFixture.EmptyAirport());

        var result = await fixture.Executor.ExecuteAsync(fixture.Request, fixture.Reporter, CancellationToken.None).WaitAsync(ExecutorFixture.Bound);
        fixture.AssertTerminal(result);
        fixture.Verify(caseId, result);

        ExecutorAssertions.Completed(result, 2, 1, 0, 1);
        Assert.AreEqual(peer.Id, fixture.Writes.Single().AssetId);
        var error = fixture.Reporter.Events.OfType<LogEmitted>().Single(item => item.Level == ProcessingLogLevel.Error);
        Assert.AreSame(fixture.Request, error.Request);
        StringAssert.Contains(error.Message, caseId == "ordinary-admin" ? "admin failure" : "airport failure");
        Assert.AreEqual(2, fixture.Reporter.Events.OfType<ProgressChanged>().Count());
        Assert.AreEqual(1, fixture.Logger.Entries.Count(item => item.Level == Microsoft.Extensions.Logging.LogLevel.Error));
        Assert.IsFalse(fixture.Writes.Any(item => item.AssetId == failed.Id));
        Assert.AreEqual(0, fixture.SkippedWrites.Count);
        var failedAccepted = fixture.Reporter.EventObservations.Single(item =>
            item.Event is ProgressChanged && item.AssetId == failed.Id).Sequence;
        var peerBatch = fixture.Calls.Single(item => item.Call is { Kind: ExecutorCallKind.Batch, Ordinal: 2 }).Sequence;
        Assert.IsTrue(failedAccepted < peerBatch, "Failed disposition must be accepted before the peer batch begins.");
    }
}
