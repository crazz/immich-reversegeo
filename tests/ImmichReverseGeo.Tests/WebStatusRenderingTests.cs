using System.Reflection;
using ImmichReverseGeo.Core.ApplicationRole;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerFailureRecovery;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable BL0006

namespace ImmichReverseGeo.Tests;

[TestClass]
[TestCategory("Change44")]
[DoNotParallelize]
public sealed class WebStatusRenderingTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task Dashboard_RendersWebOnlyPolicyAndStatusWhenDatabaseStatsAreUnavailable()
    {
        var status = new ProcessAssetsWebStatus(DeploymentMode.WebOnly);
        var state = new ProcessingState();
        var dashboard = CreateDashboard(status, state);
        SetField(dashboard, "_dbError", "fixture database unavailable");
        await using var renderer = new ComponentRenderer();
        await renderer.AttachAsync(dashboard);

        RenderSnapshot rendered = await renderer.ReadAsync();
        StringAssert.Contains(rendered.Text, "Service Status");
        StringAssert.Contains(rendered.Text, "Deployment mode");
        StringAssert.Contains(rendered.Text, "Web-only");
        StringAssert.Contains(rendered.Text, "Disabled by Web-only");
        StringAssert.Contains(rendered.Text, "Saved schedule values are retained");
        StringAssert.Contains(rendered.Text, "manual runs remain available from the Dashboard");
        StringAssert.Contains(rendered.Text, "Worker: Idle");
        StringAssert.Contains(rendered.Text, "Database unavailable: fixture database unavailable");
        Assert.IsTrue(rendered.HasAttribute("role", "status"));
        Assert.IsTrue(rendered.HasAttribute("aria-live", "polite"));
        Assert.IsFalse(rendered.Text.Contains("Run-once", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task DashboardAndNavigation_RenderTheSameFiveWorkerStatesAndOneSafeFailureAlert()
    {
        var status = new ProcessAssetsWebStatus(DeploymentMode.Standard);
        var state = new ProcessingState();
        var dashboard = CreateDashboard(status, state);
        var navigation = CreateNavigation(status);
        await using var dashboardRenderer = new ComponentRenderer();
        await using var navigationRenderer = new ComponentRenderer();
        await dashboardRenderer.AttachAsync(dashboard);
        await navigationRenderer.AttachAsync(navigation);
        await AssertWorkerAsync("Idle", "idle", dashboardRenderer, navigationRenderer);
        RenderSnapshot standard = await dashboardRenderer.ReadAsync();
        StringAssert.Contains(standard.Text, "Standard");
        StringAssert.Contains(standard.Text, "Available");
        StringAssert.Contains(
            standard.Text,
            "Your saved schedule settings control whether and when it runs");

        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual);
        var sink = (IProcessAssetsWorkerStatusSink)status;
        await PublishAndRenderAsync(
            () => sink.Admit(request, cancellationAlreadyWon: false),
            dashboardRenderer,
            navigationRenderer);
        await AssertWorkerAsync("Starting", "starting", dashboardRenderer, navigationRenderer);

        await PublishAndRenderAsync(
            () => sink.ObserveTransport(request, WorkerRunTransportPhase.Accepted),
            dashboardRenderer,
            navigationRenderer);
        await AssertWorkerAsync("Running", "running", dashboardRenderer, navigationRenderer);

        await PublishAndRenderAsync(
            () => sink.ObserveCancellation(request),
            dashboardRenderer,
            navigationRenderer);
        await AssertWorkerAsync("Cancelling", "cancelling", dashboardRenderer, navigationRenderer);

        await PublishAndRenderAsync(
            () => sink.ObserveFinality(
                request,
                ProcessingRunOutcome.Failed,
                WorkerRunFailureCategory.Crash),
            dashboardRenderer,
            navigationRenderer);
        await AssertWorkerAsync("Failed", "failed", dashboardRenderer, navigationRenderer);

        RenderSnapshot failed = await dashboardRenderer.ReadAsync();
        Assert.IsTrue(failed.HasAttribute("role", "alert"));
        Assert.AreEqual(1, failed.AttributeCount("role", "alert"));
        StringAssert.Contains(failed.Text, "ProcessAssets worker failed.");
        StringAssert.Contains(failed.Text, WorkerRunDiagnostics.Describe(WorkerRunFailureCategory.Crash));
        StringAssert.Contains(failed.Text, "Open Logs");
        Assert.IsFalse(failed.Text.Contains("exit code", StringComparison.OrdinalIgnoreCase));

        int renderCount = dashboardRenderer.RenderCount;
        Task rerendered = dashboardRenderer.NextRenderAsync();
        state.SetActivity("Unrelated local activity.");
        await rerendered.WaitAsync(Bound);
        RenderSnapshot retained = await dashboardRenderer.ReadAsync();
        Assert.AreEqual(renderCount + 1, dashboardRenderer.RenderCount);
        Assert.AreEqual(1, retained.AttributeCount("role", "alert"));
        Assert.AreEqual(status.Current.Revision, GetDashboardSnapshot(dashboard).Revision);
        CollectionAssert.DoesNotContain(
            dashboardRenderer.LastEditTypes,
            RenderTreeEditType.RemoveFrame,
            "the retained alert subtree must not be removed on an unrelated rerender");
        CollectionAssert.DoesNotContain(
            dashboardRenderer.LastEditTypes,
            RenderTreeEditType.PrependFrame,
            "the retained alert subtree must not be recreated on an unrelated rerender");
    }

    [TestMethod]
    public async Task NewComponents_ReadAlreadyRunningSingletonWithoutAnotherWorkerEvent()
    {
        var status = new ProcessAssetsWebStatus(DeploymentMode.Standard);
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual);
        var sink = (IProcessAssetsWorkerStatusSink)status;
        sink.Admit(request, cancellationAlreadyWon: false);
        sink.ObserveTransport(request, WorkerRunTransportPhase.Accepted);
        Assert.AreEqual(ProcessAssetsWorkerState.Running, status.Current.Worker);
        long runningRevision = status.Current.Revision;

        var dashboard = CreateDashboard(status, new ProcessingState());
        var navigation = CreateNavigation(status);
        await using var dashboardRenderer = new ComponentRenderer();
        await using var navigationRenderer = new ComponentRenderer();
        await dashboardRenderer.AttachAsync(dashboard);
        await navigationRenderer.AttachAsync(navigation);

        StringAssert.Contains((await dashboardRenderer.ReadAsync()).Text, "Worker: Running");
        StringAssert.Contains((await navigationRenderer.ReadAsync()).Text, "Worker: Running");
        Assert.AreEqual(runningRevision, GetDashboardSnapshot(dashboard).Revision);
        Assert.AreEqual(runningRevision, GetNavigationSnapshot(navigation).Revision);
    }

    [TestMethod]
    public async Task SyntheticSubscribeRace_RereadsCurrentAndRejectsAnOlderQueuedSnapshot()
    {
        // This fake controls only the read/subscribe interleaving. The preceding test
        // uses the real singleton for process-lifetime reconnect behavior.
        var status = new RacingStatus();
        var dashboard = CreateDashboard(status, new ProcessingState());
        var navigation = CreateNavigation(status);
        await using var dashboardRenderer = new ComponentRenderer();
        await using var navigationRenderer = new ComponentRenderer();

        await dashboardRenderer.AttachAsync(dashboard);
        await navigationRenderer.AttachAsync(navigation);
        await dashboardRenderer.Dispatcher.InvokeAsync(() => { });
        await navigationRenderer.Dispatcher.InvokeAsync(() => { });

        StringAssert.Contains((await dashboardRenderer.ReadAsync()).Text, "Worker: Running");
        StringAssert.Contains((await navigationRenderer.ReadAsync()).Text, "Worker: Running");
        Assert.AreEqual(2L, GetDashboardSnapshot(dashboard).Revision);
        Assert.AreEqual(2L, GetNavigationSnapshot(navigation).Revision);
        Assert.AreEqual(2, status.CurrentReadsAfterSubscription);

        dashboard.Dispose();
        navigation.Dispose();
        Assert.AreEqual(2, status.Disposals);
    }

    [TestMethod]
    public void SharedStyles_ProvideWorkerStateMobileAndReducedMotionRules()
    {
        string css = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ImmichReverseGeo.Web",
            "wwwroot",
            "app.css"));

        StringAssert.Contains(css, ".worker-status-card");
        StringAssert.Contains(css, ".status-dot.starting");
        StringAssert.Contains(css, ".status-dot.cancelling");
        StringAssert.Contains(css, ".status-dot.failed");
        StringAssert.Contains(css, "@media (max-width: 900px)");
        StringAssert.Contains(css, "@media (max-width: 640px)");
        StringAssert.Contains(css, "@media (prefers-reduced-motion: reduce)");
    }

    internal static ImmichReverseGeo.Web.Components.Pages.Dashboard CreateDashboard(
        IProcessAssetsWebStatus status,
        ProcessingState state)
    {
        var component = new ImmichReverseGeo.Web.Components.Pages.Dashboard();
        SetInjected(component, "State", state);
        SetInjected(component, "RunCoordinator", new IdleCoordinator());
        SetInjected(component, "WorkerStatus", status);
        SetField(component, "_circuitReady", true);
        return component;
    }

    internal static ImmichReverseGeo.Web.Components.Layout.NavMenu CreateNavigation(
        IProcessAssetsWebStatus status)
    {
        var component = new ImmichReverseGeo.Web.Components.Layout.NavMenu();
        SetInjected(component, "WorkerStatus", status);
        return component;
    }

    private static async Task PublishAndRenderAsync(
        Action publish,
        ComponentRenderer dashboard,
        ComponentRenderer navigation)
    {
        Task dashboardRender = dashboard.NextRenderAsync();
        Task navigationRender = navigation.NextRenderAsync();
        publish();
        await Task.WhenAll(
            dashboardRender.WaitAsync(Bound),
            navigationRender.WaitAsync(Bound));
    }

    private static async Task AssertWorkerAsync(
        string label,
        string cssClass,
        ComponentRenderer dashboard,
        ComponentRenderer navigation)
    {
        RenderSnapshot detailed = await dashboard.ReadAsync();
        RenderSnapshot compact = await navigation.ReadAsync();
        StringAssert.Contains(detailed.Text, $"Worker: {label}");
        StringAssert.Contains(compact.Text, $"Worker: {label}");
        Assert.IsTrue(detailed.HasCssClass("status-dot", cssClass));
        Assert.IsTrue(compact.HasCssClass("status-dot", cssClass));
        Assert.IsTrue(compact.HasAttribute("aria-hidden", "true"));
    }

    private static ProcessAssetsWebStatusSnapshot GetDashboardSnapshot(
        ImmichReverseGeo.Web.Components.Pages.Dashboard component)
    {
        return (ProcessAssetsWebStatusSnapshot)GetField(component, "_workerStatus")!;
    }

    private static ProcessAssetsWebStatusSnapshot GetNavigationSnapshot(
        ImmichReverseGeo.Web.Components.Layout.NavMenu component)
    {
        return (ProcessAssetsWebStatusSnapshot)GetField(component, "_workerStatus")!;
    }

    private static void SetInjected(object component, string name, object value)
    {
        component.GetType()
            .GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(component, value);
    }

    private static void SetField(object component, string name, object? value)
    {
        component.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(component, value);
    }

    private static object? GetField(object component, string name)
    {
        return component.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(component);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "package.json")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private sealed class IdleCoordinator : IManualProcessingRunCoordinator
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

    private sealed class RacingStatus : IProcessAssetsWebStatus
    {
        private readonly ProcessAssetsWebStatusSnapshot _running = new(
            WebDeploymentMode.Standard,
            InternalSchedulingPolicy.Available,
            ProcessAssetsWorkerState.Running,
            FailureSummary: null,
            Revision: 2);
        private bool _subscribed;

        internal int CurrentReadsAfterSubscription { get; private set; }
        internal int Disposals { get; private set; }

        public ProcessAssetsWebStatusSnapshot Current
        {
            get
            {
                if (_subscribed)
                {
                    CurrentReadsAfterSubscription++;
                }

                return _running;
            }
        }

        public IDisposable Subscribe(Action<ProcessAssetsWebStatusSnapshot> changed)
        {
            _subscribed = true;
            changed(new ProcessAssetsWebStatusSnapshot(
                WebDeploymentMode.Standard,
                InternalSchedulingPolicy.Available,
                ProcessAssetsWorkerState.Starting,
                FailureSummary: null,
                Revision: 1));
            return new CallbackDisposable(() => Disposals++);
        }
    }

    private sealed class CallbackDisposable(Action dispose) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                dispose();
            }
        }
    }

    internal sealed record RenderSnapshot(RenderTreeFrame[] Frames)
    {
        internal string Text => string.Concat(Frames
            .Where(frame => frame.FrameType is RenderTreeFrameType.Text or RenderTreeFrameType.Markup)
            .Select(frame => frame.FrameType == RenderTreeFrameType.Text
                ? frame.TextContent
                : frame.MarkupContent));

        internal bool HasAttribute(string name, string value)
        {
            return Frames.Any(frame => frame.FrameType == RenderTreeFrameType.Attribute
                && string.Equals(frame.AttributeName, name, StringComparison.Ordinal)
                && string.Equals(frame.AttributeValue?.ToString(), value, StringComparison.Ordinal));
        }

        internal int AttributeCount(string name, string value)
        {
            return Frames.Count(frame => frame.FrameType == RenderTreeFrameType.Attribute
                && string.Equals(frame.AttributeName, name, StringComparison.Ordinal)
                && string.Equals(frame.AttributeValue?.ToString(), value, StringComparison.Ordinal));
        }

        internal bool HasCssClass(params string[] expected)
        {
            return Frames.Any(frame => frame.FrameType == RenderTreeFrameType.Attribute
                && string.Equals(frame.AttributeName, "class", StringComparison.Ordinal)
                && expected.All(value => (frame.AttributeValue?.ToString() ?? string.Empty)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains(value, StringComparer.Ordinal)));
        }

        internal string ElementTextByCssClass(string cssClass)
        {
            for (var index = 0; index < Frames.Length; index++)
            {
                RenderTreeFrame frame = Frames[index];
                if (frame.FrameType != RenderTreeFrameType.Element)
                {
                    continue;
                }

                var hasClass = false;
                for (var attributeIndex = index + 1;
                    attributeIndex < Frames.Length
                        && Frames[attributeIndex].FrameType == RenderTreeFrameType.Attribute;
                    attributeIndex++)
                {
                    RenderTreeFrame attribute = Frames[attributeIndex];
                    if (string.Equals(attribute.AttributeName, "class", StringComparison.Ordinal)
                        && (attribute.AttributeValue?.ToString() ?? string.Empty)
                            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                            .Contains(cssClass, StringComparer.Ordinal))
                    {
                        hasClass = true;
                        break;
                    }
                }

                if (!hasClass)
                {
                    continue;
                }

                return string.Concat(Frames
                    .Skip(index)
                    .Take(frame.ElementSubtreeLength)
                    .Where(item => item.FrameType is RenderTreeFrameType.Text or RenderTreeFrameType.Markup)
                    .Select(item => item.FrameType == RenderTreeFrameType.Text
                        ? item.TextContent
                        : item.MarkupContent));
            }

            throw new AssertFailedException($"No rendered element has CSS class '{cssClass}'.");
        }
    }

    internal sealed class ComponentRenderer : Renderer
    {
        private readonly object _renderGate = new();
        private TaskCompletionSource? _nextRender;
        private int _componentId;

        internal ComponentRenderer()
            : base(CreateServices(), NullLoggerFactory.Instance)
        {
        }

        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        internal int RenderCount { get; private set; }
        internal RenderTreeEditType[] LastEditTypes { get; private set; } = [];

        internal Task AttachAsync(IComponent component)
        {
            return Dispatcher.InvokeAsync(async () =>
            {
                _componentId = AssignRootComponentId(component);
                await RenderRootComponentAsync(_componentId, ParameterView.Empty);
            });
        }

        internal Task NextRenderAsync()
        {
            lock (_renderGate)
            {
                if (_nextRender is not null)
                {
                    throw new InvalidOperationException("A render waiter is already armed.");
                }

                _nextRender = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return _nextRender.Task;
            }
        }

        internal Task<RenderSnapshot> ReadAsync()
        {
            return Dispatcher.InvokeAsync(() =>
            {
                var frames = GetCurrentRenderTreeFrames(_componentId);
                return new RenderSnapshot(frames.Array.Take(frames.Count).ToArray());
            });
        }

        protected override void HandleException(Exception exception)
        {
            throw exception;
        }

        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch)
        {
            TaskCompletionSource? next;
            lock (_renderGate)
            {
                RenderCount++;
                LastEditTypes = renderBatch.UpdatedComponents.Array
                    .Take(renderBatch.UpdatedComponents.Count)
                    .SelectMany(diff => diff.Edits.Array.Take(diff.Edits.Count))
                    .Select(edit => edit.Type)
                    .ToArray();
                next = _nextRender;
                _nextRender = null;
            }

            next?.TrySetResult();
            return Task.CompletedTask;
        }

        private static IServiceProvider CreateServices()
        {
            var services = new ServiceCollection();
            services.AddSingleton<NavigationManager>(new TestNavigationManager());
            return services.BuildServiceProvider();
        }
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        internal TestNavigationManager()
        {
            Initialize("http://localhost/", "http://localhost/");
        }

        protected override void NavigateToCore(string uri, bool forceLoad)
        {
            Uri = ToAbsoluteUri(uri).ToString();
            NotifyLocationChanged(isInterceptedLink: false);
        }
    }
}
