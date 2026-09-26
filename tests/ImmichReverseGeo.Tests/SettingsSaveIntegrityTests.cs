using System.Collections;
using System.Reflection;
using System.Text.Json;
using ImmichReverseGeo.Core.ApplicationRole;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Web.Components.Pages;
using ImmichReverseGeo.Web.Composition;
using ImmichReverseGeo.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests;

[TestClass]
public sealed class SettingsSaveIntegrityTests
{
    private string _directory = null!;
    private ConfigService _service = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), "settings-integrity-" + Guid.NewGuid().ToString("N"));
        _service = new ConfigService(NullLogger<ConfigService>.Instance, _directory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public async Task InvalidBatchSize_PreservesEverySavedGroupAndEditableDraftWithAccessibleFeedback(int batchSize)
    {
        await _service.SeedConfigAsync(new AppConfig());
        var page = new Settings();
        SetInjected(page, "Config", _service);
        SetInjected(page, "Composition", ApplicationCompositionContext.Create(
            CompositionEnvironment.Development, _directory, _directory, _directory, DeploymentMode.Standard));
        await using var renderer = new WebStatusRenderingTests.ComponentRenderer();
        await renderer.AttachAsync(page);
        await renderer.Dispatcher.InvokeAsync(() => SaveAsync(page));
        Assert.IsTrue(GetField<bool>(page, "_saved"));
        byte[] prior = await File.ReadAllBytesAsync(Path.Combine(_directory, "settings.json"));
        var draft = GetField<AppConfig>(page, "_cfg");
        draft.Processing.BatchSize = batchSize;
        draft.Processing.VerboseLogging = true;
        draft.Processing.UseAirportInfrastructure = false;
        SetField(page, "_schedule", ScheduleEditorState.FromCron("35 * * * *"));

        await renderer.Dispatcher.InvokeAsync(() => SaveAsync(page));
        await RenderPageAsync(renderer, page);
        var rendered = await renderer.ReadFlattenedAsync();

        Assert.IsFalse(GetField<bool>(page, "_saved"));
        Assert.IsFalse(rendered.Text.Contains("Settings saved.", StringComparison.Ordinal));
        StringAssert.Contains(rendered.Text, "Batch Size must be positive.");
        Assert.IsTrue(rendered.HasAttribute("role", "alert"));
        Assert.IsTrue(rendered.HasAttribute("aria-invalid", "true"));
        Assert.IsTrue(rendered.HasAttribute("aria-describedby", "batch-size-error"));
        Assert.IsTrue(rendered.HasAttribute("id", "batch-size-error"));
        CollectionAssert.AreEqual(prior, await File.ReadAllBytesAsync(Path.Combine(_directory, "settings.json")));
        Assert.AreEqual(batchSize, draft.Processing.BatchSize);
        Assert.IsTrue(draft.Processing.VerboseLogging);
        Assert.IsFalse(draft.Processing.UseAirportInfrastructure);
        Assert.AreEqual("35 * * * *", draft.Schedule.Cron);
    }

    [TestMethod]
    [DataRow(0, 1)]
    [DataRow(-1, int.MaxValue)]
    public async Task ExistingInvalidBatchSize_RemainsVisibleAcrossIndependentSavesAndCanBeRepaired(int invalid, int corrected)
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "settings.json");
        string original = $$$"""{"processing":{"batchSize":{{{invalid}}}}}""";
        await File.WriteAllTextAsync(path, original);
        var page = new Settings();
        SetInjected(page, "Config", _service);
        SetInjected(page, "Composition", ApplicationCompositionContext.Create(
            CompositionEnvironment.Development, _directory, _directory, _directory, DeploymentMode.Standard));
        await using var appearance = new AppearanceApplier(new NoopAppearanceDocument(), new NoopBrowserColorScheme());
        await appearance.InitializeAsync(AppearanceModes.Auto);
        SetInjected(page, "Appearance", appearance);
        await using var renderer = new WebStatusRenderingTests.ComponentRenderer();
        await renderer.AttachAsync(page);
        var draft = GetField<AppConfig>(page, "_cfg");
        Assert.AreEqual(invalid, draft.Processing.BatchSize);
        Assert.IsTrue((await renderer.ReadFlattenedAsync()).HasAttribute("value", invalid.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.AreEqual(original, await File.ReadAllTextAsync(path));

        await renderer.Dispatcher.InvokeAsync(() => (Task)typeof(Settings)
            .GetMethod("OnAppearanceChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(page, [new ChangeEventArgs { Value = AppearanceModes.Dark }])!);

        Assert.AreEqual(AppearanceModes.Dark, appearance.SavedMode);
        var afterAppearance = await _service.GetConfigAsync();
        Assert.AreEqual(AppearanceModes.Dark, afterAppearance.Appearance.Mode);
        Assert.AreEqual(invalid, afterAppearance.Processing.BatchSize);
        var city = await CreateCityPageAsync();
        AddCountryOverride(city);
        await SaveAsync(city);
        Assert.IsTrue(GetField<bool>(city, "_saved"));
        Assert.AreEqual(invalid, (await _service.GetConfigAsync()).Processing.BatchSize);
        draft.Processing.BatchSize = corrected;

        await renderer.Dispatcher.InvokeAsync(() => SaveAsync(page));
        await RenderPageAsync(renderer, page);

        Assert.IsTrue(GetField<bool>(page, "_saved"));
        Assert.IsNull(GetField<string?>(page, "_saveError"));
        Assert.IsFalse((await renderer.ReadFlattenedAsync()).HasAttribute("aria-invalid", "true"));
        var reloaded = await new ConfigService(NullLogger<ConfigService>.Instance, _directory).GetConfigAsync();
        Assert.AreEqual(corrected, reloaded.Processing.BatchSize);
        Assert.AreEqual(AppearanceModes.Dark, reloaded.Appearance.Mode);
        CollectionAssert.AreEqual(new[] { "localadmin" }, reloaded.Processing.CityResolver.CountryOverrides["FRA"].PreferredSubtypes);
        Assert.AreEqual(corrected, (await ((IProcessingRunConfiguration)_service).GetConfigAsync()).Processing.BatchSize);
    }

    [TestMethod]
    [DataRow("60 * * * *")]
    [DataRow("*/0 * * * *")]
    [DataRow("0 24 * * *")]
    [DataRow("٠ ٢ * * *")]
    public async Task InvalidSavedSchedule_UnrelatedProcessingSavePreservesOriginalCron(string cron)
    {
        await _service.SeedConfigAsync(new AppConfig
        {
            Schedule = new ScheduleConfig { Enabled = true, Cron = cron }
        });
        var settings = await CreateSettingsPageAsync();
        GetField<AppConfig>(settings, "_cfg").Processing.BatchSize = 73;

        await SaveAsync(settings);

        var reloaded = await new ConfigService(NullLogger<ConfigService>.Instance, _directory).GetConfigAsync();
        Assert.AreEqual(cron, reloaded.Schedule.Cron);
        Assert.IsTrue(reloaded.Schedule.Enabled);
        Assert.AreEqual(73, reloaded.Processing.BatchSize);
        Assert.IsTrue(GetField<bool>(settings, "_saved"));
    }

    [TestMethod]
    public async Task OlderCityMatchingPage_SavesCountryOverrideWithoutRevertingNewerSettings()
    {
        var settings = await CreateSettingsPageAsync();
        var city = await CreateCityPageAsync();
        SetField(settings, "_schedule", ScheduleEditorState.FromCron("15 * * * *"));
        GetField<AppConfig>(settings, "_cfg").Processing.BatchSize = 91;
        await SaveAsync(settings);

        AddCountryOverride(city);
        await SaveAsync(city);

        var saved = await _service.GetConfigAsync();
        Assert.AreEqual(91, saved.Processing.BatchSize);
        Assert.AreEqual("15 * * * *", saved.Schedule.Cron);
        CollectionAssert.AreEqual(new[] { "localadmin" }, saved.Processing.CityResolver.CountryOverrides["FRA"].PreferredSubtypes);
        Assert.IsTrue(GetField<bool>(city, "_saved"));
    }

    [TestMethod]
    public async Task OlderSettingsPage_PreservesNewerAppearanceAndCityMatching()
    {
        var settings = await CreateSettingsPageAsync();
        SetField(settings, "_schedule", ScheduleEditorState.FromCron("25 * * * *"));
        GetField<AppConfig>(settings, "_cfg").Processing.BatchSize = 73;
        await _service.SaveAppearanceModeAsync(AppearanceModes.Dark);
        var city = await CreateCityPageAsync();
        AddCountryOverride(city);
        await SaveAsync(city);

        await SaveAsync(settings);

        var saved = await _service.GetConfigAsync();
        Assert.AreEqual(AppearanceModes.Dark, saved.Appearance.Mode);
        Assert.IsTrue(saved.Processing.CityResolver.CountryOverrides.ContainsKey("FRA"));
        Assert.AreEqual(73, saved.Processing.BatchSize);
        Assert.AreEqual("25 * * * *", saved.Schedule.Cron);
        Assert.IsTrue(GetField<bool>(settings, "_saved"));
    }

    [TestMethod]
    public async Task IndependentSavesOverlap_BothGroupsRemainSaved()
    {
        var store = new GateableStore();
        var service = new ConfigService(NullLogger<ConfigService>.Instance, store);
        var edited = await service.GetConfigAsync();
        edited.Processing.BatchSize = 87;
        Task settings = service.SaveSettingsAsync(SettingsUpdate.FromConfig(edited));
        await store.PublicationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task appearance = service.SaveAppearanceModeAsync(AppearanceModes.Dark);
        store.ReleasePublication.TrySetResult();
        await Task.WhenAll(settings, appearance).WaitAsync(TimeSpan.FromSeconds(5));

        var saved = await service.GetConfigAsync();
        Assert.AreEqual(87, saved.Processing.BatchSize);
        Assert.AreEqual(AppearanceModes.Dark, saved.Appearance.Mode);
    }

    [TestMethod]
    public async Task SameGroupSaves_LaterPublicationWinsDespiteReversedCallerConfirmation()
    {
        var first = await _service.GetConfigAsync();
        var second = await _service.GetConfigAsync();
        first.Schedule.Cron = "15 * * * *";
        second.Schedule.Cron = "25 * * * *";
        first.Processing.BatchSize = 71;
        second.Processing.BatchSize = 82;
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirm = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task FirstCallerAsync()
        {
            await _service.SaveSettingsAsync(SettingsUpdate.FromConfig(first));
            published.TrySetResult();
            await confirm.Task;
        }

        Task firstCaller = FirstCallerAsync();
        await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await _service.SaveAppearanceModeAsync(AppearanceModes.Dark);
        var city = await CreateCityPageAsync();
        AddCountryOverride(city);
        await SaveAsync(city);
        await _service.SaveSettingsAsync(SettingsUpdate.FromConfig(second));
        confirm.TrySetResult();
        await firstCaller;

        var saved = await _service.GetConfigAsync();
        Assert.AreEqual(82, saved.Processing.BatchSize);
        Assert.AreEqual("25 * * * *", saved.Schedule.Cron);
        Assert.AreEqual(AppearanceModes.Dark, saved.Appearance.Mode);
        CollectionAssert.AreEqual(new[] { "localadmin" }, saved.Processing.CityResolver.CountryOverrides["FRA"].PreferredSubtypes);
    }

    [TestMethod]
    public async Task IndependentReaderOpenedDuringSave_ObservesACompletePriorDocument()
    {
        var initial = new AppConfig();
        initial.Schedule.Cron = new string('a', 200);
        await _service.SeedConfigAsync(initial);
        string path = Path.Combine(_directory, "settings.json");
        byte[] prior = await File.ReadAllBytesAsync(path);
        await using var processReader = await SettingsReaderProcess.StartAsync(path);
        await using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1, true);
        var observed = new MemoryStream();
        byte[] prefix = new byte[100];
        await reader.ReadExactlyAsync(prefix);
        observed.Write(prefix);
        initial.Schedule.Cron = new string('b', 300);
        Exception? saveError = null;
        try
        {
            await _service.SaveSettingsAsync(SettingsUpdate.FromConfig(initial));
        }
        catch (Exception error)
        {
            saveError = error;
        }

        await reader.CopyToAsync(observed);

        Assert.IsNull(saveError, "A reader sharing replacement must not block publication.");
        CollectionAssert.AreEqual(prior, observed.ToArray());
        Assert.AreEqual(System.Text.Encoding.UTF8.GetString(prior), await processReader.FinishAsync());
        Assert.AreEqual(initial.Schedule.Cron, (await _service.GetConfigAsync()).Schedule.Cron);
    }

    [TestMethod]
    public async Task FailedPublication_PreservesPriorDocumentAndBothPageDraftsWithSafeErrors()
    {
        await _service.SeedConfigAsync(new AppConfig());
        string path = Path.Combine(_directory, "settings.json");
        byte[] prior = await File.ReadAllBytesAsync(path);
        var settings = await CreateSettingsPageAsync();
        var city = await CreateCityPageAsync();
        GetField<AppConfig>(settings, "_cfg").Processing.BatchSize = 97;
        AddCountryOverride(city);
        var failing = new ConfigService(NullLogger<ConfigService>.Instance, new FailingStore(new SettingsDocumentStore(path)));
        SetInjected(settings, "Config", failing);
        SetInjected(city, "Config", failing);

        await SaveAsync(settings);
        await SaveAsync(city);

        CollectionAssert.AreEqual(prior, await File.ReadAllBytesAsync(path));
        Assert.AreEqual(97, GetField<AppConfig>(settings, "_cfg").Processing.BatchSize);
        Assert.AreEqual(1, GetField<IList>(city, "_countryEditors").Count);
        foreach (object page in new object[] { settings, city })
        {
            Assert.IsFalse(GetField<bool>(page, "_saved"));
            Assert.AreEqual("Unable to save settings. Check configuration storage access and free space, then retry.", GetField<string>(page, "_saveError"));
        }
    }

    [TestMethod]
    public async Task FirstSaveFails_LeavesNoSavedDocumentAndLaterReadsUseDefaults()
    {
        Directory.CreateDirectory(_directory);
        string blockedDirectory = Path.Combine(_directory, "blocked");
        await File.WriteAllTextAsync(blockedDirectory, "directory obstruction");
        var service = new ConfigService(NullLogger<ConfigService>.Instance, blockedDirectory);
        var update = SettingsUpdate.FromConfig(new AppConfig()) with { BatchSize = 89 };

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.SaveSettingsAsync(update));

        Assert.IsFalse(File.Exists(Path.Combine(blockedDirectory, "settings.json")));
        File.Delete(blockedDirectory);
        Assert.AreEqual(50, (await service.GetConfigAsync()).Processing.BatchSize);
        Assert.IsFalse(File.Exists(Path.Combine(blockedDirectory, "settings.json")));
    }

    [TestMethod]
    public async Task SaveCancelledBeforePublication_PreservesPriorSettingsAndDoesNotSucceed()
    {
        await _service.SeedConfigAsync(new AppConfig());
        byte[] prior = await File.ReadAllBytesAsync(Path.Combine(_directory, "settings.json"));
        var store = new GateableStore(prior);
        var service = new ConfigService(NullLogger<ConfigService>.Instance, store);
        using var cancellation = new CancellationTokenSource();
        var update = SettingsUpdate.FromConfig(await service.GetConfigAsync()) with { BatchSize = 79 };
        Task saving = service.SaveSettingsAsync(update, cancellation.Token);
        await store.PublicationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => saving);
        CollectionAssert.AreEqual(prior, await store.ReadAsync(CancellationToken.None));
        Assert.AreEqual(50, (await service.GetConfigAsync()).Processing.BatchSize);
    }

    [TestMethod]
    public async Task EditorSaveCancelledBeforePublication_PreservesDraftsAndClearsConfirmation()
    {
        await _service.SeedConfigAsync(new AppConfig());
        string path = Path.Combine(_directory, "settings.json");
        var settings = new Settings();
        SetInjected(settings, "Config", _service);
        SetInjected(settings, "Composition", ApplicationCompositionContext.Create(
            CompositionEnvironment.Development, _directory, _directory, _directory, DeploymentMode.Standard));
        using var cityFixture = AdminConsoleCityMatchingFixture.Create();
        var city = cityFixture.CreatePage();
        SetInjected(city, "Config", _service);

        foreach (ComponentBase page in new ComponentBase[] { settings, city })
        {
            await using var renderer = new WebStatusRenderingTests.ComponentRenderer();
            await renderer.AttachAsync(page);
            await renderer.Dispatcher.InvokeAsync(() => SaveAsync(page));
            Assert.IsTrue(GetField<bool>(page, "_saved"));
            await RenderPageAsync(renderer, page);
            StringAssert.Contains((await renderer.ReadFlattenedAsync()).Text.ToLowerInvariant(), "settings saved.");
            byte[] prior = await File.ReadAllBytesAsync(path);
            if (page is Settings)
            {
                GetField<AppConfig>(page, "_cfg").Processing.BatchSize = 79;
                SetField(page, "_schedule", ScheduleEditorState.FromCron("35 * * * *"));
            }
            else
            {
                AddCountryOverride(city);
            }

            using var cancellation = new CancellationTokenSource();
            var store = new CancellablePublicationStore(new SettingsDocumentStore(path), cancellation.Token);
            SetInjected(page, "Config", new ConfigService(NullLogger<ConfigService>.Instance, store));
            Task saving = renderer.Dispatcher.InvokeAsync(() => SaveAsync(page));
            await store.PublicationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await saving.WaitAsync(TimeSpan.FromSeconds(5));
            await RenderPageAsync(renderer, page);
            var rendered = await renderer.ReadFlattenedAsync();

            Assert.IsFalse(GetField<bool>(page, "_saved"));
            Assert.IsFalse(rendered.Text.Contains("settings saved.", StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(rendered.HasCssClass("alert", "alert-error"));
            Assert.IsTrue(rendered.HasAttribute("role", "alert"));
            CollectionAssert.AreEqual(prior, await File.ReadAllBytesAsync(path));
            if (page is Settings)
            {
                Assert.AreEqual(79, GetField<AppConfig>(page, "_cfg").Processing.BatchSize);
                Assert.AreEqual("35 * * * *", GetField<AppConfig>(page, "_cfg").Schedule.Cron);
            }
            else
            {
                var editors = GetField<IList>(page, "_countryEditors");
                Assert.AreEqual(1, editors.Count);
                object editor = editors[0]!;
                CollectionAssert.AreEqual(new[] { "localadmin" }, ((CityResolverProfileOverride)editor.GetType().GetProperty("Profile")!.GetValue(editor)!).PreferredSubtypes);
                Assert.IsFalse((await _service.GetConfigAsync()).Processing.CityResolver.CountryOverrides.ContainsKey("FRA"));
            }
        }
    }

    [TestMethod]
    public async Task UndecodableExistingDocument_IsPreservedAndProducesABoundedSettingsError()
    {
        var page = await CreateSettingsPageAsync();
        GetField<AppConfig>(page, "_cfg").Processing.BatchSize = 77;
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "settings.json");
        foreach (string document in new[] { "null", "[credential-canary", "{\"processing\":{\"batchSize\":\"credential-canary\"}}" })
        {
            await File.WriteAllTextAsync(path, document);

            await SaveAsync(page);

            Assert.IsFalse(GetField<bool>(page, "_saved"));
            Assert.AreEqual(document, await File.ReadAllTextAsync(path));
            Assert.AreEqual(77, GetField<AppConfig>(page, "_cfg").Processing.BatchSize);
            Assert.AreEqual("Unable to read settings. Check the saved settings file and configuration storage access.", GetField<string>(page, "_saveError"));
            var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => _service.GetConfigAsync());
            Assert.IsFalse(error.ToString().Contains("credential-canary", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public async Task UnreadableExistingPath_IsNotTreatedAsFirstRunAbsence()
    {
        string path = Path.Combine(_directory, "settings.json");
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, "preserve"), "existing content");

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => _service.GetConfigAsync());

        Assert.AreEqual("Unable to read settings. Check the saved settings file and configuration storage access.", error.Message);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => _service.SaveAppearanceModeAsync(AppearanceModes.Dark));
        Assert.AreEqual("existing content", await File.ReadAllTextAsync(Path.Combine(path, "preserve")));
    }

    [TestMethod]
    public async Task ReplacementPreservesExistingFileAccessRestrictions()
    {
        await _service.SeedConfigAsync(new AppConfig());
        string path = Path.Combine(_directory, "settings.json");
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(path, FileAttributes.ReadOnly);
            try
            {
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => _service.SaveAppearanceModeAsync(AppearanceModes.Dark));
                Assert.IsTrue(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
                Assert.AreEqual(AppearanceModes.Auto, (await _service.GetConfigAsync()).Appearance.Mode);
            }
            finally
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
        }
        else
        {
            const UnixFileMode restricted = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            File.SetUnixFileMode(path, restricted);

            await _service.SaveAppearanceModeAsync(AppearanceModes.Dark);

            Assert.AreEqual(restricted, File.GetUnixFileMode(path));
            Assert.AreEqual(AppearanceModes.Dark, (await _service.GetConfigAsync()).Appearance.Mode);
        }
    }

    [TestMethod]
    public async Task PostCommitDiagnosticFailure_DoesNotTurnPublishedSettingsIntoAFailedSave()
    {
        await _service.SeedConfigAsync(new AppConfig());
        var service = new ConfigService(new ThrowingLogger(), _directory);
        var page = await CreateSettingsPageAsync();
        SetInjected(page, "Config", service);
        GetField<AppConfig>(page, "_cfg").Processing.BatchSize = 93;

        await SaveAsync(page);

        Assert.IsTrue(GetField<bool>(page, "_saved"));
        Assert.IsNull(GetField<string?>(page, "_saveError"));
        Assert.AreEqual(93, (await _service.GetConfigAsync()).Processing.BatchSize);
    }

    [TestMethod]
    public async Task BrowserConfirmationInterrupted_AppearanceRemainsCommittedAfterReload()
    {
        await _service.SaveAppearanceModeAsync(AppearanceModes.Light);
        await using var appearance = new AppearanceApplier(new InterruptedDocument(), new NoopBrowserColorScheme());
        await appearance.InitializeAsync(AppearanceModes.Light);

        await Assert.ThrowsExactlyAsync<IOException>(() => appearance.TryChangeModeAsync(AppearanceModes.Dark, _service.SaveAppearanceModeAsync));

        var reloaded = await new ConfigService(NullLogger<ConfigService>.Instance, _directory).GetConfigAsync();
        Assert.AreEqual(AppearanceModes.Dark, reloaded.Appearance.Mode);
        Assert.AreEqual(AppearanceModes.Dark, appearance.SavedMode);
        Assert.IsNull(appearance.LastChangeError);
    }

    [TestMethod]
    public async Task OverlappingAppearanceChanges_ApplyTheLastPublishedChoice()
    {
        var store = new GateableStore();
        store.ReleasePublication.TrySetResult();
        var service = new ConfigService(NullLogger<ConfigService>.Instance, store);
        await using var appearance = new AppearanceApplier(new NoopAppearanceDocument(), new NoopBrowserColorScheme());
        await appearance.InitializeAsync(AppearanceModes.Auto);
        var firstPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> first = appearance.TryChangeModeAsync(AppearanceModes.Dark, async mode =>
        {
            await service.SaveAppearanceModeAsync(mode);
            firstPublished.TrySetResult();
            await firstCompletion.Task;
        });
        await firstPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<bool> second = appearance.TryChangeModeAsync(AppearanceModes.Light, service.SaveAppearanceModeAsync);
        firstCompletion.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual((await service.GetConfigAsync()).Appearance.Mode, appearance.SavedMode);
        Assert.AreEqual(AppearanceModes.Light, appearance.SavedMode);
    }

    [TestMethod]
    public async Task CancellationAfterPublication_ReturnsCommittedValuesAndKeepsTheNewDocument()
    {
        await _service.SeedConfigAsync(new AppConfig());
        using var cancellation = new CancellationTokenSource();
        var store = new CancellingAfterCommitStore(new SettingsDocumentStore(Path.Combine(_directory, "settings.json")), cancellation);
        var service = new ConfigService(NullLogger<ConfigService>.Instance, store);
        var update = SettingsUpdate.FromConfig(await service.GetConfigAsync()) with { BatchSize = 78 };

        var committed = await service.SaveSettingsAsync(update, cancellation.Token);

        Assert.IsTrue(cancellation.IsCancellationRequested);
        Assert.AreEqual(78, committed.BatchSize);
        Assert.AreEqual(78, (await _service.GetConfigAsync()).Processing.BatchSize);
    }

    [TestMethod]
    public async Task WaitingSaves_CaptureOwnedValuesAndCopyMutableCityCollections()
    {
        var store = new GateableStore();
        var service = new ConfigService(NullLogger<ConfigService>.Instance, store);
        Task appearance = service.SaveAppearanceModeAsync(AppearanceModes.Dark);
        await store.PublicationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var draft = new AppConfig();
        draft.Processing.BatchSize = 84;
        draft.Processing.CityResolver.DefaultProfile.PreferredSubtypes.Add("locality");
        draft.Processing.CityResolver.CountryOverrides["FRA"] = new CityResolverProfile { PreferredSubtypes = ["localadmin"] };
        var cityUpdate = new CityResolverSettingsUpdate(draft.Processing.CityResolver);
        Task settings = service.SaveSettingsAsync(SettingsUpdate.FromConfig(draft));
        Task city = service.SaveCityResolverSettingsAsync(cityUpdate);
        draft.Processing.BatchSize = 99;
        draft.Processing.CityResolver.DefaultProfile.PreferredSubtypes.Clear();
        draft.Processing.CityResolver.CountryOverrides["FRA"].PreferredSubtypes.Clear();
        draft.Processing.CityResolver.CountryOverrides.Clear();
        cityUpdate.ToConfig().DefaultProfile.PreferredSubtypes.Clear();

        store.ReleasePublication.TrySetResult();
        await Task.WhenAll(appearance, settings, city).WaitAsync(TimeSpan.FromSeconds(5));

        var saved = await service.GetConfigAsync();
        Assert.AreEqual(84, saved.Processing.BatchSize);
        Assert.AreEqual(AppearanceModes.Dark, saved.Appearance.Mode);
        CollectionAssert.AreEqual(new[] { "locality" }, saved.Processing.CityResolver.DefaultProfile.PreferredSubtypes);
        CollectionAssert.AreEqual(new[] { "localadmin" }, saved.Processing.CityResolver.CountryOverrides["FRA"].PreferredSubtypes);
    }

    private async Task<Settings> CreateSettingsPageAsync()
    {
        var page = new Settings();
        SetInjected(page, "Config", _service);
        var config = await _service.GetConfigAsync();
        SetField(page, "_cfg", config);
        SetField(page, "_schedule", ScheduleEditorState.FromCron(config.Schedule.Cron));
        return page;
    }

    private async Task<CityResolver> CreateCityPageAsync()
    {
        var page = new CityResolver();
        SetInjected(page, "Config", _service);
        SetField(page, "_cfg", await _service.GetConfigAsync());
        return page;
    }

    private static void AddCountryOverride(CityResolver page)
    {
        var type = typeof(CityResolver).GetNestedType("CountryOverrideEditor", BindingFlags.NonPublic)!;
        var editor = Activator.CreateInstance(type)!;
        var countryProperty = type.GetProperty("Country")!;
        countryProperty.SetValue(editor, JsonSerializer.Deserialize(
            """{"Iso3":"FRA","Alpha2":"FR","DisplayName":"France"}""", countryProperty.PropertyType));
        type.GetProperty("Profile")!.SetValue(editor, new CityResolverProfileOverride { PreferredSubtypes = ["localadmin"] });
        GetField<IList>(page, "_countryEditors").Add(editor);
    }

    private sealed class InterruptedDocument : IAppearanceDocument
    {
        public Task SetDataThemeAsync(string theme)
        {
            if (theme == AppearanceThemes.Dark)
            {
                throw new IOException("browser confirmation interrupted");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingLogger : ILogger<ConfigService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => throw new IOException("diagnostics unavailable");
    }

    private sealed class CancellingAfterCommitStore(ISettingsDocumentStore inner, CancellationTokenSource cancellation) : ISettingsDocumentStore
    {
        public Task<byte[]?> ReadAsync(CancellationToken cancellationToken) => inner.ReadAsync(cancellationToken);

        public async Task PublishAsync(byte[] document, CancellationToken cancellationToken)
        {
            await inner.PublishAsync(document, cancellationToken);
            cancellation.Cancel();
        }
    }

    private sealed class FailingStore(ISettingsDocumentStore inner) : ISettingsDocumentStore
    {
        public Task<byte[]?> ReadAsync(CancellationToken cancellationToken) => inner.ReadAsync(cancellationToken);

        public Task PublishAsync(byte[] document, CancellationToken cancellationToken) => throw new IOException("credential-canary raw configuration content");
    }

    private sealed class CancellablePublicationStore(ISettingsDocumentStore inner, CancellationToken cancellation) : ISettingsDocumentStore
    {
        public TaskCompletionSource PublicationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleasePublication { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<byte[]?> ReadAsync(CancellationToken cancellationToken) => inner.ReadAsync(cancellationToken);

        public async Task PublishAsync(byte[] document, CancellationToken cancellationToken)
        {
            PublicationEntered.TrySetResult();
            await ReleasePublication.Task.WaitAsync(cancellation);
            await inner.PublishAsync(document, cancellationToken);
        }
    }

    private sealed class GateableStore(byte[]? initial = null) : ISettingsDocumentStore
    {
        private byte[]? _document = initial;
        private int _publications;
        public TaskCompletionSource PublicationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleasePublication { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<byte[]?> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(_document);

        public async Task PublishAsync(byte[] document, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _publications) == 1)
            {
                PublicationEntered.TrySetResult();
                await ReleasePublication.Task.WaitAsync(cancellationToken);
            }

            _document = document;
        }
    }

    private static Task RenderPageAsync(WebStatusRenderingTests.ComponentRenderer renderer, ComponentBase page)
    {
        return renderer.Dispatcher.InvokeAsync(async () =>
        {
            Task nextRender = renderer.NextRenderAsync();
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null);
            await nextRender.WaitAsync(TimeSpan.FromSeconds(5));
        });
    }

    private static Task SaveAsync(object page) => (Task)page.GetType().GetMethod("Save", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null)!;

    private static T GetField<T>(object page, string name) => (T)page.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;

    private static void SetField(object page, string name, object value) => page.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);

    private static void SetInjected(object page, string name, object value) => page.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(page, value);
}
