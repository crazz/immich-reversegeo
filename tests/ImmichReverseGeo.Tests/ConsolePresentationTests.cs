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
public sealed class ConsolePresentationTests
{
    [TestMethod]
    public async Task Operator_MovesBetweenConsolePages()
    {
        // GIVEN the Web console is showing a covered destination
        // WHEN the operator opens another covered destination
        // THEN headings, controls, surfaces, and statuses use the same console treatment
        // AND Light or Dark appearance remains the active palette
        // BUT the indigo lounge look is not used
        //
        // Closest-faithful stand-in: shared app.css token contract + ComponentRenderer
        // snapshots for two covered destinations. A real browser contrast meter is not
        // available in this suite; contrast-critical roles are asserted by token presence.

        string css = AdminConsoleChromeTestHelpers.ReadAppCss();

        Assert.IsTrue(
            Regex.IsMatch(
                css,
                @"html\[data-theme\s*=\s*[""']light[""']\]\s*\{[^}]*--bg-base\s*:",
                RegexOptions.Singleline | RegexOptions.IgnoreCase),
            "Light palette must define --bg-base under html[data-theme=light]");
        Assert.IsTrue(
            Regex.IsMatch(
                css,
                @"html\[data-theme\s*=\s*[""']dark[""']\]\s*\{[^}]*--bg-base\s*:",
                RegexOptions.Singleline | RegexOptions.IgnoreCase),
            "Dark palette must define --bg-base under html[data-theme=dark]");
        Assert.IsTrue(
            Regex.IsMatch(
                css,
                @"html\[data-theme\s*=\s*[""']light[""']\]\s*\{[^}]*--accent\s*:",
                RegexOptions.Singleline | RegexOptions.IgnoreCase),
            "Light palette must define --accent");
        Assert.IsTrue(
            Regex.IsMatch(
                css,
                @"html\[data-theme\s*=\s*[""']dark[""']\]\s*\{[^}]*--accent\s*:",
                RegexOptions.Singleline | RegexOptions.IgnoreCase),
            "Dark palette must define --accent");

        Assert.IsFalse(
            css.Contains("fonts.googleapis.com", StringComparison.OrdinalIgnoreCase),
            "network font stylesheet must not be requested");
        Assert.IsFalse(css.Contains("Syne", StringComparison.Ordinal));
        Assert.IsFalse(css.Contains("Nunito", StringComparison.Ordinal));
        Assert.IsFalse(
            css.Contains("indigo-night", StringComparison.OrdinalIgnoreCase)
                || css.Contains("indigo lounge", StringComparison.OrdinalIgnoreCase),
            "indigo lounge presentation must not remain as the documented look");
        Assert.IsFalse(
            css.Contains("graphite presentation", StringComparison.OrdinalIgnoreCase),
            "unpublished graphite restyle must not remain the documented look");

        // Google Admin seed: light canvas uses a light --bg-base; dark uses a dark one.
        string lightBg = ExtractThemeToken(css, "light", "--bg-base");
        string darkBg = ExtractThemeToken(css, "dark", "--bg-base");
        Assert.IsTrue(
            IsLightHex(lightBg),
            $"Light --bg-base must be a light canvas color, got '{lightBg}'");
        Assert.IsTrue(
            IsDarkHex(darkBg),
            $"Dark --bg-base must be a dark charcoal color, got '{darkBg}'");

        StringAssert.Contains(css, ".page-header");
        StringAssert.Contains(css, ".settings-card");
        StringAssert.Contains(css, ".btn-primary");
        StringAssert.Contains(css, ".top-bar");
        StringAssert.Contains(css, ".sidebar-shell");

        var status = new ProcessAssetsWebStatus(DeploymentMode.Standard);
        var overview = WebStatusRenderingTests.CreateDashboard(status, new ProcessingState());
        await using var overviewRenderer = new WebStatusRenderingTests.ComponentRenderer();
        await overviewRenderer.AttachAsync(overview);
        WebStatusRenderingTests.RenderSnapshot overviewRendered = await overviewRenderer.ReadAsync();

        var lookup = new StaticLookupPage();
        await using var lookupRenderer = new WebStatusRenderingTests.ComponentRenderer();
        await lookupRenderer.AttachAsync(lookup);
        WebStatusRenderingTests.RenderSnapshot lookupRendered = await lookupRenderer.ReadAsync();

        Assert.IsTrue(overviewRendered.HasCssClass("page-header"));
        Assert.IsTrue(lookupRendered.HasCssClass("page-header"));
        Assert.AreEqual("Overview", AdminConsoleChromeTestHelpers.PageHeading(overviewRendered));
        Assert.AreEqual("Lookup", AdminConsoleChromeTestHelpers.PageHeading(lookupRendered));
        Assert.IsTrue(overviewRendered.HasCssClass("btn-primary"));
        Assert.IsTrue(lookupRendered.HasCssClass("btn-primary"));
        Assert.IsTrue(overviewRendered.HasAttribute("role", "status"));
    }

    [TestMethod]
    public void StatusAlertAndBadgeText_MeetsContrastAgainstActualSurfaces()
    {
        // Spec: normal enabled status/alert/badge text ≥4.5:1 against the surface it sits on.
        // Alerts often sit on the page canvas (--bg-base), not only on white --bg-surface.
        // Roles previously failing in Light: warn-on-white, badge-miss, alert-info, alert-error.
        string css = AdminConsoleChromeTestHelpers.ReadAppCss();

        foreach (string theme in new[] { "light", "dark" })
        {
            string canvas = ExtractThemeToken(css, theme, "--bg-base");
            string surface = ExtractThemeToken(css, theme, "--bg-surface");
            string warn = ExtractThemeToken(css, theme, "--warn");
            string warnLight = ExtractThemeToken(css, theme, "--warn-light");
            string accent = ExtractThemeToken(css, theme, "--accent");
            string accentLight = ExtractThemeToken(css, theme, "--accent-light");
            string danger = ExtractThemeToken(css, theme, "--danger");
            string dangerLight = ExtractThemeToken(css, theme, "--danger-light");

            AssertContrastAtLeast(
                4.5,
                warn,
                surface,
                $"{theme}: .log-line.warn / status --warn on --bg-surface");
            AssertContrastAtLeast(
                4.5,
                warn,
                BlendOver(warnLight, canvas),
                $"{theme}: .badge-miss --warn on --warn-light over --bg-base");
            AssertContrastAtLeast(
                4.5,
                warn,
                BlendOver(warnLight, surface),
                $"{theme}: .badge-miss --warn on --warn-light over --bg-surface");
            AssertContrastAtLeast(
                4.5,
                accent,
                BlendOver(accentLight, canvas),
                $"{theme}: .alert-info --accent on --accent-light over --bg-base");
            AssertContrastAtLeast(
                4.5,
                accent,
                BlendOver(accentLight, surface),
                $"{theme}: .alert-info --accent on --accent-light over --bg-surface");
            AssertContrastAtLeast(
                4.5,
                danger,
                BlendOver(dangerLight, canvas),
                $"{theme}: .alert-error --danger on --danger-light over --bg-base");
            AssertContrastAtLeast(
                4.5,
                danger,
                BlendOver(dangerLight, surface),
                $"{theme}: .alert-error --danger on --danger-light over --bg-surface");
        }

        Assert.IsFalse(
            Regex.IsMatch(
                css,
                @"Measured\s*≥\s*4\.5:1",
                RegexOptions.IgnoreCase),
            "do not claim Measured ≥4.5:1 unless status/alert/badge ratios are asserted");
    }

    [TestMethod]
    public void AccessibleConsoleStates_DistinguishControlsAndHonorReducedMotion()
    {
        // Presentation parts of Accessible console states:
        // Primary/secondary/destructive/disabled/hovered/keyboard-focused distinguishable
        // in both Light and Dark; reduced-motion suppresses nonessential animation;
        // contrast-critical text roles are ratio-asserted in StatusAlertAndBadgeText_*.
        string css = AdminConsoleChromeTestHelpers.ReadAppCss();

        foreach (string theme in new[] { "light", "dark" })
        {
            Assert.IsTrue(
                Regex.IsMatch(
                    css,
                    $@"html\[data-theme\s*=\s*[""']{theme}[""']\]\s*\{{[^}}]*--text-primary\s*:",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase),
                $"{theme}: --text-primary required for ≥4.5:1 enabled text");
            Assert.IsTrue(
                Regex.IsMatch(
                    css,
                    $@"html\[data-theme\s*=\s*[""']{theme}[""']\]\s*\{{[^}}]*--danger\s*:",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase),
                $"{theme}: --danger required for error/status text with color");
            Assert.IsTrue(
                Regex.IsMatch(
                    css,
                    $@"html\[data-theme\s*=\s*[""']{theme}[""']\]\s*\{{[^}}]*--accent\s*:",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase),
                $"{theme}: --accent required for primary/focus controls");
            Assert.IsTrue(
                Regex.IsMatch(
                    css,
                    $@"html\[data-theme\s*=\s*[""']{theme}[""']\]\s*\{{[^}}]*--on-accent\s*:",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase),
                $"{theme}: --on-accent required for primary button label contrast");
        }

        StringAssert.Contains(css, ".btn-primary");
        StringAssert.Contains(css, ".btn-secondary");
        StringAssert.Contains(css, ".btn-danger");
        Assert.IsTrue(
            Regex.IsMatch(css, @"\.btn-primary:hover|button\.btn-primary:hover"),
            "primary hover must be styled");
        Assert.IsTrue(
            Regex.IsMatch(css, @"\.btn-secondary:hover|button\.btn-secondary:hover"),
            "secondary hover must be styled");
        Assert.IsTrue(
            Regex.IsMatch(css, @"\.btn-danger:hover|button\.btn-danger:hover"),
            "destructive hover must be styled");
        Assert.IsTrue(
            Regex.IsMatch(css, @"button:disabled|\.btn:disabled"),
            "disabled controls must be styled");
        Assert.IsTrue(
            Regex.IsMatch(
                css,
                @":is\(a,\s*button,\s*input,\s*select,\s*textarea,\s*summary\):focus-visible",
                RegexOptions.IgnoreCase),
            "keyboard-focused controls must be distinguishable");

        Assert.IsTrue(
            Regex.IsMatch(
                css,
                @"@media\s*\(\s*prefers-reduced-motion\s*:\s*reduce\s*\)",
                RegexOptions.IgnoreCase),
            "reduced-motion preference must suppress nonessential animation");
        StringAssert.Contains(css, "components-reconnect-modal");
        StringAssert.Contains(css, ".text-danger");
    }

    [TestMethod]
    public async Task WorkerOrMaintenanceStatus_ChangesKeepTextAndLiveSemantics()
    {
        // GIVEN an existing status transition or failure is rendered
        // WHEN that state is shown
        // THEN status text remains available in addition to color
        // AND existing live-region or alert semantics remain
        var status = new ProcessAssetsWebStatus(DeploymentMode.Standard);
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual);
        var sink = (IProcessAssetsWorkerStatusSink)status;
        sink.Admit(request, cancellationAlreadyWon: false);
        sink.ObserveTransport(request, WorkerRunTransportPhase.Accepted);
        sink.ObserveFinality(request, ProcessingRunOutcome.Failed, WorkerRunFailureCategory.Crash);

        var overview = WebStatusRenderingTests.CreateDashboard(status, new ProcessingState());
        await using var overviewRenderer = new WebStatusRenderingTests.ComponentRenderer();
        await overviewRenderer.AttachAsync(overview);
        WebStatusRenderingTests.RenderSnapshot overviewRendered = await overviewRenderer.ReadAsync();

        StringAssert.Contains(overviewRendered.Text, "Worker: Failed");
        StringAssert.Contains(overviewRendered.Text, "ProcessAssets worker failed.");
        Assert.IsTrue(overviewRendered.HasAttribute("role", "status"));
        Assert.IsTrue(overviewRendered.HasAttribute("role", "alert"));
        Assert.IsTrue(overviewRendered.HasAttribute("aria-live", "polite"));

        string css = AdminConsoleChromeTestHelpers.ReadAppCss();
        Assert.IsTrue(
            Regex.IsMatch(css, @"\.alert-error|\.alert-danger"),
            "status alerts must keep colored treatment with text");
        StringAssert.Contains(css, "color: var(--danger)");
    }

    [TestMethod]
    public async Task ExistingAction_IsActivatedWithSameControls()
    {
        // GIVEN an existing console action is available
        // WHEN the operator activates it after the redesign
        // THEN it invokes the same operation with the same inputs and safeguards
        // AND it exposes the same pending and final outcomes as before
        //
        // Closest-faithful: control inventory and labels remain reachable; handlers are
        // unchanged (covered by existing processing/lookup/maintenance tests).
        var status = new ProcessAssetsWebStatus(DeploymentMode.Standard);
        var overview = WebStatusRenderingTests.CreateDashboard(status, new ProcessingState());
        await using var overviewRenderer = new WebStatusRenderingTests.ComponentRenderer();
        await overviewRenderer.AttachAsync(overview);
        WebStatusRenderingTests.RenderSnapshot overviewRendered = await overviewRenderer.ReadAsync();
        AdminConsoleChromeTestHelpers.AssertPageHeaderContains(overviewRendered, "Run Now");
        AdminConsoleChromeTestHelpers.AssertPageHeaderContains(overviewRendered, "Refresh Stats");
        StringAssert.Contains(overviewRendered.Text, "Open Logs");

        var lookup = new StaticLookupPage();
        await using var lookupRenderer = new WebStatusRenderingTests.ComponentRenderer();
        await lookupRenderer.AttachAsync(lookup);
        WebStatusRenderingTests.RenderSnapshot lookupRendered = await lookupRenderer.ReadAsync();
        AdminConsoleChromeTestHelpers.AssertPageHeaderContains(lookupRendered, "Lookup");
        StringAssert.Contains(lookupRendered.Text, "Include bundled airport infrastructure lookup");

        var skipList = new ImmichReverseGeo.Web.Components.Pages.SkipList();
        await using var skipRenderer = new WebStatusRenderingTests.ComponentRenderer();
        await skipRenderer.AttachAsync(skipList);
        WebStatusRenderingTests.RenderSnapshot skipRendered = await skipRenderer.ReadAsync();
        AdminConsoleChromeTestHelpers.AssertPageHeaderContains(skipRendered, "Clear Skip List");
    }

    [TestMethod]
    public async Task ExistingConfirmation_IsDisplayedForResetAndCacheDeletion()
    {
        // GIVEN Reset All or a cache deletion reaches its existing confirmation state
        // WHEN that confirmation is shown
        // THEN the same prompt and confirmation/cancellation controls remain visible and operable
        // AND no confirmation step is added or bypassed
        var reset = new StaticResetLocationsPage();
        typeof(ImmichReverseGeo.Web.Components.Pages.ResetGeoData)
            .GetField("_resetAllConfirm", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(reset, true);
        await using var resetRenderer = new WebStatusRenderingTests.ComponentRenderer();
        await resetRenderer.AttachAsync(reset);
        WebStatusRenderingTests.RenderSnapshot resetRendered = await resetRenderer.ReadAsync();
        Assert.AreEqual("Reset locations", AdminConsoleChromeTestHelpers.PageHeading(resetRendered));
        StringAssert.Contains(resetRendered.Text, "Yes, reset all geo data");
        StringAssert.Contains(resetRendered.Text, "Cancel");
        StringAssert.Contains(
            resetRendered.Text,
            "This will clear reverse geo location data for every matching asset");

        string geoBoundariesSource = File.ReadAllText(Path.Combine(
            AdminConsoleChromeTestHelpers.FindRepositoryRoot(),
            "src",
            "ImmichReverseGeo.Web",
            "Components",
            "Pages",
            "GeoBoundaries.razor"));
        StringAssert.Contains(geoBoundariesSource, "role=\"dialog\"");
        StringAssert.Contains(geoBoundariesSource, "aria-label=\"Confirm cache deletion\"");
        StringAssert.Contains(geoBoundariesSource, "Confirm Delete All");
        StringAssert.Contains(geoBoundariesSource, "Confirm Delete");
        StringAssert.Contains(geoBoundariesSource, "Keep caches");
        StringAssert.Contains(geoBoundariesSource, "Delete All Overture Divisions");
        StringAssert.Contains(geoBoundariesSource, "Delete All GADM Caches");
    }

    [TestMethod]
    public async Task LicenceAndHelpText_NeedMultipleLines()
    {
        // GIVEN existing explanatory text, attribution, or GADM licence text needs multiple lines
        // WHEN the page is displayed
        // THEN the text remains complete and readable
        // BUT it is not shortened, clipped, hidden behind a new disclosure, or removed
        var status = new ProcessAssetsWebStatus(DeploymentMode.Standard);
        var layout = WebStatusRenderingTests.CreateTopBar(status);
        await using var layoutRenderer = new WebStatusRenderingTests.ComponentRenderer();
        await layoutRenderer.AttachAsync(layout);
        WebStatusRenderingTests.RenderSnapshot layoutRendered = await layoutRenderer.ReadFlattenedAsync();

        StringAssert.Contains(layoutRendered.Text, "Data sources");
        StringAssert.Contains(layoutRendered.Text, "Overture Maps");
        StringAssert.Contains(layoutRendered.Text, "Divisions/Base: ODbL · Places: mixed open licences");
        StringAssert.Contains(layoutRendered.Text, "© OpenStreetMap contributors, Overture Maps Foundation.");
        StringAssert.Contains(
            layoutRendered.Text,
            "Bundled Overture-derived data also carries upstream source attributions");
        Assert.IsFalse(
            layoutRendered.Text.Contains("licence-disclosure", StringComparison.OrdinalIgnoreCase)
                || layoutRendered.Text.Contains("Show more licence", StringComparison.OrdinalIgnoreCase),
            "licence text must not be hidden behind a new disclosure");

        var areaCaches = new StaticGeoBoundariesPage();
        await using var areaRenderer = new WebStatusRenderingTests.ComponentRenderer();
        await areaRenderer.AttachAsync(areaCaches);
        WebStatusRenderingTests.RenderSnapshot areaRendered = await areaRenderer.ReadAsync();
        StringAssert.Contains(areaRendered.Text, "GADM data is available for academic and other non-commercial use.");
        StringAssert.Contains(areaRendered.Text, "Review the GADM license");

        await using var settings = await AdminConsoleSettingsPageFixture.CreateAsync();
        WebStatusRenderingTests.RenderSnapshot settingsRendered = await settings.ReadAsync();
        StringAssert.Contains(settingsRendered.Text, "Appearance");
        StringAssert.Contains(settingsRendered.Text, "Enable GADM administrative areas as an optional source");
    }

    [TestMethod]
    public void PageBodies_UseThemeTokensWithoutGraphiteAccentHardcodes()
    {
        // Task 4.2: remaining page bodies share console tokens; no graphite lounge accent leftovers.
        string css = AdminConsoleChromeTestHelpers.ReadAppCss();
        Assert.IsFalse(
            css.Contains("rgba(142, 184, 255", StringComparison.Ordinal),
            "page surfaces must use theme accent tokens instead of graphite accent hardcodes");
        Assert.IsFalse(
            css.Contains("#bcd7ff", StringComparison.OrdinalIgnoreCase),
            "alert-info must not use dark-only info text that fails Light contrast");
        Assert.IsTrue(
            Regex.IsMatch(
                css,
                @"\.alert-info\s*\{[^}]*color:\s*var\(--accent\)",
                RegexOptions.Singleline | RegexOptions.IgnoreCase),
            "alert-info must use theme accent color for readable status text in both palettes");
        StringAssert.Contains(css, "var(--accent-light)");
        StringAssert.Contains(css, "var(--accent-border)");
    }

    [TestMethod]
    public void PublicDocs_MatchConsoleDestinationNamesAndAppearance()
    {
        // Task 4.3: published workflow names, Skip list, /data redirect, and Appearance
        // match the console operators see.
        string root = AdminConsoleChromeTestHelpers.FindRepositoryRoot();
        string usingTheApp = File.ReadAllText(Path.Combine(root, "docs", "website", "using-the-app.md"));
        string websiteChangelog = File.ReadAllText(Path.Combine(root, "docs", "website", "changelog.md"));
        string technicalChangelog = File.ReadAllText(Path.Combine(root, "CHANGELOG.md"));
        string gettingStarted = File.ReadAllText(Path.Combine(root, "docs", "website", "getting-started.md"));
        string configuration = File.ReadAllText(Path.Combine(root, "docs", "website", "configuration.md"));
        string deploymentModes = File.ReadAllText(Path.Combine(root, "docs", "website", "deployment-modes.md"));
        string upgrading = File.ReadAllText(Path.Combine(root, "docs", "website", "upgrading.md"));
        string installation = File.ReadAllText(Path.Combine(root, "docs", "website", "installation.md"));
        string troubleshooting = File.ReadAllText(Path.Combine(root, "docs", "website", "troubleshooting.md"));

        StringAssert.Contains(usingTheApp, "## Overview");
        Assert.IsFalse(
            usingTheApp.Contains("## Dashboard", StringComparison.Ordinal),
            "using-the-app must use Overview, not Dashboard, as the console page heading");
        StringAssert.Contains(usingTheApp, "Skip list");
        StringAssert.Contains(usingTheApp, "/data/skip-list");
        StringAssert.Contains(usingTheApp, "Area caches");
        StringAssert.Contains(usingTheApp, "Reset locations");
        StringAssert.Contains(usingTheApp, "City matching");
        StringAssert.Contains(usingTheApp, "Appearance");
        Assert.IsTrue(
            usingTheApp.Contains("redirect", StringComparison.OrdinalIgnoreCase)
                && usingTheApp.Contains("/data", StringComparison.Ordinal),
            "using-the-app must describe the /data → Skip list redirect");

        StringAssert.Contains(websiteChangelog, "Overview");
        StringAssert.Contains(websiteChangelog, "Skip list");
        StringAssert.Contains(websiteChangelog, "Appearance");
        StringAssert.Contains(websiteChangelog, "/data");
        StringAssert.Contains(technicalChangelog, "Appearance");
        StringAssert.Contains(technicalChangelog, "Skip list");

        string websiteUnreleased = UnreleasedSection(websiteChangelog);
        string technicalUnreleased = UnreleasedSection(technicalChangelog);
        Assert.IsFalse(
            Regex.IsMatch(websiteUnreleased, @"\bAdministrative Areas\b"),
            "website Unreleased must use Area caches, not Administrative Areas");
        Assert.IsFalse(
            Regex.IsMatch(technicalUnreleased, @"\bAdministrative Areas\b"),
            "technical Unreleased must use Area caches, not Administrative Areas");
        Assert.IsFalse(
            Regex.IsMatch(websiteUnreleased, @"\bCity Resolver\b"),
            "website Unreleased must use City matching, not City Resolver");
        Assert.IsFalse(
            Regex.IsMatch(technicalUnreleased, @"\bCity Resolver\b"),
            "technical Unreleased must use City matching, not City Resolver");
        Assert.IsFalse(
            Regex.IsMatch(websiteUnreleased, @"\bData area\b"),
            "website Unreleased must not keep the old Data area console name");
        Assert.IsFalse(
            Regex.IsMatch(technicalUnreleased, @"\bData area\b"),
            "technical Unreleased must not keep the old Data area console name");
        StringAssert.Contains(websiteUnreleased, "Area caches");
        StringAssert.Contains(technicalUnreleased, "Area caches");

        StringAssert.Contains(gettingStarted, "Overview");
        Assert.IsFalse(
            Regex.IsMatch(gettingStarted, @"\bDashboard\b"),
            "getting-started must not still describe the console page as Dashboard");
        StringAssert.Contains(configuration, "City matching");
        StringAssert.Contains(configuration, "Appearance");

        foreach ((string label, string body) in new[]
                 {
                     ("deployment-modes", deploymentModes),
                     ("upgrading", upgrading),
                     ("installation", installation),
                     ("troubleshooting", troubleshooting),
                 })
        {
            Assert.IsFalse(
                Regex.IsMatch(body, @"\bDashboard\b"),
                $"{label} must refer to Overview, not Dashboard, for the Web console");
            Assert.IsFalse(
                Regex.IsMatch(body, @"\bAdministrative Areas\b"),
                $"{label} must refer to Area caches, not Administrative Areas");
        }

        StringAssert.Contains(deploymentModes, "Overview");
        StringAssert.Contains(upgrading, "Overview");
        StringAssert.Contains(upgrading, "Area caches");
        StringAssert.Contains(installation, "Overview");
        StringAssert.Contains(troubleshooting, "Overview");
    }

    private sealed class StaticGeoBoundariesPage : ImmichReverseGeo.Web.Components.Pages.GeoBoundaries
    {
        protected override Task OnInitializedAsync() => Task.CompletedTask;
    }

    private sealed class StaticResetLocationsPage : ImmichReverseGeo.Web.Components.Pages.ResetGeoData
    {
        protected override Task OnInitializedAsync() => Task.CompletedTask;
    }

    private static string UnreleasedSection(string changelog)
    {
        Match match = Regex.Match(
            changelog,
            @"##\s+Unreleased\s*(?<body>.*?)(?=\r?\n##\s+|\z)",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        Assert.IsTrue(match.Success, "changelog must have an Unreleased section");
        return match.Groups["body"].Value;
    }

    private static string ExtractThemeToken(string css, string theme, string token)
    {
        Match block = Regex.Match(
            css,
            $@"html\[data-theme\s*=\s*[""']{Regex.Escape(theme)}[""']\]\s*\{{(?<body>[^}}]+)\}}",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        Assert.IsTrue(block.Success, $"missing html[data-theme={theme}] block");
        Match value = Regex.Match(
            block.Groups["body"].Value,
            $@"\{Regex.Escape(token)}\s*:\s*(?<value>[^;]+);",
            RegexOptions.IgnoreCase);
        Assert.IsTrue(value.Success, $"missing {token} in {theme} theme");
        return value.Groups["value"].Value.Trim();
    }

    private static void AssertContrastAtLeast(
        double minimum,
        string foreground,
        string background,
        string role)
    {
        double ratio = ContrastRatio(foreground, background);
        Assert.IsGreaterThanOrEqualTo(
            minimum,
            ratio,
            $"{role}: expected ≥{minimum:0.0}:1, got {ratio:0.00}:1 (fg={foreground}, bg={background})");
    }

    private static string BlendOver(string overlay, string baseColor)
    {
        Assert.IsTrue(
            TryParseRgba(overlay, out int or, out int og, out int ob, out double alpha),
            $"expected rgba tint token, got '{overlay}'");
        Assert.IsTrue(
            TryParseHex(baseColor, out int br, out int bg, out int bb),
            $"expected hex base surface, got '{baseColor}'");

        int r = (int)Math.Round(or * alpha + br * (1 - alpha));
        int g = (int)Math.Round(og * alpha + bg * (1 - alpha));
        int b = (int)Math.Round(ob * alpha + bb * (1 - alpha));
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    private static double ContrastRatio(string foreground, string background)
    {
        Assert.IsTrue(TryParseHex(foreground, out int fr, out int fg, out int fb), $"bad fg '{foreground}'");
        Assert.IsTrue(TryParseHex(background, out int br, out int bg, out int bb), $"bad bg '{background}'");
        double l1 = RelativeLuminance(fr, fg, fb);
        double l2 = RelativeLuminance(br, bg, bb);
        double lighter = Math.Max(l1, l2);
        double darker = Math.Min(l1, l2);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static bool IsLightHex(string raw)
    {
        if (!TryParseHex(raw, out int r, out int g, out int b))
        {
            return false;
        }

        return RelativeLuminance(r, g, b) >= 0.75;
    }

    private static bool IsDarkHex(string raw)
    {
        if (!TryParseHex(raw, out int r, out int g, out int b))
        {
            return false;
        }

        return RelativeLuminance(r, g, b) <= 0.25;
    }

    private static bool TryParseHex(string raw, out int r, out int g, out int b)
    {
        r = g = b = 0;
        Match match = Regex.Match(raw.Trim(), @"^#(?<hex>[0-9a-fA-F]{6})$");
        if (!match.Success)
        {
            return false;
        }

        string hex = match.Groups["hex"].Value;
        r = Convert.ToInt32(hex[..2], 16);
        g = Convert.ToInt32(hex[2..4], 16);
        b = Convert.ToInt32(hex[4..6], 16);
        return true;
    }

    private static bool TryParseRgba(string raw, out int r, out int g, out int b, out double alpha)
    {
        r = g = b = 0;
        alpha = 0;
        Match match = Regex.Match(
            raw.Trim(),
            @"^rgba\(\s*(?<r>\d+)\s*,\s*(?<g>\d+)\s*,\s*(?<b>\d+)\s*,\s*(?<a>0?\.\d+|1(?:\.0+)?|0)\s*\)$",
            RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return false;
        }

        r = int.Parse(match.Groups["r"].Value);
        g = int.Parse(match.Groups["g"].Value);
        b = int.Parse(match.Groups["b"].Value);
        alpha = double.Parse(match.Groups["a"].Value, System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    private static double RelativeLuminance(int r, int g, int b)
    {
        static double Channel(int c)
        {
            double s = c / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(r) + 0.7152 * Channel(g) + 0.0722 * Channel(b);
    }

    private sealed class StaticLookupPage : ImmichReverseGeo.Web.Components.Pages.Lookup
    {
        protected override Task OnInitializedAsync() => Task.CompletedTask;
    }
}
