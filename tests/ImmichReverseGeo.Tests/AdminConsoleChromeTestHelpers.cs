using ImmichReverseGeo.Core.ApplicationRole;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Web.Composition;
using ImmichReverseGeo.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

#pragma warning disable BL0006

namespace ImmichReverseGeo.Tests;

internal static class AdminConsoleChromeTestHelpers
{
    internal const int PhoneWidthCssPx = 390;

    internal static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "package.json")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    internal static string ReadAppCss()
    {
        return File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ImmichReverseGeo.Web",
            "wwwroot",
            "app.css"));
    }

    internal static string CssRulesMatchingMaxWidth(string css, int widthCssPx)
    {
        var blocks = new StringBuilder();
        foreach (Match match in Regex.Matches(
            css,
            @"@media\s*\(\s*max-width\s*:\s*(\d+)px\s*\)\s*\{",
            RegexOptions.IgnoreCase))
        {
            if (!int.TryParse(match.Groups[1].Value, out int maxWidth) || widthCssPx > maxWidth)
            {
                continue;
            }

            int bodyStart = match.Index + match.Length;
            int depth = 1;
            int cursor = bodyStart;
            while (cursor < css.Length && depth > 0)
            {
                char ch = css[cursor];
                if (ch == '{')
                {
                    depth++;
                }
                else if (ch == '}')
                {
                    depth--;
                }

                cursor++;
            }

            blocks.Append(css.AsSpan(bodyStart, cursor - bodyStart - 1));
            blocks.AppendLine();
        }

        return blocks.ToString();
    }

    internal static bool HasMenuOrHamburgerControl(WebStatusRenderingTests.RenderSnapshot snapshot)
    {
        if (snapshot.Text.Contains("menu-button", StringComparison.OrdinalIgnoreCase)
            || snapshot.Text.Contains("hamburger", StringComparison.OrdinalIgnoreCase)
            || snapshot.Text.Contains("nav-toggle", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (string label in FocusableLabels(snapshot.Frames, 0, snapshot.Frames.Length))
        {
            if (label.Contains("menu", StringComparison.OrdinalIgnoreCase)
                || label.Contains("hamburger", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static void AssertRegionOrder(
        WebStatusRenderingTests.RenderSnapshot snapshot,
        params string[] cssClasses)
    {
        int previous = -1;
        foreach (string cssClass in cssClasses)
        {
            int index = IndexOfCssClass(snapshot, cssClass);
            Assert.IsTrue(index >= 0, $"missing region '{cssClass}'");
            Assert.IsTrue(
                index > previous,
                $"region '{cssClass}' must follow earlier chrome regions in render order");
            previous = index;
        }
    }

    internal static void AssertPageHeaderContains(
        WebStatusRenderingTests.RenderSnapshot snapshot,
        string expected)
    {
        if (snapshot.HasCssClass("page-header"))
        {
            StringAssert.Contains(snapshot.ElementTextByCssClass("page-header"), expected);
            return;
        }

        StringAssert.Contains(snapshot.Text, "page-header");
        StringAssert.Contains(snapshot.Text, expected);
    }

    internal static int IndexOfCssClass(WebStatusRenderingTests.RenderSnapshot snapshot, string cssClass)
    {
        for (var index = 0; index < snapshot.Frames.Length; index++)
        {
            RenderTreeFrame frame = snapshot.Frames[index];
            if (frame.FrameType != RenderTreeFrameType.Element)
            {
                continue;
            }

            for (var attributeIndex = index + 1;
                attributeIndex < snapshot.Frames.Length
                    && snapshot.Frames[attributeIndex].FrameType == RenderTreeFrameType.Attribute;
                attributeIndex++)
            {
                RenderTreeFrame attribute = snapshot.Frames[attributeIndex];
                if (string.Equals(attribute.AttributeName, "class", StringComparison.Ordinal)
                    && (attribute.AttributeValue?.ToString() ?? string.Empty)
                        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Contains(cssClass, StringComparer.Ordinal))
                {
                    return index;
                }
            }
        }

        return -1;
    }

    internal static int IndexOfMarkupContaining(
        WebStatusRenderingTests.RenderSnapshot snapshot,
        string fragment)
    {
        for (var index = 0; index < snapshot.Frames.Length; index++)
        {
            RenderTreeFrame frame = snapshot.Frames[index];
            if (frame.FrameType == RenderTreeFrameType.Markup
                && frame.MarkupContent.Contains(fragment, StringComparison.Ordinal))
            {
                return index;
            }

            if (frame.FrameType == RenderTreeFrameType.Text
                && frame.TextContent.Contains(fragment, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    internal static string[] MarkupAnchorLabels(string text)
    {
        var labels = new List<string>();
        foreach (Match match in Regex.Matches(
            text,
            """<a\b[^>]*>([^<]+)</a>""",
            RegexOptions.IgnoreCase))
        {
            string label = Regex.Replace(match.Groups[1].Value, @"\s+", " ").Trim();
            if (!string.IsNullOrWhiteSpace(label)
                && !labels.Contains(label, StringComparer.Ordinal))
            {
                labels.Add(label);
            }
        }

        return labels.ToArray();
    }

    /// <summary>
    /// Collects keyboard-reachable labels in flattened render order. Element focusables and
    /// anchors inside Markup frames are interleaved so composed MainLayout+Body trees keep
    /// top bar → rail → page → footer order. ComponentRenderer cannot drive real Tab keys;
    /// this is the closest faithful stand-in for reading-order traversal.
    /// </summary>
    internal static string[] FocusableLabelsInDocumentOrder(
        WebStatusRenderingTests.RenderSnapshot snapshot)
    {
        var labels = new List<string>();
        for (var index = 0; index < snapshot.Frames.Length; index++)
        {
            RenderTreeFrame frame = snapshot.Frames[index];
            if (frame.FrameType == RenderTreeFrameType.Element
                && IsFocusableElement(frame.ElementName))
            {
                if (!HasHiddenAttribute(snapshot.Frames, index, frame.ElementSubtreeLength)
                    && !HasDisabledAttribute(snapshot.Frames, index, frame.ElementSubtreeLength))
                {
                    string label = ElementText(snapshot.Frames, index, frame.ElementSubtreeLength).Trim();
                    if (!string.IsNullOrWhiteSpace(label))
                    {
                        labels.Add(Regex.Replace(label, @"\s+", " "));
                    }
                }

                index += Math.Max(0, frame.ElementSubtreeLength - 1);
                continue;
            }

            if (frame.FrameType == RenderTreeFrameType.Markup)
            {
                foreach (string label in MarkupAnchorLabels(frame.MarkupContent))
                {
                    if (!labels.Contains(label, StringComparer.Ordinal))
                    {
                        labels.Add(label);
                    }
                }
            }
        }

        return labels.ToArray();
    }

    internal static string[] FocusableLabels(RenderTreeFrame[] frames, int start, int length)
    {
        var labels = new List<string>();
        int end = Math.Min(frames.Length, start + length);
        for (var index = Math.Max(0, start); index < end; index++)
        {
            RenderTreeFrame frame = frames[index];
            if (frame.FrameType != RenderTreeFrameType.Element
                || !IsFocusableElement(frame.ElementName))
            {
                continue;
            }

            if (HasHiddenAttribute(frames, index, frame.ElementSubtreeLength)
                || HasDisabledAttribute(frames, index, frame.ElementSubtreeLength))
            {
                continue;
            }

            string label = ElementText(frames, index, frame.ElementSubtreeLength).Trim();
            if (!string.IsNullOrWhiteSpace(label))
            {
                labels.Add(Regex.Replace(label, @"\s+", " "));
            }
        }

        return labels.ToArray();
    }

    internal static bool IsFocusableElement(string elementName)
    {
        return elementName is "a" or "button" or "input" or "select" or "textarea" or "summary";
    }

    internal static bool HasHiddenAttribute(RenderTreeFrame[] frames, int index, int length)
    {
        return frames
            .Skip(index + 1)
            .Take(length - 1)
            .TakeWhile(frame => frame.FrameType == RenderTreeFrameType.Attribute)
            .Any(frame => string.Equals(frame.AttributeName, "hidden", StringComparison.Ordinal)
                && !Equals(frame.AttributeValue, false));
    }

    internal static bool HasDisabledAttribute(RenderTreeFrame[] frames, int index, int length)
    {
        return frames
            .Skip(index + 1)
            .Take(length - 1)
            .TakeWhile(frame => frame.FrameType == RenderTreeFrameType.Attribute)
            .Any(frame => string.Equals(frame.AttributeName, "disabled", StringComparison.Ordinal)
                && !Equals(frame.AttributeValue, false));
    }

    internal static string ElementText(RenderTreeFrame[] frames, int index, int length)
    {
        return string.Concat(frames
            .Skip(index)
            .Take(length)
            .Where(item => item.FrameType is RenderTreeFrameType.Text or RenderTreeFrameType.Markup)
            .Select(item => item.FrameType == RenderTreeFrameType.Text
                ? item.TextContent
                : item.MarkupContent));
    }

    internal static string PageHeading(WebStatusRenderingTests.RenderSnapshot snapshot)
    {
        Match match = Regex.Match(snapshot.Text, @"<h2>([^<]+)</h2>");
        if (match.Success)
        {
            return match.Groups[1].Value.Trim();
        }

        for (var index = 0; index < snapshot.Frames.Length; index++)
        {
            var frame = snapshot.Frames[index];
            if (frame.FrameType != RenderTreeFrameType.Element
                || !string.Equals(frame.ElementName, "h2", StringComparison.Ordinal))
            {
                continue;
            }

            return string.Concat(snapshot.Frames
                    .Skip(index + 1)
                    .TakeWhile(item => item.FrameType is RenderTreeFrameType.Text
                        or RenderTreeFrameType.Markup
                        or RenderTreeFrameType.Attribute
                        or RenderTreeFrameType.Region)
                    .Where(item => item.FrameType is RenderTreeFrameType.Text or RenderTreeFrameType.Markup)
                    .Select(item => item.FrameType == RenderTreeFrameType.Text
                        ? item.TextContent
                        : item.MarkupContent))
                .Trim();
        }

        return string.Empty;
    }

    internal static string[] SectionTitles(WebStatusRenderingTests.RenderSnapshot snapshot)
    {
        return Regex.Matches(
                snapshot.Text,
                """<div class="nav-section-title">([^<]+)</div>""")
            .Select(match => match.Groups[1].Value.Trim())
            .ToArray();
    }

    internal static string[] DestinationLabels(WebStatusRenderingTests.RenderSnapshot snapshot)
    {
        var labels = new List<string>();
        for (var index = 0; index < snapshot.Frames.Length; index++)
        {
            var frame = snapshot.Frames[index];
            if (frame.FrameType != RenderTreeFrameType.Element
                || !string.Equals(frame.ElementName, "a", StringComparison.Ordinal))
            {
                continue;
            }

            var cursor = index + 1;
            while (cursor < snapshot.Frames.Length
                && snapshot.Frames[cursor].FrameType is RenderTreeFrameType.Attribute or RenderTreeFrameType.Region)
            {
                cursor++;
            }

            var parts = new List<string>();
            while (cursor < snapshot.Frames.Length
                && snapshot.Frames[cursor].FrameType is RenderTreeFrameType.Text
                    or RenderTreeFrameType.Markup
                    or RenderTreeFrameType.Region)
            {
                RenderTreeFrame content = snapshot.Frames[cursor];
                if (content.FrameType == RenderTreeFrameType.Text)
                {
                    parts.Add(content.TextContent);
                }
                else if (content.FrameType == RenderTreeFrameType.Markup)
                {
                    parts.Add(content.MarkupContent);
                }

                cursor++;
            }

            string text = string.Concat(parts).Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                labels.Add(text);
            }
        }

        return labels.ToArray();
    }

    internal static bool HasNavHref(WebStatusRenderingTests.RenderSnapshot snapshot, string href)
    {
        return snapshot.Frames.Any(frame =>
            frame.FrameType == RenderTreeFrameType.Attribute
            && string.Equals(frame.AttributeName, "href", StringComparison.Ordinal)
            && string.Equals(frame.AttributeValue?.ToString(), href, StringComparison.Ordinal));
    }

    internal static async Task<ComposedOverviewConsole> RenderOverviewInsideMainLayoutAsync(
        IProcessAssetsWebStatus status,
        ProcessingState? state = null)
    {
        state ??= new ProcessingState();
        var overview = WebStatusRenderingTests.CreateDashboard(status, state);
        var layout = WebStatusRenderingTests.CreateTopBar(status);

        string tempRoot = Path.Combine(Path.GetTempPath(), "admin-console-compose-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var dataSource = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=immich;Username=immich;Password=not-used;Pooling=false;Timeout=1;Command Timeout=1");
        var db = new ImmichDbRepository(dataSource, NullLogger<ImmichDbRepository>.Instance);
        var skipped = new SkippedAssetsRepository(NullLogger<SkippedAssetsRepository>.Instance, tempRoot);
        var coordinator = new IdleManualCoordinator();

        var renderer = new WebStatusRenderingTests.ComponentRenderer(services =>
        {
            services.AddSingleton(state);
            services.AddSingleton<IManualProcessingRunCoordinator>(coordinator);
            services.AddSingleton(status);
            services.AddSingleton<IProcessAssetsWebStatus>(status);
            services.AddSingleton(db);
            services.AddSingleton(skipped);
            services.AddSingleton<IComponentActivator>(new FixedInstanceActivator(overview));
        });

        RenderFragment body = builder =>
        {
            builder.OpenComponent(0, overview.GetType());
            builder.CloseComponent();
        };

        await renderer.AttachAsync(
            layout,
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(LayoutComponentBase.Body)] = body
            }));

        return new ComposedOverviewConsole(renderer, dataSource, tempRoot);
    }

    internal sealed class ComposedOverviewConsole : IAsyncDisposable
    {
        private readonly NpgsqlDataSource _dataSource;
        private readonly string _tempRoot;

        internal ComposedOverviewConsole(
            WebStatusRenderingTests.ComponentRenderer renderer,
            NpgsqlDataSource dataSource,
            string tempRoot)
        {
            Renderer = renderer;
            _dataSource = dataSource;
            _tempRoot = tempRoot;
        }

        internal WebStatusRenderingTests.ComponentRenderer Renderer { get; }

        internal Task<WebStatusRenderingTests.RenderSnapshot> ReadFlattenedAsync()
        {
            return Renderer.ReadFlattenedAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Renderer.DisposeAsync();
            await _dataSource.DisposeAsync();
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
    }

    private sealed class IdleManualCoordinator : IManualProcessingRunCoordinator
    {
        public Task<ProcessingRunAdmissionResult> TriggerManualAsync()
        {
            return Task.FromResult(ProcessingRunAdmissionResult.Accepted);
        }

        public Task? StopActiveRun()
        {
            return null;
        }

        public bool CancelActiveRun()
        {
            return false;
        }
    }

    private sealed class FixedInstanceActivator(IComponent instance) : IComponentActivator
    {
        public IComponent CreateInstance(Type componentType)
        {
            if (componentType == instance.GetType()
                || componentType.IsInstanceOfType(instance))
            {
                return instance;
            }

            return (IComponent)(Activator.CreateInstance(componentType)
                ?? throw new InvalidOperationException($"Could not create '{componentType}'."));
        }
    }
}

internal sealed class AdminConsoleCityMatchingFixture : IDisposable
{
    private readonly string _root;

    private AdminConsoleCityMatchingFixture(string root)
    {
        _root = root;
    }

    public static AdminConsoleCityMatchingFixture Create()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string defaults = Path.Combine(root, "bundled", "defaults");
        string config = Path.Combine(root, "config");
        Directory.CreateDirectory(defaults);
        Directory.CreateDirectory(config);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "data", "city-resolver-profiles.json"),
            Path.Combine(defaults, "city-resolver-profiles.json"));
        return new AdminConsoleCityMatchingFixture(root);
    }

    public ImmichReverseGeo.Web.Components.Pages.CityResolver CreatePage()
    {
        var page = new ImmichReverseGeo.Web.Components.Pages.CityResolver();
        SetInjected(
            page,
            "Config",
            new ConfigService(NullLogger<ConfigService>.Instance, Path.Combine(_root, "config")));
        SetInjected(
            page,
            "CountryCodes",
            CountryCodeService.CreateForTest(Path.Combine(AppContext.BaseDirectory, "data")));
        SetInjected(
            page,
            "CityResolverCatalog",
            new CityResolverProfileCatalogService(
                NullLogger<CityResolverProfileCatalogService>.Instance,
                new StorageOptions(
                    DataDir: Path.Combine(_root, "data"),
                    BundledDataDir: Path.Combine(_root, "bundled"))));
        return page;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static void SetInjected(object component, string name, object value)
    {
        component.GetType()
            .GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(component, value);
    }
}

internal sealed class AdminConsoleSettingsPageFixture : IAsyncDisposable
{
    private readonly string _root;
    private readonly NpgsqlDataSource _dataSource;
    private readonly WebStatusRenderingTests.ComponentRenderer _renderer;

    private AdminConsoleSettingsPageFixture(
        string root,
        NpgsqlDataSource dataSource,
        WebStatusRenderingTests.ComponentRenderer renderer)
    {
        _root = root;
        _dataSource = dataSource;
        _renderer = renderer;
    }

    public static async Task<AdminConsoleSettingsPageFixture> CreateAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "admin-console-settings-" + Guid.NewGuid().ToString("N"));
        string configDirectory = Path.Combine(root, "config");
        string dataDirectory = Path.Combine(root, "data");
        Directory.CreateDirectory(configDirectory);
        Directory.CreateDirectory(dataDirectory);

        var composition = ApplicationCompositionContext.Create(
            CompositionEnvironment.Development,
            root,
            dataDirectory,
            configDirectory,
            DeploymentMode.Standard);
        var config = new ConfigService(NullLogger<ConfigService>.Instance, configDirectory);
        await config.SaveConfigAsync(new AppConfig());
        var dataSource = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=immich;Username=immich;Password=not-used;Pooling=false;Timeout=1;Command Timeout=1");
        var page = new ImmichReverseGeo.Web.Components.Pages.Settings();
        SetInjected(page, "Config", config);
        SetInjected(page, "Db", new ImmichDbRepository(dataSource, NullLogger<ImmichDbRepository>.Instance));
        SetInjected(page, "Composition", composition);
        SetInjected(
            page,
            "Appearance",
            new AppearanceApplier(new NoopAppearanceDocument(), new NoopBrowserColorScheme()));
        var renderer = new WebStatusRenderingTests.ComponentRenderer();
        await renderer.AttachAsync(page);
        return new AdminConsoleSettingsPageFixture(root, dataSource, renderer);
    }

    public Task<WebStatusRenderingTests.RenderSnapshot> ReadAsync()
    {
        return _renderer.ReadFlattenedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _renderer.DisposeAsync();
        await _dataSource.DisposeAsync();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static void SetInjected(object component, string name, object value)
    {
        component.GetType()
            .GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(component, value);
    }
}
