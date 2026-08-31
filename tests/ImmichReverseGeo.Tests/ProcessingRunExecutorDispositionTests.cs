using ImmichReverseGeo.Core.Models;

namespace ImmichReverseGeo.Tests;

[TestClass]
[TestCategory("Change14")]
public class ProcessingRunExecutorDispositionTests
{
    [TestMethod]
    public async Task ExecuteAsync_MatchedLocationPreservesCity_WritesExactCityAndOnlyUpdated()
    {
        var asset = ExecutorFixture.Asset(1);
        var fixture = new ExecutorFixture().EnableReporter().EnableCount().EnableSnapshots().EnablePages().EnableAdmin().EnableWrite();
        fixture.SetPages([asset], []);
        fixture.ResolveBehavior = (current, session, token) =>
            Task.FromResult<ImmichReverseGeo.Web.Services.AdministrativeAreaResolution?>(
                ExecutorFixture.Resolution(new GeoResult("Country", "State", "City")));

        var result = await fixture.Executor.ExecuteAsync(fixture.Request, fixture.Reporter, CancellationToken.None).WaitAsync(ExecutorFixture.Bound);
        fixture.AssertTerminal(result);
        fixture.Verify("fallback-city", result);

        ExecutorAssertions.Completed(result, 1, 1, 0, 0);
        Assert.AreEqual((asset.Id, new GeoResult("Country", "State", "City")), fixture.Writes.Single());
        Assert.AreEqual(0, fixture.SkippedWrites.Count);
        Assert.AreEqual(0, fixture.AirportCalls.Count);
    }

    [TestMethod]
    public async Task ExecuteAsync_NoAdministrativeMatch_PersistsSkippedBeforeExactDisposition()
    {
        var asset = ExecutorFixture.Asset(1);
        var fixture = new ExecutorFixture().EnableReporter().EnableCount().EnableSnapshots().EnablePages().EnableAdmin().EnableSkippedInsert();
        fixture.SetPages([asset], []);
        fixture.ResolveBehavior = (current, session, token) =>
            Task.FromResult<ImmichReverseGeo.Web.Services.AdministrativeAreaResolution?>(
                ExecutorFixture.Resolution(new GeoResult(null, null, null)));

        var result = await fixture.Executor.ExecuteAsync(fixture.Request, fixture.Reporter, CancellationToken.None).WaitAsync(ExecutorFixture.Bound);
        fixture.AssertTerminal(result);
        fixture.Verify("no-administrative-match", result, runToken: CancellationToken.None);

        ExecutorAssertions.Completed(result, 1, 0, 1, 0);
        Assert.AreEqual(asset.Id, fixture.SkippedWrites.Single());
        Assert.AreEqual(0, fixture.Writes.Count);
    }

    public static IEnumerable<object[]> AirportCases =>
    [
        ["airport-containing-overrides"],
        ["airport-noncontaining-preserves-admin"],
        ["airport-noncontaining-fills-absent"]
    ];

    [TestMethod]
    [DynamicData(nameof(AirportCases))]
    public async Task ExecuteAsync_AirportSelectionRows_ApplyContainingPreserveAndFillPolicies(string caseId)
    {
        var asset = ExecutorFixture.Asset(1);
        var fixture = new ExecutorFixture().EnableReporter().EnableCount().EnableSnapshots().EnablePages().EnableAdmin().EnableWrite();
        fixture.Config.Processing.UseAirportInfrastructure = true;
        fixture.SetPages([asset], []);
        var adminCity = caseId == "airport-noncontaining-fills-absent" ? null : "AdminCity";
        var airportCity = caseId == "airport-containing-overrides" ? "ContainingAirport" : "NearbyAirport";
        var contains = caseId == "airport-containing-overrides";
        var expectedCity = caseId switch
        {
            "airport-containing-overrides" => "ContainingAirport",
            "airport-noncontaining-preserves-admin" => "AdminCity",
            "airport-noncontaining-fills-absent" => "NearbyAirport",
            _ => throw new AssertFailedException($"Unknown case {caseId}")
        };
        fixture.ResolveBehavior = (current, session, token) =>
            Task.FromResult<ImmichReverseGeo.Web.Services.AdministrativeAreaResolution?>(
                ExecutorFixture.Resolution(new GeoResult("Country", "State", adminCity)));
        fixture.AirportBehavior = (current, token) => Task.FromResult(ExecutorFixture.Airport(airportCity, contains));

        var result = await fixture.Executor.ExecuteAsync(fixture.Request, fixture.Reporter, CancellationToken.None).WaitAsync(ExecutorFixture.Bound);
        fixture.AssertTerminal(result);
        fixture.Verify(caseId, result);

        ExecutorAssertions.Completed(result, 1, 1, 0, 0);
        Assert.AreEqual(expectedCity, fixture.Writes.Single().Geo.City);
        Assert.AreEqual(1, fixture.AirportCalls.Count);
        var admin = fixture.Calls.Single(item => item.Call == new ExecutorCallContract(ExecutorCallKind.Admin, AssetId: asset.Id)).Sequence;
        var airport = fixture.Calls.Single(item => item.Call == new ExecutorCallContract(ExecutorCallKind.Airport, AssetId: asset.Id)).Sequence;
        var write = fixture.Calls.Single(item => item.Call == new ExecutorCallContract(ExecutorCallKind.WriteAttempt, AssetId: asset.Id)).Sequence;
        Assert.IsTrue(admin < airport && airport < write);
        Assert.AreEqual(0, fixture.SkippedWrites.Count);
    }
}
