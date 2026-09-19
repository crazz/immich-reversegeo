using System.Reflection;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable BL0006

namespace ImmichReverseGeo.Tests;

[TestClass]
[DoNotParallelize]
public sealed class SkipListDestinationTests
{
    [TestMethod]
    public async Task Operator_OpensADataBookmark()
    {
        var navigation = new RecordingNavigationManager();
        var page = new ImmichReverseGeo.Web.Components.Pages.Data();
        await using var renderer = new WebStatusRenderingTests.ComponentRenderer(services =>
        {
            services.AddSingleton<NavigationManager>(navigation);
        });
        SetInjected(page, "Navigation", navigation);

        await renderer.AttachAsync(page);

        WebStatusRenderingTests.RenderSnapshot rendered = await renderer.ReadAsync();
        Assert.AreEqual("http://localhost/data/skip-list", navigation.Uri);
        Assert.IsFalse(rendered.Text.Contains("Data Management", StringComparison.Ordinal));
        Assert.IsFalse(rendered.Text.Contains("Management Sections", StringComparison.Ordinal));
        Assert.IsFalse(rendered.Text.Contains("data-link-card", StringComparison.Ordinal));
        Assert.IsFalse(rendered.Text.Contains("Skipped Assets", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Operator_ManagesSkippedAssetsFromSkipList()
    {
        await using var coordinator = new WorkerJobCoordinator(WorkerJobDescriptors.Registered);
        var stores = new ClearSkipListStores();
        var controller = new DatabaseMaintenanceController(
            coordinator,
            stores,
            stores,
            NullLogger<DatabaseMaintenanceController>.Instance);
        var countReader = new SequenceCountReader(5, 0);
        var page = new ImmichReverseGeo.Web.Components.Pages.SkipList();
        SetInjected(page, "Maintenance", controller);
        SetInjected(page, "Skipped", countReader);
        await using var renderer = new WebStatusRenderingTests.ComponentRenderer();
        await renderer.AttachAsync(page);

        WebStatusRenderingTests.RenderSnapshot before = await renderer.ReadAsync();
        StringAssert.Contains(before.Text, "5 asset(s) permanently skipped.");
        Assert.IsFalse(before.Text.Contains("Management Sections", StringComparison.Ordinal));
        Assert.IsFalse(before.Text.Contains("data-link-card", StringComparison.Ordinal));
        Assert.IsFalse(before.Text.Contains("Data Management", StringComparison.Ordinal));
        Assert.IsTrue(HasEnabledButton(before, "Clear Skip List"));

        await InvokeButtonAsync(renderer, "Clear Skip List");

        WebStatusRenderingTests.RenderSnapshot after = await renderer.ReadAsync();
        DatabaseMaintenanceResult? clearResult = GetField<DatabaseMaintenanceResult?>(
            page,
            "_skipClearResult");
        Assert.IsNotNull(clearResult);
        Assert.AreEqual(DatabaseMaintenanceDisposition.Complete, clearResult.Disposition);
        StringAssert.Contains(after.Text, clearResult.Message);
        Assert.IsTrue(after.HasAttribute("role", "status"));
        Assert.IsTrue(after.HasAttribute("aria-live", "polite"));
        Assert.AreEqual(0L, GetField<long>(page, "_skippedCount"));
        Assert.IsNull(GetField<string?>(page, "_skipReloadError"));
        Assert.IsFalse(after.Text.Contains("Management Sections", StringComparison.Ordinal));
        Assert.IsFalse(after.Text.Contains("data-link-card", StringComparison.Ordinal));
        Assert.AreEqual(1, stores.SkippedClearCalls);
    }

    private static async Task InvokeButtonAsync(
        WebStatusRenderingTests.ComponentRenderer renderer,
        string label)
    {
        WebStatusRenderingTests.RenderSnapshot snapshot = await renderer.ReadAsync();
        ulong eventHandlerId = 0;
        for (var elementIndex = 0; elementIndex < snapshot.Frames.Length; elementIndex++)
        {
            RenderTreeFrame element = snapshot.Frames[elementIndex];
            if (element.FrameType != RenderTreeFrameType.Element
                || !string.Equals(element.ElementName, "button", StringComparison.Ordinal))
            {
                continue;
            }

            string content = string.Concat(snapshot.Frames
                .Skip(elementIndex)
                .Take(element.ElementSubtreeLength)
                .Where(frame => frame.FrameType is RenderTreeFrameType.Text or RenderTreeFrameType.Markup)
                .Select(frame => frame.FrameType == RenderTreeFrameType.Text
                    ? frame.TextContent
                    : frame.MarkupContent));
            if (!string.Equals(content.Trim(), label, StringComparison.Ordinal))
            {
                continue;
            }

            RenderTreeFrame onclick = snapshot.Frames
                .Skip(elementIndex + 1)
                .Take(element.ElementSubtreeLength - 1)
                .First(item => item.FrameType == RenderTreeFrameType.Attribute
                    && string.Equals(item.AttributeName, "onclick", StringComparison.Ordinal));
            eventHandlerId = onclick.AttributeEventHandlerId;
            break;
        }

        Assert.AreNotEqual(0UL, eventHandlerId, $"Rendered button '{label}' was not found.");
        await renderer.Dispatcher.InvokeAsync(
            () => renderer.DispatchEventAsync(eventHandlerId, null, new MouseEventArgs()));
    }

    private static bool HasEnabledButton(
        WebStatusRenderingTests.RenderSnapshot snapshot,
        string label)
    {
        for (var elementIndex = 0; elementIndex < snapshot.Frames.Length; elementIndex++)
        {
            RenderTreeFrame element = snapshot.Frames[elementIndex];
            if (element.FrameType != RenderTreeFrameType.Element
                || !string.Equals(element.ElementName, "button", StringComparison.Ordinal))
            {
                continue;
            }

            RenderTreeFrame[] subtree = snapshot.Frames
                .Skip(elementIndex)
                .Take(element.ElementSubtreeLength)
                .ToArray();
            string content = string.Concat(subtree
                .Where(frame => frame.FrameType is RenderTreeFrameType.Text or RenderTreeFrameType.Markup)
                .Select(frame => frame.FrameType == RenderTreeFrameType.Text
                    ? frame.TextContent
                    : frame.MarkupContent));
            if (!string.Equals(content.Trim(), label, StringComparison.Ordinal))
            {
                continue;
            }

            bool disabled = subtree.Any(frame =>
                frame.FrameType == RenderTreeFrameType.Attribute
                && string.Equals(frame.AttributeName, "disabled", StringComparison.Ordinal)
                && !Equals(frame.AttributeValue, false));
            return !disabled;
        }

        return false;
    }

    private static void SetInjected(object target, string name, object value)
    {
        PropertyInfo? property = target.GetType()
            .GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.IsNotNull(property, $"Component must expose injected '{name}'.");
        property.SetValue(target, value);
    }

    private static T GetField<T>(object target, string name)
    {
        FieldInfo? field = target.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field, $"Component must keep field '{name}'.");
        return (T)field.GetValue(target)!;
    }

    private sealed class RecordingNavigationManager : NavigationManager
    {
        public RecordingNavigationManager()
        {
            Initialize("http://localhost/", "http://localhost/data");
        }

        protected override void NavigateToCore(string uri, bool forceLoad)
        {
            Uri = ToAbsoluteUri(uri).ToString();
            NotifyLocationChanged(isInterceptedLink: false);
        }

        protected override void NavigateToCore(string uri, NavigationOptions options)
        {
            NavigateToCore(uri, options.ForceLoad);
        }
    }

    private sealed class SequenceCountReader(params long[] counts) : ISkippedAssetsCountReader
    {
        private int _index;

        public Task<long> GetCountAsync()
        {
            long count = counts[Math.Min(_index, counts.Length - 1)];
            _index++;
            return Task.FromResult(count);
        }
    }

    private sealed class ClearSkipListStores : IImmichLocationResetStore, ISkippedAssetsMaintenanceStore
    {
        internal int SkippedClearCalls { get; private set; }

        public Task<long> ClearAllLocationDataAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0L);

        public Task<IReadOnlyList<Guid>> ClearLocationDataForAssetsAsync(
            IReadOnlyCollection<Guid> assetIds,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);

        public Task<IReadOnlyList<Guid>> ClearLocationDataByValueAsync(
            LocationResetScope scope,
            string value,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);

        public Task<long> ClearAllAsync(CancellationToken cancellationToken = default)
        {
            SkippedClearCalls++;
            return Task.FromResult(5L);
        }

        public Task<long> RemoveAsync(
            IEnumerable<Guid> assetIds,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0L);
    }
}
