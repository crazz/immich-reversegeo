using ImmichReverseGeo.Core.ApplicationRole;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Web.Composition;
using ImmichReverseGeo.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using System.Text.Json;

namespace ImmichReverseGeo.Tests;

[TestClass]
public sealed class AppearancePreferenceTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup() => _tempDir = Path.Combine(Path.GetTempPath(), "appearance-" + Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task FirstVisit_UsesAuto_AndPaletteMatchesBrowserScheme()
    {
        Directory.CreateDirectory(_tempDir);
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, "settings.json"),
            """
            {
              "schedule": {
                "cron": "0 * * * *",
                "enabled": true
              },
              "processing": {
                "batchSize": 50
              }
            }
            """);

        var configService = new ConfigService(NullLogger<ConfigService>.Instance, configDir: _tempDir);
        var config = await configService.GetConfigAsync();

        var document = new RecordingAppearanceDocument();
        var browser = new FixedBrowserColorScheme("dark");
        var applier = new AppearanceApplier(document, browser);
        await applier.InitializeAsync(config.Appearance.Mode);

        Assert.AreEqual(AppearanceModes.Auto, config.Appearance.Mode);
        Assert.AreEqual(AppearanceModes.Auto, applier.SavedMode);
        Assert.AreEqual(AppearanceThemes.Dark, document.DataTheme);
    }

    [TestMethod]
    public async Task OperatorSelectsDark_AppliesImmediatelyAndPersistsWithoutSaveAllSettings()
    {
        Directory.CreateDirectory(_tempDir);
        var configService = new ConfigService(NullLogger<ConfigService>.Instance, configDir: _tempDir);
        await configService.SeedConfigAsync(new AppConfig
        {
            Appearance = new AppearanceConfig { Mode = AppearanceModes.Light },
            Schedule = new ScheduleConfig { Cron = "15 * * * *", Enabled = true },
            Processing = new ProcessingConfig { BatchSize = 25 }
        });

        var document = new RecordingAppearanceDocument();
        var browser = new FixedBrowserColorScheme("light");
        var applier = new AppearanceApplier(document, browser);
        await applier.InitializeAsync(AppearanceModes.Light);
        Assert.AreEqual(AppearanceThemes.Light, document.DataTheme);

        bool saved = await applier.TryChangeModeAsync(
            AppearanceModes.Dark,
            mode => configService.SaveAppearanceModeAsync(mode));

        Assert.IsTrue(saved);
        Assert.AreEqual(AppearanceThemes.Dark, document.DataTheme);
        Assert.AreEqual(AppearanceModes.Dark, applier.SavedMode);

        var reloaded = await configService.GetConfigAsync();
        Assert.AreEqual(AppearanceModes.Dark, reloaded.Appearance.Mode);
        Assert.AreEqual("15 * * * *", reloaded.Schedule.Cron);
        Assert.AreEqual(25, reloaded.Processing.BatchSize);

        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_tempDir, "settings.json")));
        Assert.AreEqual("dark", json.RootElement.GetProperty("appearance").GetProperty("mode").GetString());
        Assert.AreEqual("15 * * * *", json.RootElement.GetProperty("schedule").GetProperty("cron").GetString());
        Assert.AreEqual(25, json.RootElement.GetProperty("processing").GetProperty("batchSize").GetInt32());
    }

    [TestMethod]
    public async Task Auto_FollowsLaterBrowserSchemeChangeWithoutReload()
    {
        var document = new RecordingAppearanceDocument();
        var browser = new MutableBrowserColorScheme("light");
        var applier = new AppearanceApplier(document, browser);
        await applier.InitializeAsync(AppearanceModes.Auto);
        Assert.AreEqual(AppearanceThemes.Light, document.DataTheme);

        browser.ChangeScheme("dark");

        Assert.AreEqual(AppearanceThemes.Dark, document.DataTheme);
        Assert.AreEqual(AppearanceModes.Auto, applier.SavedMode);
    }

    [TestMethod]
    public async Task AppearanceCannotBeSaved_KeepsLightShowsErrorDoesNotPreviewDark()
    {
        Directory.CreateDirectory(_tempDir);
        string configDirectory = Path.Combine(_tempDir, "config");
        string dataDirectory = Path.Combine(_tempDir, "data");
        Directory.CreateDirectory(configDirectory);
        Directory.CreateDirectory(dataDirectory);

        var configService = new ConfigService(NullLogger<ConfigService>.Instance, configDir: configDirectory);
        await configService.SeedConfigAsync(new AppConfig
        {
            Appearance = new AppearanceConfig { Mode = AppearanceModes.Light }
        });

        var document = new RecordingAppearanceDocument();
        var browser = new FixedBrowserColorScheme("dark");
        var applier = new AppearanceApplier(document, browser);
        await applier.InitializeAsync(AppearanceModes.Light);

        var composition = ApplicationCompositionContext.Create(
            CompositionEnvironment.Development,
            _tempDir,
            dataDirectory,
            configDirectory,
            DeploymentMode.Standard);
        await using var dataSource = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=immich;Username=immich;Password=not-used;Pooling=false;Timeout=1;Command Timeout=1");

        var page = new ImmichReverseGeo.Web.Components.Pages.Settings();
        SetInjected(page, "Config", configService);
        SetInjected(page, "Db", new ImmichDbRepository(dataSource, NullLogger<ImmichDbRepository>.Instance));
        SetInjected(page, "Composition", composition);
        SetInjected(page, "Appearance", applier);

        await using var renderer = new WebStatusRenderingTests.ComponentRenderer();
        await renderer.AttachAsync(page);
        var draft = (AppConfig)typeof(ImmichReverseGeo.Web.Components.Pages.Settings)
            .GetField("_cfg", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(page)!;
        draft.Processing.BatchSize = 117;

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await InvokeAppearanceChangeAsync(page, AppearanceModes.Dark, _ => throw new IOException("cannot write settings"));
            Task nextRender = renderer.NextRenderAsync();
            typeof(ComponentBase)
                .GetMethod("StateHasChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(page, null);
            await nextRender;
        });
        WebStatusRenderingTests.RenderSnapshot rendered = await renderer.ReadFlattenedAsync();

        Assert.AreEqual(AppearanceThemes.Light, document.DataTheme);
        Assert.AreEqual(AppearanceModes.Light, applier.SavedMode);
        Assert.AreEqual(AppearanceModes.Light, GetAppearanceSelection(page));
        Assert.IsTrue(rendered.HasCssClass("alert", "alert-error"));
        StringAssert.Contains(rendered.Text, "cannot write settings");
        Assert.IsTrue(rendered.HasAttribute("role", "alert"));
        Assert.AreEqual(117, draft.Processing.BatchSize);
        Assert.AreEqual(AppearanceModes.Light, (await configService.GetConfigAsync()).Appearance.Mode);
    }

    [TestMethod]
    public async Task FirstVisitAppearanceCannotBeSaved_KeepsAutoMatchingBrowserScheme()
    {
        Directory.CreateDirectory(_tempDir);
        string configDirectory = Path.Combine(_tempDir, "config");
        string dataDirectory = Path.Combine(_tempDir, "data");
        Directory.CreateDirectory(configDirectory);
        Directory.CreateDirectory(dataDirectory);

        await File.WriteAllTextAsync(
            Path.Combine(configDirectory, "settings.json"),
            """
            {
              "schedule": { "cron": "0 * * * *", "enabled": true },
              "processing": { "batchSize": 50 }
            }
            """);

        var configService = new ConfigService(NullLogger<ConfigService>.Instance, configDir: configDirectory);
        var config = await configService.GetConfigAsync();
        Assert.AreEqual(AppearanceModes.Auto, config.Appearance.Mode);

        var document = new RecordingAppearanceDocument();
        var browser = new FixedBrowserColorScheme("light");
        var applier = new AppearanceApplier(document, browser);
        await applier.InitializeAsync(config.Appearance.Mode);
        Assert.AreEqual(AppearanceThemes.Light, document.DataTheme);

        var composition = ApplicationCompositionContext.Create(
            CompositionEnvironment.Development,
            _tempDir,
            dataDirectory,
            configDirectory,
            DeploymentMode.Standard);
        await using var dataSource = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=immich;Username=immich;Password=not-used;Pooling=false;Timeout=1;Command Timeout=1");

        var page = new ImmichReverseGeo.Web.Components.Pages.Settings();
        SetInjected(page, "Config", configService);
        SetInjected(page, "Db", new ImmichDbRepository(dataSource, NullLogger<ImmichDbRepository>.Instance));
        SetInjected(page, "Composition", composition);
        SetInjected(page, "Appearance", applier);

        await using var renderer = new WebStatusRenderingTests.ComponentRenderer();
        await renderer.AttachAsync(page);

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await InvokeAppearanceChangeAsync(page, AppearanceModes.Dark, _ => throw new IOException("cannot write settings"));
            Task nextRender = renderer.NextRenderAsync();
            typeof(ComponentBase)
                .GetMethod("StateHasChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(page, null);
            await nextRender;
        });
        WebStatusRenderingTests.RenderSnapshot rendered = await renderer.ReadFlattenedAsync();

        Assert.AreEqual(AppearanceModes.Auto, applier.SavedMode);
        Assert.AreEqual(AppearanceModes.Auto, GetAppearanceSelection(page));
        Assert.AreEqual(AppearanceThemes.Light, document.DataTheme);
        Assert.IsTrue(rendered.HasCssClass("alert", "alert-error"));
        StringAssert.Contains(rendered.Text, "cannot write settings");
    }

    [TestMethod]
    public async Task AutoWithNoBrowserScheme_ResolvesToLight()
    {
        var document = new RecordingAppearanceDocument();
        var browser = new FixedBrowserColorScheme(null);
        var applier = new AppearanceApplier(document, browser);
        await applier.InitializeAsync(AppearanceModes.Auto);

        Assert.AreEqual(AppearanceModes.Auto, applier.SavedMode);
        Assert.AreEqual(AppearanceThemes.Light, document.DataTheme);
    }

    [TestMethod]
    public async Task ExplicitDark_DoesNotFollowBrowserSchemeChanges()
    {
        var document = new RecordingAppearanceDocument();
        var browser = new MutableBrowserColorScheme("light");
        var applier = new AppearanceApplier(document, browser);
        await applier.InitializeAsync(AppearanceModes.Dark);
        Assert.AreEqual(AppearanceThemes.Dark, document.DataTheme);

        browser.ChangeScheme("light");

        Assert.AreEqual(AppearanceThemes.Dark, document.DataTheme);
        Assert.AreEqual(AppearanceModes.Dark, applier.SavedMode);
    }

    [TestMethod]
    public async Task InitializeAsync_AfterConcurrentSuccessfulChange_DoesNotClobberSavedMode()
    {
        var document = new RecordingAppearanceDocument();
        var browser = new GateableBrowserColorScheme("light");
        var applier = new AppearanceApplier(document, browser);

        Task initTask = applier.InitializeAsync(AppearanceModes.Auto);
        await browser.WaitUntilEnsureReadyEnteredAsync();

        bool saved = await applier.TryChangeModeAsync(
            AppearanceModes.Dark,
            _ => Task.CompletedTask);

        Assert.IsTrue(saved);
        Assert.AreEqual(AppearanceModes.Dark, applier.SavedMode);
        Assert.AreEqual(AppearanceThemes.Dark, document.DataTheme);

        browser.ReleaseEnsureReady();
        await initTask;

        Assert.AreEqual(AppearanceModes.Dark, applier.SavedMode);
        Assert.AreEqual(AppearanceThemes.Dark, document.DataTheme);
    }

    [TestMethod]
    public async Task TryChangeModeAsync_AutoBeforeInitialize_ResolvesAgainstBrowserScheme()
    {
        var document = new RecordingAppearanceDocument();
        var browser = new LazyReadyBrowserColorScheme("dark");
        var applier = new AppearanceApplier(document, browser);

        Assert.IsNull(browser.CurrentScheme);

        bool saved = await applier.TryChangeModeAsync(
            AppearanceModes.Auto,
            _ => Task.CompletedTask);

        Assert.IsTrue(saved);
        Assert.AreEqual(AppearanceModes.Auto, applier.SavedMode);
        Assert.AreEqual(AppearanceThemes.Dark, document.DataTheme);
        Assert.AreEqual("dark", browser.CurrentScheme);
    }

    private static void SetInjected(object component, string name, object value)
    {
        component.GetType()
            .GetProperty(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(component, value);
    }

    private static Task InvokeAppearanceChangeAsync(
        ImmichReverseGeo.Web.Components.Pages.Settings page,
        string mode,
        Func<string, Task> persistAsync)
    {
        var method = typeof(ImmichReverseGeo.Web.Components.Pages.Settings)
            .GetMethod("ChangeAppearanceAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Settings.ChangeAppearanceAsync was not found.");
        return (Task)method.Invoke(page, [mode, persistAsync])!;
    }

    private static string GetAppearanceSelection(ImmichReverseGeo.Web.Components.Pages.Settings page)
    {
        var field = typeof(ImmichReverseGeo.Web.Components.Pages.Settings)
            .GetField("_appearanceMode", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Settings._appearanceMode was not found.");
        return (string)field.GetValue(page)!;
    }

    private sealed class RecordingAppearanceDocument : IAppearanceDocument
    {
        public string? DataTheme { get; private set; }

        public Task SetDataThemeAsync(string theme)
        {
            DataTheme = theme;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedBrowserColorScheme : IBrowserColorScheme
    {
        public FixedBrowserColorScheme(string? scheme) => CurrentScheme = scheme;

        public string? CurrentScheme { get; }

        public Task EnsureReadyAsync() => Task.CompletedTask;

        public IDisposable Subscribe(Action<string?> onChanged) => new NoopDisposable();

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private sealed class MutableBrowserColorScheme : IBrowserColorScheme
    {
        private Action<string?>? _onChanged;

        public MutableBrowserColorScheme(string? scheme) => CurrentScheme = scheme;

        public string? CurrentScheme { get; private set; }

        public Task EnsureReadyAsync() => Task.CompletedTask;

        public IDisposable Subscribe(Action<string?> onChanged)
        {
            _onChanged = onChanged;
            return new Subscription(() => _onChanged = null);
        }

        public void ChangeScheme(string? scheme)
        {
            CurrentScheme = scheme;
            _onChanged?.Invoke(scheme);
        }

        private sealed class Subscription(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }

    private sealed class GateableBrowserColorScheme : IBrowserColorScheme
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _ensureReadyCalls;

        public GateableBrowserColorScheme(string? scheme) => CurrentScheme = scheme;

        public string? CurrentScheme { get; }

        public async Task EnsureReadyAsync()
        {
            if (Interlocked.Increment(ref _ensureReadyCalls) == 1)
            {
                _entered.TrySetResult();
                await _release.Task.ConfigureAwait(false);
                return;
            }
        }

        public Task WaitUntilEnsureReadyEnteredAsync() => _entered.Task;

        public void ReleaseEnsureReady() => _release.TrySetResult();

        public IDisposable Subscribe(Action<string?> onChanged) => new NoopDisposable();

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private sealed class LazyReadyBrowserColorScheme : IBrowserColorScheme
    {
        private readonly string? _schemeWhenReady;

        public LazyReadyBrowserColorScheme(string? schemeWhenReady) => _schemeWhenReady = schemeWhenReady;

        public string? CurrentScheme { get; private set; }

        public Task EnsureReadyAsync()
        {
            CurrentScheme = _schemeWhenReady;
            return Task.CompletedTask;
        }

        public IDisposable Subscribe(Action<string?> onChanged) => new NoopDisposable();

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
