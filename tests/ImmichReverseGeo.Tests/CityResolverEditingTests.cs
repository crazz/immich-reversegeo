using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using static ImmichReverseGeo.Tests.WebStatusRenderingTests;

#pragma warning disable BL0006

namespace ImmichReverseGeo.Tests;

[TestClass]
public sealed class CityResolverEditingTests
{
    [TestMethod]
    public async Task FranceSubtypeOnly_SaveAndReloadRetainsInheritedTieBreak()
    {
        using var fixture = AdminConsoleCityMatchingFixture.Create();
        var config = new AppConfig();
        config.Processing.CityResolver.CountryOverrides["FRA"] = new CityResolverProfile
        {
            PreferredSubtypes = ["county", "localadmin"],
            TieBreakMode = string.Empty
        };
        await fixture.CreateConfigService().SeedConfigAsync(config);
        await using var renderer = new ComponentRenderer();
        await renderer.AttachAsync(fixture.CreatePage());

        await ClickAsync(renderer, "Save City Resolver Settings");

        var saved = (await fixture.CreateConfigService().GetConfigAsync()).Processing.CityResolver;
        Assert.AreEqual(string.Empty, saved.CountryOverrides["FRA"].TieBreakMode);
        CollectionAssert.AreEqual(new[] { "county", "localadmin" }, saved.CountryOverrides["FRA"].PreferredSubtypes);
        Assert.AreEqual(CityResolverTieBreakModes.LargestArea, fixture.CreateCatalog().GetProfile(saved, "FRA").TieBreakMode);
        await using var reloaded = new ComponentRenderer();
        await reloaded.AttachAsync(fixture.CreatePage());
        Assert.AreEqual("Inherit", SelectedTieBreak(await reloaded.ReadAsync(), "country-tie-break-FRA"));
        StringAssert.Contains((await reloaded.ReadAsync()).ElementTextByCssClass("resolver-country-summary"), "Prefer broader area");
    }

    [TestMethod]
    public async Task FranceTighterOnly_BoundSelectionKeepsSubtypeOrderInherited()
    {
        using var fixture = AdminConsoleCityMatchingFixture.Create();
        await using var renderer = new ComponentRenderer();
        await renderer.AttachAsync(fixture.CreatePage());
        await ChangeAsync(renderer, "input", 1, "FRA");
        await ClickAsync(renderer, "Add Country Override");
        await ChangeTieBreakAsync(renderer, "country-tie-break-FRA", "Tighter");
        StringAssert.Contains((await renderer.ReadAsync()).ElementTextByCssClass("resolver-country-summary"), "Prefer tighter area");

        await ClickAsync(renderer, "Save City Resolver Settings");

        var saved = (await fixture.CreateConfigService().GetConfigAsync()).Processing.CityResolver;
        Assert.AreEqual(0, saved.CountryOverrides["FRA"].PreferredSubtypes.Count);
        Assert.AreEqual(CityResolverTieBreakModes.SmallestArea, saved.CountryOverrides["FRA"].TieBreakMode);
        var effective = fixture.CreateCatalog().GetProfile(saved, "FRA");
        CollectionAssert.AreEqual(fixture.CreateCatalog().GetProfile(null, "FRA").PreferredSubtypes, effective.PreferredSubtypes);
        Assert.AreEqual(CityResolverTieBreakModes.SmallestArea, effective.TieBreakMode);
        await using var reloaded = new ComponentRenderer();
        await reloaded.AttachAsync(fixture.CreatePage());
        Assert.AreEqual("Tighter", SelectedTieBreak(await reloaded.ReadAsync(), "country-tie-break-FRA"));
        StringAssert.Contains((await reloaded.ReadAsync()).ElementTextByCssClass("resolver-country-summary"), "Prefer tighter area");
    }

    [TestMethod]
    public async Task BroaderOnly_SaveAndReloadKeepsSubtypeOrderInherited()
    {
        using var fixture = AdminConsoleCityMatchingFixture.Create();
        Assert.AreEqual(CityResolverTieBreakModes.SmallestArea, fixture.CreateCatalog().GetProfile(null, "USA").TieBreakMode);
        await using var renderer = new ComponentRenderer();
        await renderer.AttachAsync(fixture.CreatePage());
        await ChangeAsync(renderer, "input", 1, "USA");
        await ClickAsync(renderer, "Add Country Override");
        await ChangeTieBreakAsync(renderer, "country-tie-break-USA", "Broader");
        StringAssert.Contains((await renderer.ReadAsync()).ElementTextByCssClass("resolver-country-summary"), "Prefer broader area");

        await ClickAsync(renderer, "Save City Resolver Settings");

        var saved = (await fixture.CreateConfigService().GetConfigAsync()).Processing.CityResolver;
        Assert.AreEqual(0, saved.CountryOverrides["USA"].PreferredSubtypes.Count);
        Assert.AreEqual(CityResolverTieBreakModes.LargestArea, saved.CountryOverrides["USA"].TieBreakMode);
        var effective = fixture.CreateCatalog().GetProfile(saved, "USA");
        CollectionAssert.AreEqual(fixture.CreateCatalog().GetProfile(null, "USA").PreferredSubtypes, effective.PreferredSubtypes);
        Assert.AreEqual(CityResolverTieBreakModes.LargestArea, effective.TieBreakMode);
        await using var reloaded = new ComponentRenderer();
        await reloaded.AttachAsync(fixture.CreatePage());
        Assert.AreEqual("Broader", SelectedTieBreak(await reloaded.ReadAsync(), "country-tie-break-USA"));
        StringAssert.Contains((await reloaded.ReadAsync()).ElementTextByCssClass("resolver-country-summary"), "Prefer broader area");
    }

    [TestMethod]
    public async Task ExplicitPreference_ReturnsToInheritanceThroughBoundControl()
    {
        using var fixture = AdminConsoleCityMatchingFixture.Create();
        var config = new AppConfig();
        config.Processing.CityResolver.CountryOverrides["FRA"] = new CityResolverProfile
        {
            PreferredSubtypes = ["county"],
            TieBreakMode = CityResolverTieBreakModes.SmallestArea
        };
        await fixture.CreateConfigService().SeedConfigAsync(config);
        await using var renderer = new ComponentRenderer();
        await renderer.AttachAsync(fixture.CreatePage());
        Assert.AreEqual("Tighter", SelectedTieBreak(await renderer.ReadAsync(), "country-tie-break-FRA"));

        await ChangeTieBreakAsync(renderer, "country-tie-break-FRA", "Inherit");
        StringAssert.Contains((await renderer.ReadAsync()).ElementTextByCssClass("resolver-country-summary"), "Prefer broader area");
        await ClickAsync(renderer, "Save City Resolver Settings");

        var saved = (await fixture.CreateConfigService().GetConfigAsync()).Processing.CityResolver;
        Assert.AreEqual(string.Empty, saved.CountryOverrides["FRA"].TieBreakMode);
        CollectionAssert.AreEqual(new[] { "county" }, saved.CountryOverrides["FRA"].PreferredSubtypes);
        Assert.AreEqual(CityResolverTieBreakModes.LargestArea, fixture.CreateCatalog().GetProfile(saved, "FRA").TieBreakMode);
        await using var reloaded = new ComponentRenderer();
        await reloaded.AttachAsync(fixture.CreatePage());
        Assert.AreEqual("Inherit", SelectedTieBreak(await reloaded.ReadAsync(), "country-tie-break-FRA"));
        StringAssert.Contains((await reloaded.ReadAsync()).ElementTextByCssClass("resolver-country-summary"), "Prefer broader area");
    }

    [TestMethod]
    public async Task GlobalSubtypeOnly_DoesNotReplaceBundledCountryTieBreaks()
    {
        using var fixture = AdminConsoleCityMatchingFixture.Create();
        await using var renderer = new ComponentRenderer();
        await renderer.AttachAsync(fixture.CreatePage());
        await ChangeAsync(renderer, "input", 0, true);
        await ChangeAsync(renderer, "select", 1, "county");
        await ClickAsync(renderer, "Add");
        await ChangeAsync(renderer, "input", 1, "FRA");
        await ClickAsync(renderer, "Add Country Override");

        await ClickAsync(renderer, "Save City Resolver Settings");

        var saved = (await fixture.CreateConfigService().GetConfigAsync()).Processing.CityResolver;
        Assert.AreEqual(string.Empty, saved.DefaultProfile.TieBreakMode);
        CollectionAssert.AreEqual(new[] { "county" }, saved.DefaultProfile.PreferredSubtypes);
        var catalog = fixture.CreateCatalog();
        Assert.AreEqual(CityResolverTieBreakModes.LargestArea, catalog.GetProfile(saved, "FRA").TieBreakMode);
        Assert.AreEqual(CityResolverTieBreakModes.SmallestArea, catalog.GetProfile(saved, "USA").TieBreakMode);
        StringAssert.Contains((await renderer.ReadAsync()).ElementTextByCssClass("resolver-country-summary"), "Prefer broader area");
        await using var reloaded = new ComponentRenderer();
        await reloaded.AttachAsync(fixture.CreatePage());
        Assert.AreEqual("Inherit", SelectedTieBreak(await reloaded.ReadAsync(), "global-tie-break"));
        StringAssert.Contains((await reloaded.ReadAsync()).ElementTextByCssClass("resolver-country-summary"), "Prefer broader area");
        await ClickAsync(reloaded, "Save City Resolver Settings");
        Assert.AreEqual(string.Empty, (await fixture.CreateConfigService().GetConfigAsync()).Processing.CityResolver.DefaultProfile.TieBreakMode);
    }

    [TestMethod]
    public async Task ProfilePrecedence_ExplicitFieldsWinAndOmittedFieldsInherit()
    {
        using var fixture = AdminConsoleCityMatchingFixture.Create();
        await using var renderer = new ComponentRenderer();
        await renderer.AttachAsync(fixture.CreatePage());
        await ChangeAsync(renderer, "input", 0, true);
        await ChangeTieBreakAsync(renderer, "global-tie-break", "Tighter");
        await ChangeAsync(renderer, "select", 1, "county");
        await ClickAsync(renderer, "Add");
        await ChangeAsync(renderer, "input", 1, "FRA");
        await ClickAsync(renderer, "Add Country Override");
        await ChangeTieBreakAsync(renderer, "country-tie-break-FRA", "Broader");

        await ClickAsync(renderer, "Save City Resolver Settings");

        var saved = (await fixture.CreateConfigService().GetConfigAsync()).Processing.CityResolver;
        var catalog = fixture.CreateCatalog();
        Assert.AreEqual(CityResolverTieBreakModes.LargestArea, catalog.GetProfile(saved, "FRA").TieBreakMode);
        CollectionAssert.AreEqual(new[] { "county" }, catalog.GetProfile(saved, "FRA").PreferredSubtypes);
        Assert.AreEqual(0, saved.CountryOverrides["FRA"].PreferredSubtypes.Count);
        StringAssert.Contains((await renderer.ReadAsync()).ElementTextByCssClass("resolver-country-summary"), "Prefer broader area");
        await using var reloaded = new ComponentRenderer();
        await reloaded.AttachAsync(fixture.CreatePage());
        Assert.AreEqual("Tighter", SelectedTieBreak(await reloaded.ReadAsync(), "global-tie-break"));
        Assert.AreEqual("Broader", SelectedTieBreak(await reloaded.ReadAsync(), "country-tie-break-FRA"));

        await ChangeTieBreakAsync(reloaded, "country-tie-break-FRA", "Inherit");
        await ClickAsync(reloaded, "Save City Resolver Settings");
        saved = (await fixture.CreateConfigService().GetConfigAsync()).Processing.CityResolver;
        Assert.AreEqual(CityResolverTieBreakModes.SmallestArea, catalog.GetProfile(saved, "FRA").TieBreakMode);
        CollectionAssert.AreEqual(new[] { "county" }, catalog.GetProfile(saved, "FRA").PreferredSubtypes);
        StringAssert.Contains((await reloaded.ReadAsync()).ElementTextByCssClass("resolver-country-summary"), "Prefer tighter area");

        await ChangeTieBreakAsync(reloaded, "global-tie-break", "Inherit");
        await ClickAsync(reloaded, "Remove");
        await ClickAsync(reloaded, "Save City Resolver Settings");
        saved = (await fixture.CreateConfigService().GetConfigAsync()).Processing.CityResolver;
        Assert.AreEqual(string.Empty, saved.DefaultProfile.TieBreakMode);
        Assert.AreEqual(0, saved.DefaultProfile.PreferredSubtypes.Count);
        Assert.AreEqual(CityResolverTieBreakModes.LargestArea, catalog.GetProfile(saved, "FRA").TieBreakMode);
        CollectionAssert.AreEqual(catalog.GetProfile(null, "FRA").PreferredSubtypes, catalog.GetProfile(saved, "FRA").PreferredSubtypes);
        Assert.AreEqual(CityResolverTieBreakModes.SmallestArea, catalog.GetProfile(saved, "USA").TieBreakMode);
        CollectionAssert.AreEqual(catalog.GetProfile(null, null).PreferredSubtypes, catalog.GetProfile(saved, "USA").PreferredSubtypes);
        await using var inherited = new ComponentRenderer();
        await inherited.AttachAsync(fixture.CreatePage());
        Assert.AreEqual("Inherit", SelectedTieBreak(await inherited.ReadAsync(), "country-tie-break-FRA"));
        StringAssert.Contains((await inherited.ReadAsync()).ElementTextByCssClass("resolver-country-summary"), "Prefer broader area");
    }

    [TestMethod]
    public async Task TieBreakControls_ExposeNamedInheritedTighterAndBroaderChoices()
    {
        using var fixture = AdminConsoleCityMatchingFixture.Create();
        await using var renderer = new ComponentRenderer();
        await renderer.AttachAsync(fixture.CreatePage());
        await ChangeAsync(renderer, "input", 0, true);
        await ChangeAsync(renderer, "input", 1, "FRA");
        await ClickAsync(renderer, "Add Country Override");
        var rendered = await renderer.ReadAsync();
        Assert.IsTrue(rendered.HasAttribute("id", "global-tie-break"));
        Assert.IsTrue(rendered.Text.Contains("for=\"global-tie-break\"", StringComparison.Ordinal)
            || rendered.HasAttribute("for", "global-tie-break"));
        Assert.IsTrue(rendered.HasAttribute("id", "country-tie-break-FRA"));
        Assert.IsTrue(rendered.HasAttribute("for", "country-tie-break-FRA"));
        foreach (int ordinal in new[] { 0, 2 })
        {
            int index = ElementIndices(rendered, "select").ElementAt(ordinal);
            string options = AdminConsoleChromeTestHelpers.ElementText(rendered.Frames, index, rendered.Frames[index].ElementSubtreeLength);
            StringAssert.Contains(options, "Inherit");
            StringAssert.Contains(options, "Prefer tighter area");
            StringAssert.Contains(options, "Prefer broader area");
            Assert.AreEqual("Inherit", SelectedValue(rendered, ordinal));
        }

        await ChangeTieBreakAsync(renderer, "global-tie-break", "Broader");
        await ClickAsync(renderer, "Save City Resolver Settings");

        var saved = (await fixture.CreateConfigService().GetConfigAsync()).Processing.CityResolver;
        Assert.AreEqual(CityResolverTieBreakModes.LargestArea, saved.DefaultProfile.TieBreakMode);
        Assert.AreEqual(0, saved.DefaultProfile.PreferredSubtypes.Count);
        Assert.AreEqual(string.Empty, saved.CountryOverrides["FRA"].TieBreakMode);
        Assert.AreEqual(CityResolverTieBreakModes.LargestArea, fixture.CreateCatalog().GetProfile(saved, "USA").TieBreakMode);
        await using var reloaded = new ComponentRenderer();
        await reloaded.AttachAsync(fixture.CreatePage());
        Assert.AreEqual("Broader", SelectedTieBreak(await reloaded.ReadAsync(), "global-tie-break"));
        Assert.AreEqual("Inherit", SelectedTieBreak(await reloaded.ReadAsync(), "country-tie-break-FRA"));
        StringAssert.Contains((await reloaded.ReadAsync()).ElementTextByCssClass("resolver-country-summary"), "Prefer broader area");
    }

    [TestMethod]
    public async Task SavedPartials_LookupProtocolAndProcessingCatalogAgree()
    {
        using var fixture = AdminConsoleCityMatchingFixture.Create();
        var config = new AppConfig();
        config.Processing.CityResolver.DefaultProfile.PreferredSubtypes = ["county"];
        config.Processing.CityResolver.CountryOverrides["FRA"] = new CityResolverProfile
        {
            PreferredSubtypes = ["localadmin", "locality"],
            TieBreakMode = string.Empty
        };
        config.Processing.CityResolver.CountryOverrides["USA"] = new CityResolverProfile
        {
            PreferredSubtypes = [],
            TieBreakMode = CityResolverTieBreakModes.SmallestArea
        };
        config.Processing.CityResolver.CountryOverrides["CAN"] = new CityResolverProfile
        {
            PreferredSubtypes = [],
            TieBreakMode = CityResolverTieBreakModes.LargestArea
        };
        await fixture.CreateConfigService().SeedConfigAsync(config);
        await using var renderer = new ComponentRenderer();
        await renderer.AttachAsync(fixture.CreatePage());
        await ClickAsync(renderer, "Save City Resolver Settings");
        var saved = (await fixture.CreateConfigService().GetConfigAsync()).Processing.CityResolver;
        var snapshot = CoordinateLookupCityProfileConversions.Snapshot(saved);
        var request = new CoordinateLookupRequest(48.8566, 2.3522, true, false, false, snapshot);
        var message = new WorkerJobControllerMessage(
            WorkerJobProtocolV2.RequestCategory,
            WorkerJobProtocolV2.ExecuteType,
            1,
            DateTimeOffset.Parse("2026-09-26T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            WorkerJobKind.CoordinateLookup,
            new CoordinateLookupExecutePayload(request));

        var parsed = WorkerJobProtocolCodec.ParseControllerInput(WorkerJobProtocolCodec.SerializeControllerInput(message));

        Assert.IsTrue(parsed.IsSuccess);
        var payload = Assert.IsInstanceOfType<CoordinateLookupExecutePayload>(parsed.Message!.Payload);
        var decoded = CoordinateLookupCityProfileConversions.ToConfig(payload.Request.CityResolverOverrides);
        Assert.IsNotNull(snapshot.DefaultProfile);
        Assert.IsNull(snapshot.DefaultProfile.TieBreak);
        Assert.IsNull(snapshot.CountryProfiles.Single(country => country.CountryCode == "FRA").Profile.TieBreak);
        CollectionAssert.AreEqual(new[] { "CAN", "FRA", "USA" }, snapshot.CountryProfiles.Select(country => country.CountryCode).ToArray());
        var catalog = fixture.CreateCatalog();
        foreach (string iso3 in new[] { "FRA", "USA", "CAN" })
        {
            var processing = catalog.GetProfile(saved, iso3);
            var lookup = catalog.GetProfile(decoded, iso3);
            Assert.AreEqual(processing.TieBreakMode, lookup.TieBreakMode, iso3);
            CollectionAssert.AreEqual(processing.PreferredSubtypes, lookup.PreferredSubtypes, iso3);
            Assert.AreEqual(saved.CountryOverrides[iso3].TieBreakMode, decoded.CountryOverrides[iso3].TieBreakMode);
            CollectionAssert.AreEqual(saved.CountryOverrides[iso3].PreferredSubtypes, decoded.CountryOverrides[iso3].PreferredSubtypes);
        }

        saved.DefaultProfile.PreferredSubtypes.Clear();
        saved.CountryOverrides["FRA"].PreferredSubtypes.Clear();
        decoded.CountryOverrides["FRA"].PreferredSubtypes.Clear();
        CollectionAssert.AreEqual(new[] { "county" }, snapshot.DefaultProfile.PreferredSubtypes.ToArray());
        CollectionAssert.AreEqual(new[] { "localadmin", "locality" }, snapshot.CountryProfiles.Single(country => country.CountryCode == "FRA").Profile.PreferredSubtypes.ToArray());
    }

    private static int TieBreakIndex(RenderSnapshot snapshot, string id)
    {
        return ElementIndices(snapshot, "select").Single(index =>
            Attributes(snapshot, index).Any(frame => frame.AttributeName == "id" && Equals(frame.AttributeValue, id)));
    }

    private static string SelectedTieBreak(RenderSnapshot snapshot, string id)
    {
        return Attributes(snapshot, TieBreakIndex(snapshot, id))
            .Single(frame => frame.AttributeName == "value").AttributeValue?.ToString() ?? string.Empty;
    }

    private static async Task ChangeTieBreakAsync(ComponentRenderer renderer, string id, string value)
    {
        var snapshot = await renderer.ReadAsync();
        ulong handler = Attributes(snapshot, TieBreakIndex(snapshot, id))
            .Single(frame => frame.AttributeName == "onchange").AttributeEventHandlerId;
        await renderer.Dispatcher.InvokeAsync(() => renderer.DispatchEventAsync(handler, null, new ChangeEventArgs { Value = value }));
    }

    private static async Task ChangeAsync(ComponentRenderer renderer, string element, int ordinal, object value)
    {
        var snapshot = await renderer.ReadAsync();
        int index = ElementIndices(snapshot, element).ElementAt(ordinal);
        ulong handler = Attributes(snapshot, index).Single(frame => frame.AttributeName == "onchange").AttributeEventHandlerId;
        await renderer.Dispatcher.InvokeAsync(() => renderer.DispatchEventAsync(handler, null, new ChangeEventArgs { Value = value }));
    }

    private static string SelectedValue(RenderSnapshot snapshot, int selectIndex)
    {
        int index = ElementIndices(snapshot, "select").ElementAt(selectIndex);
        return Attributes(snapshot, index).First(frame => frame.AttributeName == "value").AttributeValue?.ToString() ?? string.Empty;
    }

    private static IEnumerable<int> ElementIndices(RenderSnapshot snapshot, string element)
    {
        return Enumerable.Range(0, snapshot.Frames.Length)
            .Where(index => snapshot.Frames[index].FrameType == RenderTreeFrameType.Element
                && snapshot.Frames[index].ElementName == element);
    }

    private static IEnumerable<RenderTreeFrame> Attributes(RenderSnapshot snapshot, int index)
    {
        return snapshot.Frames.Skip(index + 1).TakeWhile(frame => frame.FrameType == RenderTreeFrameType.Attribute);
    }

    private static async Task ClickAsync(ComponentRenderer renderer, string label)
    {
        var snapshot = await renderer.ReadAsync();
        int index = ElementIndices(snapshot, "button").First(index =>
            AdminConsoleChromeTestHelpers.ElementText(snapshot.Frames, index, snapshot.Frames[index].ElementSubtreeLength).Trim() == label);
        ulong handler = Attributes(snapshot, index).Single(frame => frame.AttributeName == "onclick").AttributeEventHandlerId;
        await renderer.Dispatcher.InvokeAsync(() => renderer.DispatchEventAsync(handler, null, new MouseEventArgs()));
    }
}
