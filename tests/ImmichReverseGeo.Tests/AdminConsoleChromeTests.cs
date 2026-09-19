using ImmichReverseGeo.Core.ApplicationRole;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerFailureRecovery;
using System.Reflection;
using System.Text.RegularExpressions;

#pragma warning disable BL0006

namespace ImmichReverseGeo.Tests;

[TestClass]
[DoNotParallelize]
public sealed class AdminConsoleChromeTests
{
    [TestMethod]
    public async Task Operator_ScansTheRailOnAWideScreen()
    {
        var status = new ProcessAssetsWebStatus(DeploymentMode.Standard);
        var navigation = WebStatusRenderingTests.CreateNavigation(status);
        await using var renderer = new WebStatusRenderingTests.ComponentRenderer();
        await renderer.AttachAsync(navigation);

        WebStatusRenderingTests.RenderSnapshot rendered = await renderer.ReadFlattenedAsync();
        string[] sectionTitles = AdminConsoleChromeTestHelpers.SectionTitles(rendered);
        string[] destinations = AdminConsoleChromeTestHelpers.DestinationLabels(rendered);

        CollectionAssert.AreEqual(
            new[] { "Work", "Configure", "Library" },
            sectionTitles);
        CollectionAssert.AreEqual(
            new[]
            {
                "Overview",
                "Lookup",
                "Logs",
                "Settings",
                "City matching",
                "Area caches",
                "Reset locations",
                "Skip list"
            },
            destinations);
        Assert.IsFalse(rendered.Text.Contains("Data Management", StringComparison.Ordinal));
        Assert.IsFalse(destinations.Contains("Data", StringComparer.Ordinal));
        Assert.IsTrue(AdminConsoleChromeTestHelpers.HasNavHref(rendered, "/"));
        Assert.IsTrue(AdminConsoleChromeTestHelpers.HasNavHref(rendered, "/lookup"));
        Assert.IsTrue(AdminConsoleChromeTestHelpers.HasNavHref(rendered, "/logs"));
        Assert.IsTrue(AdminConsoleChromeTestHelpers.HasNavHref(rendered, "/settings"));
        Assert.IsTrue(AdminConsoleChromeTestHelpers.HasNavHref(rendered, "/city-resolver"));
        Assert.IsTrue(AdminConsoleChromeTestHelpers.HasNavHref(rendered, "/data/geoboundaries"));
        Assert.IsTrue(AdminConsoleChromeTestHelpers.HasNavHref(rendered, "/data/reset-geo-data"));
        Assert.IsTrue(AdminConsoleChromeTestHelpers.HasNavHref(rendered, "/data/skip-list"));
        Assert.IsFalse(AdminConsoleChromeTestHelpers.HasNavHref(rendered, "/data"));
    }

    [TestMethod]
    public async Task Operator_OpensCityMatching()
    {
        using var fixture = AdminConsoleCityMatchingFixture.Create();
        var page = fixture.CreatePage();
        await using var renderer = new WebStatusRenderingTests.ComponentRenderer();
        await renderer.AttachAsync(page);

        WebStatusRenderingTests.RenderSnapshot rendered = await renderer.ReadFlattenedAsync();
        Assert.AreEqual("City matching", AdminConsoleChromeTestHelpers.PageHeading(rendered));
        StringAssert.Contains(rendered.Text, "Bundled Defaults");
        StringAssert.Contains(rendered.Text, "Save City Resolver Settings");
        StringAssert.Contains(rendered.Text, "Open Lookup");
        StringAssert.Contains(rendered.Text, "href=\"/lookup\"");
    }

    [TestMethod]
    public async Task Settings_NoLongerNestsCityMatching()
    {
        await using var settings = await AdminConsoleSettingsPageFixture.CreateAsync();
        WebStatusRenderingTests.RenderSnapshot settingsRendered = await settings.ReadAsync();
        Assert.IsFalse(
            settingsRendered.Text.Contains("Open City Resolver", StringComparison.Ordinal),
            "City matching must not launch from a nested Settings card");
        Assert.IsFalse(
            settingsRendered.Text.Contains("<h3>City Resolver</h3>", StringComparison.Ordinal)
                || settingsRendered.Text.Contains("City Resolver</h3>", StringComparison.Ordinal),
            "City matching must not nest under Settings");

        var status = new ProcessAssetsWebStatus(DeploymentMode.Standard);
        var navigation = WebStatusRenderingTests.CreateNavigation(status);
        await using var navRenderer = new WebStatusRenderingTests.ComponentRenderer();
        await navRenderer.AttachAsync(navigation);
        WebStatusRenderingTests.RenderSnapshot navRendered = await navRenderer.ReadFlattenedAsync();
        CollectionAssert.Contains(AdminConsoleChromeTestHelpers.DestinationLabels(navRendered), "City matching");
        CollectionAssert.AreEqual(
            new[] { "Work", "Configure", "Library" },
            AdminConsoleChromeTestHelpers.SectionTitles(navRendered));
        Assert.IsTrue(AdminConsoleChromeTestHelpers.HasNavHref(navRendered, "/city-resolver"));
    }

    [TestMethod]
    public async Task Worker_IsIdle_HeaderShowsSummaryAndOverviewKeepsDetail()
    {
        var status = new ProcessAssetsWebStatus(DeploymentMode.Standard);
        var state = new ProcessingState();
        var header = WebStatusRenderingTests.CreateTopBar(status);
        var overview = WebStatusRenderingTests.CreateDashboard(status, state);
        await using var headerRenderer = new WebStatusRenderingTests.ComponentRenderer();
        await using var overviewRenderer = new WebStatusRenderingTests.ComponentRenderer();
        await headerRenderer.AttachAsync(header);
        await overviewRenderer.AttachAsync(overview);

        WebStatusRenderingTests.RenderSnapshot headerRendered = await headerRenderer.ReadFlattenedAsync();
        WebStatusRenderingTests.RenderSnapshot overviewRendered = await overviewRenderer.ReadAsync();

        StringAssert.Contains(
            headerRendered.ElementTextByCssClass("top-bar-worker-status"),
            "Worker: Idle");
        Assert.IsFalse(
            headerRendered.Text.Contains("Service Status", StringComparison.Ordinal),
            "header must not duplicate the full Service Status card");
        Assert.IsFalse(headerRendered.Text.Contains("Deployment mode", StringComparison.Ordinal));
        StringAssert.Contains(overviewRendered.Text, "Service Status");
        StringAssert.Contains(overviewRendered.Text, "Deployment mode");
        StringAssert.Contains(overviewRendered.Text, "Worker: Idle");
        StringAssert.Contains(overviewRendered.Text, "Open Logs");
    }

    [TestMethod]
    public async Task Worker_IsRunning_HeaderShowsRunningSummaryAndOverviewKeepsDetail()
    {
        var status = new ProcessAssetsWebStatus(DeploymentMode.Standard);
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual);
        var sink = (IProcessAssetsWorkerStatusSink)status;
        sink.Admit(request, cancellationAlreadyWon: false);
        sink.ObserveTransport(request, WorkerRunTransportPhase.Accepted);

        var header = WebStatusRenderingTests.CreateTopBar(status);
        var overview = WebStatusRenderingTests.CreateDashboard(status, new ProcessingState());
        await using var headerRenderer = new WebStatusRenderingTests.ComponentRenderer();
        await using var overviewRenderer = new WebStatusRenderingTests.ComponentRenderer();
        await headerRenderer.AttachAsync(header);
        await overviewRenderer.AttachAsync(overview);

        WebStatusRenderingTests.RenderSnapshot headerRendered = await headerRenderer.ReadAsync();
        WebStatusRenderingTests.RenderSnapshot overviewRendered = await overviewRenderer.ReadAsync();

        StringAssert.Contains(
            headerRendered.ElementTextByCssClass("top-bar-worker-status"),
            "Worker: Running");
        Assert.IsFalse(headerRendered.Text.Contains("Service Status", StringComparison.Ordinal));
        StringAssert.Contains(overviewRendered.Text, "Service Status");
        StringAssert.Contains(overviewRendered.Text, "Worker: Running");
    }

    [TestMethod]
    public async Task Worker_Failed_HeaderShowsFailedSummaryAndOverviewKeepsFailureContent()
    {
        var status = new ProcessAssetsWebStatus(DeploymentMode.Standard);
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual);
        var sink = (IProcessAssetsWorkerStatusSink)status;
        sink.Admit(request, cancellationAlreadyWon: false);
        sink.ObserveTransport(request, WorkerRunTransportPhase.Accepted);
        sink.ObserveFinality(request, ProcessingRunOutcome.Failed, WorkerRunFailureCategory.Crash);

        var header = WebStatusRenderingTests.CreateTopBar(status);
        var overview = WebStatusRenderingTests.CreateDashboard(status, new ProcessingState());
        await using var headerRenderer = new WebStatusRenderingTests.ComponentRenderer();
        await using var overviewRenderer = new WebStatusRenderingTests.ComponentRenderer();
        await headerRenderer.AttachAsync(header);
        await overviewRenderer.AttachAsync(overview);

        WebStatusRenderingTests.RenderSnapshot headerRendered = await headerRenderer.ReadAsync();
        WebStatusRenderingTests.RenderSnapshot overviewRendered = await overviewRenderer.ReadAsync();

        StringAssert.Contains(
            headerRendered.ElementTextByCssClass("top-bar-worker-status"),
            "Worker: Failed");
        StringAssert.Contains(overviewRendered.Text, "Service Status");
        StringAssert.Contains(overviewRendered.Text, "ProcessAssets worker failed.");
        StringAssert.Contains(overviewRendered.Text, "Open Logs");
    }

    [TestMethod]
    public async Task Page_WithPrimaryAction_UsesDestinationTitleInPageHeader()
    {
        var status = new ProcessAssetsWebStatus(DeploymentMode.Standard);
        var overview = WebStatusRenderingTests.CreateDashboard(status, new ProcessingState());
        await using var overviewRenderer = new WebStatusRenderingTests.ComponentRenderer();
        await overviewRenderer.AttachAsync(overview);
        WebStatusRenderingTests.RenderSnapshot overviewRendered = await overviewRenderer.ReadAsync();
        Assert.AreEqual("Overview", AdminConsoleChromeTestHelpers.PageHeading(overviewRendered));
        AdminConsoleChromeTestHelpers.AssertPageHeaderContains(overviewRendered, "Run Now");

        var lookup = new StaticLookupPage();
        await using var lookupRenderer = new WebStatusRenderingTests.ComponentRenderer();
        await lookupRenderer.AttachAsync(lookup);
        WebStatusRenderingTests.RenderSnapshot lookupRendered = await lookupRenderer.ReadAsync();
        Assert.AreEqual("Lookup", AdminConsoleChromeTestHelpers.PageHeading(lookupRendered));
        AdminConsoleChromeTestHelpers.AssertPageHeaderContains(lookupRendered, "Lookup");

        using var cityMatching = AdminConsoleCityMatchingFixture.Create();
        var cityPage = cityMatching.CreatePage();
        await using var cityRenderer = new WebStatusRenderingTests.ComponentRenderer();
        await cityRenderer.AttachAsync(cityPage);
        WebStatusRenderingTests.RenderSnapshot cityRendered = await cityRenderer.ReadFlattenedAsync();
        Assert.AreEqual("City matching", AdminConsoleChromeTestHelpers.PageHeading(cityRendered));
        AdminConsoleChromeTestHelpers.AssertPageHeaderContains(cityRendered, "Save City Resolver Settings");

        await using var settings = await AdminConsoleSettingsPageFixture.CreateAsync();
        WebStatusRenderingTests.RenderSnapshot settingsRendered = await settings.ReadAsync();
        Assert.AreEqual("Settings", AdminConsoleChromeTestHelpers.PageHeading(settingsRendered));
        AdminConsoleChromeTestHelpers.AssertPageHeaderContains(settingsRendered, "Save All Settings");

        var logs = new ImmichReverseGeo.Web.Components.Pages.Logs();
        typeof(ImmichReverseGeo.Web.Components.Pages.Logs)
            .GetProperty("State", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(logs, new ProcessingState());
        await using var logsRenderer = new WebStatusRenderingTests.ComponentRenderer();
        await logsRenderer.AttachAsync(logs);
        WebStatusRenderingTests.RenderSnapshot logsRendered = await logsRenderer.ReadAsync();
        Assert.AreEqual("Logs", AdminConsoleChromeTestHelpers.PageHeading(logsRendered));
        AdminConsoleChromeTestHelpers.AssertPageHeaderContains(logsRendered, "Download");

        var areaCaches = new StaticGeoBoundariesPage();
        await using var areaRenderer = new WebStatusRenderingTests.ComponentRenderer();
        await areaRenderer.AttachAsync(areaCaches);
        Assert.AreEqual(
            "Area caches",
            AdminConsoleChromeTestHelpers.PageHeading(await areaRenderer.ReadAsync()));

        var reset = new StaticResetLocationsPage();
        await using var resetRenderer = new WebStatusRenderingTests.ComponentRenderer();
        await resetRenderer.AttachAsync(reset);
        Assert.AreEqual(
            "Reset locations",
            AdminConsoleChromeTestHelpers.PageHeading(await resetRenderer.ReadAsync()));

        var skipList = new ImmichReverseGeo.Web.Components.Pages.SkipList();
        await using var skipRenderer = new WebStatusRenderingTests.ComponentRenderer();
        await skipRenderer.AttachAsync(skipList);
        WebStatusRenderingTests.RenderSnapshot skipRendered = await skipRenderer.ReadAsync();
        Assert.AreEqual("Skip list", AdminConsoleChromeTestHelpers.PageHeading(skipRendered));
        AdminConsoleChromeTestHelpers.AssertPageHeaderContains(skipRendered, "Clear Skip List");
    }

    [TestMethod]
    public async Task PhoneWidthConsole_StacksRailWithoutMenuButton()
    {
        const int phoneWidthCssPx = AdminConsoleChromeTestHelpers.PhoneWidthCssPx;

        string css = AdminConsoleChromeTestHelpers.ReadAppCss();
        string activeNarrowCss = AdminConsoleChromeTestHelpers.CssRulesMatchingMaxWidth(css, phoneWidthCssPx);
        Assert.IsFalse(
            string.IsNullOrWhiteSpace(activeNarrowCss),
            $"no @media (max-width: …) rule matches {phoneWidthCssPx} CSS px");
        Assert.IsTrue(
            Regex.IsMatch(
                activeNarrowCss,
                @"\.page\s*\{[^}]*grid-template-areas:\s*""topbar""\s*""rail""\s*""main""",
                RegexOptions.Singleline),
            "at 390 CSS px the page grid must stack topbar, rail, then main");
        Assert.IsTrue(
            Regex.IsMatch(
                activeNarrowCss,
                @"\.nav-section\s*\{[^}]*grid-template-columns:\s*repeat\(2,\s*minmax\(0,\s*1fr\)\)",
                RegexOptions.Singleline),
            "at 390 CSS px multi-column nav groups must reflow to two columns");
        Assert.IsFalse(activeNarrowCss.Contains("nav-toggle", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(activeNarrowCss.Contains("menu-button", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(activeNarrowCss.Contains("hamburger", StringComparison.OrdinalIgnoreCase));

        var status = new ProcessAssetsWebStatus(DeploymentMode.Standard);
        var chrome = WebStatusRenderingTests.CreateTopBar(status);
        var navigation = WebStatusRenderingTests.CreateNavigation(status);
        await using var chromeRenderer = new WebStatusRenderingTests.ComponentRenderer();
        await using var navRenderer = new WebStatusRenderingTests.ComponentRenderer();
        await chromeRenderer.AttachAsync(chrome);
        await navRenderer.AttachAsync(navigation);
        WebStatusRenderingTests.RenderSnapshot chromeRendered = await chromeRenderer.ReadFlattenedAsync();
        WebStatusRenderingTests.RenderSnapshot navRendered = await navRenderer.ReadFlattenedAsync();

        Assert.IsTrue(chromeRendered.HasCssClass("top-bar"));
        Assert.IsTrue(chromeRendered.HasCssClass("sidebar-shell"));
        Assert.IsTrue(chromeRendered.HasCssClass("main-shell"));
        // Footer markup is emitted as RenderTree Markup frames (entities / long copy),
        // so assert by content rather than a discrete class attribute frame.
        StringAssert.Contains(chromeRendered.Text, "class=\"attribution\"");
        StringAssert.Contains(chromeRendered.Text, "Overture Maps");
        StringAssert.Contains(chromeRendered.Text, "Data sources");
        Assert.IsFalse(AdminConsoleChromeTestHelpers.HasMenuOrHamburgerControl(chromeRendered));
        Assert.IsFalse(AdminConsoleChromeTestHelpers.HasMenuOrHamburgerControl(navRendered));

        string[] destinations = AdminConsoleChromeTestHelpers.DestinationLabels(navRendered);
        CollectionAssert.AreEqual(
            new[]
            {
                "Overview",
                "Lookup",
                "Logs",
                "Settings",
                "City matching",
                "Area caches",
                "Reset locations",
                "Skip list"
            },
            destinations);
        CollectionAssert.AreEqual(
            new[] { "Work", "Configure", "Library" },
            AdminConsoleChromeTestHelpers.SectionTitles(navRendered));
        Assert.IsFalse(
            destinations.Any(label => label.Contains("menu", StringComparison.OrdinalIgnoreCase)
                || label.Contains("hamburger", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task KeyboardOperator_TraversesOverviewInReadingOrder()
    {
        var status = new ProcessAssetsWebStatus(DeploymentMode.Standard);
        await using var composed = await AdminConsoleChromeTestHelpers
            .RenderOverviewInsideMainLayoutAsync(status);
        WebStatusRenderingTests.RenderSnapshot rendered = await composed.ReadFlattenedAsync();

        // ComponentRenderer cannot Tab a real browser. Closest faithful equivalent:
        // render MainLayout with Overview as @Body (InteractiveServer is neutralized in
        // ComponentRenderer so page types can host as static children), then assert
        // focusables in that single composed tree follow top bar → rail → page header →
        // body → footer.
        AdminConsoleChromeTestHelpers.AssertRegionOrder(
            rendered,
            "top-bar",
            "sidebar-shell",
            "main-shell");
        int pageHeaderIndex = AdminConsoleChromeTestHelpers.IndexOfCssClass(rendered, "page-header");
        int footerMarkupIndex = AdminConsoleChromeTestHelpers.IndexOfMarkupContaining(
            rendered,
            "class=\"attribution\"");
        Assert.IsTrue(pageHeaderIndex >= 0, "Overview page header must render inside @Body");
        Assert.IsTrue(
            footerMarkupIndex > pageHeaderIndex,
            "footer must follow Overview @Body content in reading order");

        string[] focusables = AdminConsoleChromeTestHelpers.FocusableLabelsInDocumentOrder(rendered);
        string[] expectedRail =
        [
            "Overview",
            "Lookup",
            "Logs",
            "Settings",
            "City matching",
            "Area caches",
            "Reset locations",
            "Skip list"
        ];

        Assert.IsTrue(
            focusables.Length >= expectedRail.Length + 3,
            "keyboard path must reach rail, Overview actions, and footer");
        CollectionAssert.AreEqual(
            expectedRail,
            focusables.Take(expectedRail.Length).ToArray(),
            "rail destinations must be the first focusables after the status-only top bar");

        int runNow = Array.IndexOf(focusables, "Run Now");
        int refreshStats = Array.IndexOf(focusables, "Refresh Stats");
        int openLogs = Array.IndexOf(focusables, "Open Logs");
        int overtureMaps = Array.IndexOf(focusables, "Overture Maps");

        Assert.IsTrue(runNow > expectedRail.Length - 1, "page header follows the rail");
        Assert.IsTrue(refreshStats > runNow, "Refresh Stats follows Run Now in the page header");
        Assert.IsTrue(openLogs > refreshStats, "Overview body follows the page header");
        Assert.IsTrue(overtureMaps > openLogs, "footer follows Overview body content");

        Assert.AreEqual("Overview", AdminConsoleChromeTestHelpers.PageHeading(rendered));
        Assert.IsTrue(rendered.HasAttribute("role", "status"));

        string css = AdminConsoleChromeTestHelpers.ReadAppCss();
        StringAssert.Contains(css, ":focus-visible");
        Assert.IsTrue(
            Regex.IsMatch(
                css,
                @":is\(a,\s*button,\s*input,\s*select,\s*textarea,\s*summary\):focus-visible",
                RegexOptions.IgnoreCase),
            "interactive controls must have a visible :focus-visible treatment");
    }

    private sealed class StaticGeoBoundariesPage : ImmichReverseGeo.Web.Components.Pages.GeoBoundaries
    {
        protected override Task OnInitializedAsync() => Task.CompletedTask;
    }

    private sealed class StaticResetLocationsPage : ImmichReverseGeo.Web.Components.Pages.ResetGeoData
    {
        protected override Task OnInitializedAsync() => Task.CompletedTask;
    }

    private sealed class StaticLookupPage : ImmichReverseGeo.Web.Components.Pages.Lookup
    {
        protected override Task OnInitializedAsync() => Task.CompletedTask;
    }
}
