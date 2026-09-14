using System.Reflection;
using ImmichReverseGeo.Core.ApplicationRole;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Web.Composition;
using ImmichReverseGeo.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

#pragma warning disable BL0006

namespace ImmichReverseGeo.Tests.ApplicationComposition;

[TestClass]
[TestCategory("Change42")]
public sealed class WebOnlySettingsTests
{
    private const string CustomCron = "7,37 3-5 * * 2-6";

    [TestMethod]
    public async Task WebOnly_RenderingShowsPolicyKeepsEnabledCustomCronEditableAndSaveDoesNotPersistMode()
    {
        var fixture = await SettingsFixture.CreateAsync(
            DeploymentMode.WebOnly,
            new AppConfig
            {
                Schedule = new ScheduleConfig { Enabled = true, Cron = CustomCron }
            });

        try
        {
            var renderedText = await fixture.Renderer.ReadTextAsync();
            var controls = await fixture.Renderer.ReadScheduleControlsAsync(CustomCron);

            StringAssert.Contains(renderedText, "Web-only mode keeps these schedule settings, but internal scheduling is disabled.");
            StringAssert.Contains(renderedText, "Cron Expression");
            AssertNoLifecycleStatusScope(renderedText);
            Assert.IsTrue(controls.ScheduleCheckbox.IsChecked, "enabled-checkbox-retained");
            Assert.IsFalse(controls.ScheduleCheckbox.IsDisabled, "schedule-checkbox-remains-editable");
            Assert.AreEqual(CustomCron, controls.CustomCronInput.Value, "custom-cron-visible-in-editable-control");
            Assert.IsFalse(controls.CustomCronInput.IsDisabled, "custom-cron-input-remains-editable");
            Assert.AreEqual(ScheduleEditorState.ModeCustom, GetSchedule(fixture.Component).Mode, "custom-schedule-editor-state");

            await fixture.Renderer.InvokeSaveAsync();

            var saved = await fixture.ReloadSettingsAsync();
            Assert.IsTrue(saved.Schedule.Enabled, "saved-enabled");
            Assert.AreEqual(CustomCron, saved.Schedule.Cron, "saved-cron");

            var persisted = await File.ReadAllTextAsync(fixture.SettingsPath);
            Assert.IsFalse(persisted.Contains("deploymentMode", StringComparison.OrdinalIgnoreCase), "mode-is-not-config-data");
            Assert.IsFalse(persisted.Contains("web-only", StringComparison.Ordinal), "mode-value-is-not-persisted");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Standard_RenderingWithTheSameScheduleOmitsTheWebOnlyPolicy()
    {
        var fixture = await SettingsFixture.CreateAsync(
            DeploymentMode.Standard,
            new AppConfig
            {
                Schedule = new ScheduleConfig { Enabled = true, Cron = CustomCron }
            });

        try
        {
            var renderedText = await fixture.Renderer.ReadTextAsync();
            var controls = await fixture.Renderer.ReadScheduleControlsAsync(CustomCron);

            Assert.IsFalse(renderedText.Contains("Web-only mode keeps these schedule settings", StringComparison.Ordinal));
            Assert.IsTrue(controls.ScheduleCheckbox.IsChecked, "standard-enabled-checkbox-retained");
            Assert.IsFalse(controls.ScheduleCheckbox.IsDisabled, "standard-schedule-checkbox-remains-editable");
            Assert.AreEqual(CustomCron, controls.CustomCronInput.Value, "same-custom-cron-visible-in-standard-mode");
            Assert.IsFalse(controls.CustomCronInput.IsDisabled, "standard-custom-cron-input-remains-editable");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task WebOnly_RenderDoesNotRewriteInvalidSavedScheduleAndExplicitCustomSavePreservesItsNormalSemantics()
    {
        const string invalidCron = "this is not a cron expression";
        var fixture = await SettingsFixture.CreateAsync(
            DeploymentMode.WebOnly,
            new AppConfig
            {
                Schedule = new ScheduleConfig { Enabled = true, Cron = invalidCron }
            });

        try
        {
            var renderedText = await fixture.Renderer.ReadTextAsync();
            var controls = await fixture.Renderer.ReadScheduleControlsAsync(invalidCron);

            StringAssert.Contains(renderedText, "Web-only mode keeps these schedule settings, but internal scheduling is disabled.");
            Assert.AreEqual(invalidCron, controls.CustomCronInput.Value, "invalid-custom-cron-remains-visible");
            Assert.IsFalse(controls.CustomCronInput.IsDisabled, "invalid-custom-cron-remains-editable");
            Assert.AreEqual(ScheduleEditorState.ModeCustom, GetSchedule(fixture.Component).Mode, "invalid-cron-uses-existing-custom-editor");
            Assert.AreEqual(fixture.InitialSettingsJson, await File.ReadAllTextAsync(fixture.SettingsPath), "startup-render-never-persists-or-normalizes-config");

            await fixture.Renderer.InvokeSaveAsync();

            var saved = await fixture.ReloadSettingsAsync();
            Assert.IsTrue(saved.Schedule.Enabled, "explicit-custom-save-retains-enabled-state");
            Assert.AreEqual(invalidCron, saved.Schedule.Cron, "explicit-custom-save-keeps-existing-custom-cron-semantics");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static ScheduleEditorState GetSchedule(ImmichReverseGeo.Web.Components.Pages.Settings component)
    {
        return (ScheduleEditorState)(typeof(ImmichReverseGeo.Web.Components.Pages.Settings)
            .GetField("_schedule", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(component)
            ?? throw new InvalidOperationException("Settings schedule state was not found."));
    }

    private static void AssertNoLifecycleStatusScope(string renderedText)
    {
        foreach (string deferredStatus in new[] { "Process ID", "Run ID", "Job ID", "Active job", "Deployment mode" })
        {
            Assert.IsFalse(
                renderedText.Contains(deferredStatus, StringComparison.OrdinalIgnoreCase),
                "settings-does-not-add-deferred-lifecycle-status: " + deferredStatus);
        }
    }

    private static void SetInjected(object component, string propertyName, object value)
    {
        (component.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Injected Settings property '{propertyName}' was not found."))
            .SetValue(component, value);
    }

    private sealed class SettingsFixture : IAsyncDisposable
    {
        private SettingsFixture(
            string root,
            string settingsPath,
            string initialSettingsJson,
            string configDirectory,
            ConfigService config,
            ServiceProvider provider,
            ServiceProvider rendererServices,
            NpgsqlDataSource dataSource,
            ImmichReverseGeo.Web.Components.Pages.Settings component,
            SettingsRenderer renderer)
        {
            Root = root;
            SettingsPath = settingsPath;
            InitialSettingsJson = initialSettingsJson;
            ConfigDirectory = configDirectory;
            Config = config;
            Provider = provider;
            RendererServices = rendererServices;
            DataSource = dataSource;
            Component = component;
            Renderer = renderer;
        }

        public string Root { get; }
        public string SettingsPath { get; }
        public string InitialSettingsJson { get; }
        public string ConfigDirectory { get; }
        public ConfigService Config { get; }
        public ServiceProvider Provider { get; }
        public ServiceProvider RendererServices { get; }
        public NpgsqlDataSource DataSource { get; }
        public ImmichReverseGeo.Web.Components.Pages.Settings Component { get; }
        public SettingsRenderer Renderer { get; }

        public static async Task<SettingsFixture> CreateAsync(DeploymentMode mode, AppConfig config)
        {
            var root = Path.Combine(Path.GetTempPath(), "immich-reversegeo-web-only-settings-" + Guid.NewGuid().ToString("N"));
            var configDirectory = Path.Combine(root, "config");
            var dataDirectory = Path.Combine(root, "data");
            Directory.CreateDirectory(dataDirectory);
            ServiceProvider? provider = null;
            ServiceProvider? rendererServices = null;
            NpgsqlDataSource? dataSource = null;
            SettingsRenderer? renderer = null;

            try
            {
                var composition = ApplicationCompositionContext.Create(
                    CompositionEnvironment.Development,
                    root,
                    dataDirectory,
                    configDirectory,
                    mode);
                var services = new ServiceCollection();
                if (ReferenceEquals(mode, DeploymentMode.WebOnly))
                {
                    services.AddWebOnlyWebComposition(composition);
                }
                else
                {
                    services.AddStandardWebComposition(composition);
                }

                provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
                var configService = provider.GetRequiredService<ConfigService>();
                await configService.SaveConfigAsync(config);
                var settingsPath = Path.Combine(configDirectory, "settings.json");
                var initialSettingsJson = await File.ReadAllTextAsync(settingsPath);
                dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=immich;Username=immich;Password=not-used;Pooling=false;Timeout=1;Command Timeout=1");
                var repository = new ImmichDbRepository(dataSource, NullLogger<ImmichDbRepository>.Instance);
                var component = new ImmichReverseGeo.Web.Components.Pages.Settings();
                SetInjected(component, "Config", configService);
                SetInjected(component, "Db", repository);
                SetInjected(component, "Composition", provider.GetRequiredService<ApplicationCompositionContext>());
                rendererServices = new ServiceCollection().BuildServiceProvider();
                renderer = new SettingsRenderer(rendererServices, component);
                await renderer.AttachAsync(component);
                return new SettingsFixture(
                    root,
                    settingsPath,
                    initialSettingsJson,
                    configDirectory,
                    configService,
                    provider,
                    rendererServices,
                    dataSource,
                    component,
                    renderer);
            }
            catch
            {
                if (renderer is not null)
                {
                    await renderer.DisposeAsync();
                }
                if (dataSource is not null)
                {
                    await dataSource.DisposeAsync();
                }
                if (provider is not null)
                {
                    await provider.DisposeAsync();
                }
                if (rendererServices is not null)
                {
                    await rendererServices.DisposeAsync();
                }
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Renderer.DisposeAsync();
            await RendererServices.DisposeAsync();
            await DataSource.DisposeAsync();
            await Provider.DisposeAsync();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        public Task<AppConfig> ReloadSettingsAsync()
        {
            return new ConfigService(NullLogger<ConfigService>.Instance, ConfigDirectory).GetConfigAsync();
        }
    }

    private sealed class SettingsRenderer : Renderer
    {
        private static readonly MethodInfo Save = typeof(ImmichReverseGeo.Web.Components.Pages.Settings)
            .GetMethod("Save", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Settings.Save was not found.");

        private readonly ImmichReverseGeo.Web.Components.Pages.Settings _component;
        private int _componentId;

        public SettingsRenderer(IServiceProvider services, ImmichReverseGeo.Web.Components.Pages.Settings component)
            : base(services, NullLoggerFactory.Instance)
        {
            _component = component;
        }

        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        public Task AttachAsync(IComponent component)
        {
            return Dispatcher.InvokeAsync(async () =>
            {
                _componentId = AssignRootComponentId(component);
                await RenderRootComponentAsync(_componentId, ParameterView.Empty);
            });
        }

        public Task InvokeSaveAsync()
        {
            return Dispatcher.InvokeAsync(async () =>
            {
                await ((Task)Save.Invoke(_component, null)!);
            });
        }

        public Task<string> ReadTextAsync()
        {
            return Dispatcher.InvokeAsync(() =>
            {
                var frames = GetCurrentRenderTreeFrames(_componentId);
                return string.Concat(frames.Array
                    .Take(frames.Count)
                    .Where(frame => frame.FrameType is RenderTreeFrameType.Text or RenderTreeFrameType.Markup)
                    .Select(frame => frame.FrameType == RenderTreeFrameType.Text ? frame.TextContent : frame.MarkupContent));
            });
        }

        public Task<ScheduleControls> ReadScheduleControlsAsync(string customCron)
        {
            return Dispatcher.InvokeAsync(() =>
            {
                var frames = GetCurrentRenderTreeFrames(_componentId);
                var inputs = new List<RenderedInput>();
                for (var index = 0; index < frames.Count; index++)
                {
                    var frame = frames.Array[index];
                    if (frame.FrameType != RenderTreeFrameType.Element || frame.ElementName != "input")
                    {
                        continue;
                    }

                    var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
                    var end = Math.Min(index + frame.ElementSubtreeLength, frames.Count);
                    for (var attributeIndex = index + 1; attributeIndex < end; attributeIndex++)
                    {
                        var attribute = frames.Array[attributeIndex];
                        if (attribute.FrameType == RenderTreeFrameType.Attribute && attribute.AttributeName is not null)
                        {
                            attributes[attribute.AttributeName] = attribute.AttributeValue;
                        }
                    }

                    inputs.Add(new RenderedInput(
                        attributes.TryGetValue("type", out var type) ? type?.ToString() : null,
                        attributes.TryGetValue("value", out var value) ? value?.ToString() : null,
                        EffectiveBoolean(attributes, "checked"),
                        EffectiveBoolean(attributes, "disabled")));
                }

                var scheduleCheckbox = inputs.FirstOrDefault(input => input.Type == "checkbox")
                    ?? throw new AssertFailedException("Rendered schedule checkbox was not found.");
                var customCronInput = inputs.FirstOrDefault(input => input.Value == customCron)
                    ?? throw new AssertFailedException("Rendered custom cron input was not found.");
                return new ScheduleControls(scheduleCheckbox, customCronInput);
            });
        }

        private static bool EffectiveBoolean(IReadOnlyDictionary<string, object?> attributes, string attributeName)
        {
            if (!attributes.TryGetValue(attributeName, out var value))
            {
                return false;
            }

            return value is not bool boolean || boolean;
        }

        protected override void HandleException(Exception exception)
        {
            throw exception;
        }

        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
    }

    private sealed record ScheduleControls(RenderedInput ScheduleCheckbox, RenderedInput CustomCronInput);

    private sealed record RenderedInput(string? Type, string? Value, bool IsChecked, bool IsDisabled);
}
