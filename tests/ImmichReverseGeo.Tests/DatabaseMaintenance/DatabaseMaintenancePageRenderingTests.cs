using System.Reflection;
using System.Runtime.ExceptionServices;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.Services;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable BL0006

namespace ImmichReverseGeo.Tests.DatabaseMaintenance;

[TestClass]
[TestCategory("Change54")]
public sealed class DatabaseMaintenancePageRenderingTests
{
    [TestMethod]
    public async Task SuccessfulRetry_HidesConsumedRetryButtonWhileKeepingOriginalPartialResult()
    {
        await using var coordinator = new WorkerJobCoordinator(WorkerJobDescriptors.Registered);
        var controller = new DatabaseMaintenanceController(
            coordinator,
            new NoOpImmichStore(),
            new NoOpSkippedStore(),
            NullLogger<DatabaseMaintenanceController>.Instance);
        var retry = new SkippedCleanupRetryCapability(controller, new SkippedCleanupTarget.All());
        var partial = new DatabaseMaintenanceResult(
            DatabaseMaintenanceOperation.ResetAll,
            DatabaseMaintenanceDisposition.Partial,
            DatabaseMaintenanceStageResult.Succeeded(2),
            DatabaseMaintenanceStageResult.Failed(false, "skipped-io", "Skipped cleanup failed."),
            "Immich reset completed; skipped cleanup failed.",
            retry: retry);
        var completedRetry = new DatabaseMaintenanceResult(
            DatabaseMaintenanceOperation.RetrySkippedCleanup,
            DatabaseMaintenanceDisposition.Complete,
            DatabaseMaintenanceStageResult.NotStarted,
            DatabaseMaintenanceStageResult.Succeeded(1),
            "Skipped cleanup completed.");
        var page = new StaticResetGeoDataPage();
        SetField(page, "_maintenanceResult", partial);
        SetField(page, "_retryResult", completedRetry);
        await using var renderer = new WebStatusRenderingTests.ComponentRenderer();

        await renderer.AttachAsync(page);
        WebStatusRenderingTests.RenderSnapshot rendered = await renderer.ReadAsync();

        StringAssert.Contains(rendered.Text, partial.Message);
        StringAssert.Contains(rendered.Text, completedRetry.Message);
        Assert.IsFalse(rendered.Text.Contains("Retry Skip List Cleanup", StringComparison.Ordinal));
        Assert.AreEqual(2, rendered.AttributeCount("role", "status"));
        Assert.AreEqual(2, rendered.AttributeCount("aria-live", "polite"));
    }

    [TestMethod]
    public async Task EveryFinalDisposition_RendersAnAccessibleTypedMessage()
    {
        foreach (DatabaseMaintenanceDisposition disposition in Enum.GetValues<DatabaseMaintenanceDisposition>())
        {
            string message = $"controlled-{disposition}";
            var page = new StaticResetGeoDataPage();
            SetField(page, "_maintenanceResult", new DatabaseMaintenanceResult(
                DatabaseMaintenanceOperation.ResetAll,
                disposition,
                DatabaseMaintenanceStageResult.NotStarted,
                DatabaseMaintenanceStageResult.NotStarted,
                message));
            await using var renderer = new WebStatusRenderingTests.ComponentRenderer();

            await renderer.AttachAsync(page);
            WebStatusRenderingTests.RenderSnapshot rendered = await renderer.ReadAsync();

            StringAssert.Contains(rendered.Text, message);
            Assert.IsTrue(rendered.HasAttribute("role", "status"), disposition.ToString());
            Assert.IsTrue(rendered.HasAttribute("aria-live", "polite"), disposition.ToString());
            Assert.IsTrue(
                rendered.HasCssClass(
                    "alert",
                    disposition == DatabaseMaintenanceDisposition.Complete
                        ? "alert-success"
                        : "alert-error"),
                disposition.ToString());
        }
    }

    [TestMethod]
    public async Task ServerReloadBoundaries_RejectQueuedCallsWhileMutationIsActive()
    {
        var resetPage = new StaticResetGeoDataPage();
        SetField(resetPage, "_operationActive", true);
        SetField(resetPage, "_generation", 17L);
        await InvokeTaskAsync(resetPage, "ReloadLocationOptionsAsync");
        Assert.AreEqual(17L, GetField<long>(resetPage, "_generation"));
        Assert.IsTrue(GetField<bool>(resetPage, "_operationActive"));

        var dataPage = new StaticDataPage();
        SetField(dataPage, "_skipOperationActive", true);
        SetField(dataPage, "_generation", 23L);
        await InvokeTaskAsync(dataPage, "ReloadSkippedCountAsync");
        Assert.AreEqual(23L, GetField<long>(dataPage, "_generation"));
        Assert.IsTrue(GetField<bool>(dataPage, "_skipOperationActive"));
    }

    [TestMethod]
    public async Task MatchingPartial_PageDisposalAndShutdownPreserveExactResultAndFenceRetry()
    {
        Guid returned = Guid.Parse("77777777-7777-7777-7777-777777777777");
        const string matchingValue = "  Old 'matching' value  ";
        var sqliteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSqlite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var frozen = new TaskCompletionSource<DatabaseMaintenanceResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var postgres = new CounterexampleImmichStore(returned);
        var skipped = new CounterexampleSkippedStore(sqliteEntered, releaseSqlite);
        await using var coordinator = new WorkerJobCoordinator(WorkerJobDescriptors.Registered);
        var controller = new DatabaseMaintenanceController(
            coordinator,
            postgres,
            skipped,
            NullLogger<DatabaseMaintenanceController>.Instance,
            result =>
            {
                frozen.TrySetResult(result);
                return Task.CompletedTask;
            });
        var page = new StaticResetGeoDataPage();
        SetInjected(page, "Maintenance", controller);
        SetField(page, "_selectedLocationScope", LocationResetScope.City);
        SetField(page, "_selectedLocationValue", matchingValue);
        await using var renderer = new WebStatusRenderingTests.ComponentRenderer();
        await renderer.AttachAsync(page);
        Task? pageCommand = null;
        Task? shutdown = null;
        Exception? primaryFailure = null;
        try
        {
            pageCommand = InvokeButtonAsync(renderer, "Reset Matching City");
            await sqliteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await page.DisposeAsync();
            shutdown = coordinator.BeginShutdown();
            Assert.IsFalse(shutdown.IsCompleted);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        await ReleaseAndDrainAsync(
            releaseSqlite,
            primaryFailure,
            pageCommand,
            shutdown);

        DatabaseMaintenanceResult partial = await frozen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(DatabaseMaintenanceDisposition.Partial, partial.Disposition);
        Assert.AreEqual(1L, partial.Postgres.Count);
        Assert.AreEqual(DatabaseMaintenanceStageStatus.Failed, partial.Skipped.Status);
        Assert.IsNotNull(partial.Retry);
        Assert.IsNull(GetField<DatabaseMaintenanceResult?>(page, "_maintenanceResult"));
        DatabaseMaintenanceResult fencedRetry = await controller.RetrySkippedCleanupAsync(partial.Retry);
        Assert.AreEqual(DatabaseMaintenanceDisposition.Unavailable, fencedRetry.Disposition);
        Assert.AreEqual(1, postgres.CallCount);
        Assert.AreEqual(matchingValue, postgres.Value);
        Assert.IsFalse(postgres.ValueStillMatches);
        Assert.AreEqual(1, skipped.CallCount);
        Assert.IsTrue(skipped.FaultReached);
        CollectionAssert.AreEqual(new[] { returned }, skipped.Ids.ToArray());
        Assert.IsFalse(partial.Message.Contains(matchingValue, StringComparison.Ordinal));
        Assert.IsFalse(partial.Message.Contains(returned.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(0, controller.TrackedOperationCount);
        Assert.IsNull(GetField<string?>(page, "_reloadError"));
    }

    [TestMethod]
    public async Task ReloadFailures_PreserveFinalMutationResultsAndActualDisplayedCount()
    {
        await using var coordinator = new WorkerJobCoordinator(WorkerJobDescriptors.Registered);
        var stores = new NoOpStores();
        var resetController = new DatabaseMaintenanceController(
            coordinator,
            stores,
            stores,
            NullLogger<DatabaseMaintenanceController>.Instance);
        var locationReadReached = false;
        ExclusiveHeavyOwnerBusyMetadata? ownerAtLocationRead = null;
        var locationReader = new RecordingLocationReader(() =>
        {
            locationReadReached = true;
            ownerAtLocationRead = coordinator.Snapshot.ActiveOwner;
            throw new IOException("controlled location reload failure");
        });
        var resetPage = new StaticResetGeoDataPage();
        SetInjected(resetPage, "Maintenance", resetController);
        SetInjected(resetPage, "Db", locationReader);
        SetField(resetPage, "_selectedLocationScope", LocationResetScope.Country);
        SetField(resetPage, "_selectedLocationValue", "Exact Country");
        await using var resetRenderer = new WebStatusRenderingTests.ComponentRenderer();
        await resetRenderer.AttachAsync(resetPage);

        await InvokeButtonAsync(resetRenderer, "Reset Matching Country");

        DatabaseMaintenanceResult? resetResult = GetField<DatabaseMaintenanceResult?>(
            resetPage,
            "_maintenanceResult");
        Assert.IsNotNull(resetResult);
        Assert.AreEqual(DatabaseMaintenanceDisposition.Complete, resetResult.Disposition);
        Assert.IsNotNull(GetField<string?>(resetPage, "_reloadError"));
        Assert.AreEqual(1, locationReader.CallCount);
        Assert.IsTrue(locationReadReached);
        Assert.IsNull(ownerAtLocationRead);

        var clearController = new DatabaseMaintenanceController(
            coordinator,
            stores,
            stores,
            NullLogger<DatabaseMaintenanceController>.Instance);
        var countReadReached = false;
        ExclusiveHeavyOwnerBusyMetadata? ownerAtCountRead = null;
        var countReader = new RecordingCountReader(() =>
        {
            countReadReached = true;
            ownerAtCountRead = coordinator.Snapshot.ActiveOwner;
            throw new IOException("controlled skipped count reload failure");
        });
        var dataPage = new StaticDataPage();
        SetInjected(dataPage, "Maintenance", clearController);
        SetInjected(dataPage, "Skipped", countReader);
        SetField(dataPage, "_skippedCount", 5L);
        await using var dataRenderer = new WebStatusRenderingTests.ComponentRenderer();
        await dataRenderer.AttachAsync(dataPage);

        await InvokeButtonAsync(dataRenderer, "Clear Skip List");

        DatabaseMaintenanceResult? clearResult = GetField<DatabaseMaintenanceResult?>(
            dataPage,
            "_skipClearResult");
        Assert.IsNotNull(clearResult);
        Assert.AreEqual(DatabaseMaintenanceDisposition.Complete, clearResult.Disposition);
        Assert.AreEqual(5L, GetField<long>(dataPage, "_skippedCount"));
        Assert.IsNotNull(GetField<string?>(dataPage, "_skipReloadError"));
        Assert.AreEqual(1, countReader.CallCount);
        Assert.IsTrue(countReadReached);
        Assert.IsNull(ownerAtCountRead);
    }

    [TestMethod]
    public async Task SuccessfulMutations_ReloadFreshValuesOnlyAfterRelease()
    {
        await using var coordinator = new WorkerJobCoordinator(WorkerJobDescriptors.Registered);
        var stores = new NoOpStores();
        var controller = new DatabaseMaintenanceController(
            coordinator,
            stores,
            stores,
            NullLogger<DatabaseMaintenanceController>.Instance);
        var locationReader = new RecordingLocationReader(() =>
        {
            Assert.IsNull(coordinator.Snapshot.ActiveOwner);
            return [new LocationValueOption("Fresh", 1)];
        });
        var resetPage = new StaticResetGeoDataPage();
        SetInjected(resetPage, "Maintenance", controller);
        SetInjected(resetPage, "Db", locationReader);
        SetField(resetPage, "_selectedLocationScope", LocationResetScope.City);
        SetField(resetPage, "_selectedLocationValue", "Old");
        await using var resetRenderer = new WebStatusRenderingTests.ComponentRenderer();
        await resetRenderer.AttachAsync(resetPage);

        await InvokeButtonAsync(resetRenderer, "Reset Matching City");

        Assert.AreEqual(3, locationReader.CallCount);
        Assert.IsNull(GetField<string?>(resetPage, "_reloadError"));
        Assert.AreEqual("Fresh", GetField<IReadOnlyList<LocationValueOption>>(
            resetPage,
            "_cityOptions").Single().Value);

        var countReader = new RecordingCountReader(() =>
        {
            Assert.IsNull(coordinator.Snapshot.ActiveOwner);
            return 0;
        });
        var dataPage = new StaticDataPage();
        SetInjected(dataPage, "Maintenance", controller);
        SetInjected(dataPage, "Skipped", countReader);
        SetField(dataPage, "_skippedCount", 5L);
        await using var dataRenderer = new WebStatusRenderingTests.ComponentRenderer();
        await dataRenderer.AttachAsync(dataPage);

        await InvokeButtonAsync(dataRenderer, "Clear Skip List");

        Assert.AreEqual(1, countReader.CallCount);
        Assert.AreEqual(0L, GetField<long>(dataPage, "_skippedCount"));
        Assert.IsNull(GetField<string?>(dataPage, "_skipReloadError"));
    }

    [TestMethod]
    public async Task RenderedResetAllAndSelectedButtons_PreserveSafeguardsAndParsing()
    {
        await using var coordinator = new WorkerJobCoordinator(WorkerJobDescriptors.Registered);
        var stores = new NoOpStores();
        var controller = new DatabaseMaintenanceController(
            coordinator,
            stores,
            stores,
            NullLogger<DatabaseMaintenanceController>.Instance);
        var reader = new RecordingLocationReader(static () => []);
        var resetAllPage = new StaticResetGeoDataPage();
        SetInjected(resetAllPage, "Maintenance", controller);
        SetInjected(resetAllPage, "Db", reader);
        await using var resetAllRenderer = new WebStatusRenderingTests.ComponentRenderer();
        await resetAllRenderer.AttachAsync(resetAllPage);

        await InvokeTaskAsync(resetAllPage, "ResetAllConfirmed");
        DatabaseMaintenanceResult unconfirmed = GetField<DatabaseMaintenanceResult?>(
            resetAllPage,
            "_maintenanceResult")!;
        Assert.AreEqual(DatabaseMaintenanceDisposition.Validation, unconfirmed.Disposition);
        Assert.AreEqual(0, stores.PostgresCalls);
        Assert.AreEqual(0, stores.SkippedCalls);
        await InvokeButtonAsync(resetAllRenderer, "Reset All Data…");
        WebStatusRenderingTests.RenderSnapshot confirmation = await resetAllRenderer.ReadAsync();
        StringAssert.Contains(confirmation.Text, "Yes, reset all geo data");
        StringAssert.Contains(confirmation.Text, "Cancel");
        await InvokeButtonAsync(resetAllRenderer, "Cancel");
        Assert.AreEqual(0, stores.PostgresCalls);
        Assert.AreEqual(0, stores.SkippedCalls);
        await InvokeButtonAsync(resetAllRenderer, "Reset All Data…");
        await InvokeButtonAsync(resetAllRenderer, "Yes, reset all geo data");
        Assert.AreEqual(1, stores.PostgresCalls);
        Assert.AreEqual(1, stores.SkippedCalls);

        var invalidPage = new StaticResetGeoDataPage();
        SetInjected(invalidPage, "Maintenance", controller);
        SetInjected(invalidPage, "Db", reader);
        SetField(invalidPage, "_assetIdsInput", "bad-token");
        await using var invalidRenderer = new WebStatusRenderingTests.ComponentRenderer();
        await invalidRenderer.AttachAsync(invalidPage);
        await InvokeButtonAsync(invalidRenderer, "Reset Selected Items");
        DatabaseMaintenanceResult invalid = GetField<DatabaseMaintenanceResult?>(
            invalidPage,
            "_maintenanceResult")!;
        Assert.AreEqual(DatabaseMaintenanceDisposition.Validation, invalid.Disposition);
        Assert.AreEqual(1, invalid.InvalidTokenCount);
        Assert.AreEqual(1, stores.PostgresCalls);

        Guid selected = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var selectedPage = new StaticResetGeoDataPage();
        SetInjected(selectedPage, "Maintenance", controller);
        SetInjected(selectedPage, "Db", reader);
        SetField(selectedPage, "_assetIdsInput", $"{selected}, invalid, {selected}");
        await using var selectedRenderer = new WebStatusRenderingTests.ComponentRenderer();
        await selectedRenderer.AttachAsync(selectedPage);
        await InvokeButtonAsync(selectedRenderer, "Reset Selected Items");

        CollectionAssert.AreEqual(new[] { selected }, stores.SelectedIds.ToArray());
        CollectionAssert.AreEqual(new[] { selected }, stores.RemovedIds.ToArray());
        DatabaseMaintenanceResult selectedResult = GetField<DatabaseMaintenanceResult?>(
            selectedPage,
            "_maintenanceResult")!;
        Assert.AreEqual(1, selectedResult.InvalidTokenCount);
    }

    [TestMethod]
    public async Task RenderedMatchingActions_UseEveryClosedScopeWithoutConfirmation()
    {
        await using var coordinator = new WorkerJobCoordinator(WorkerJobDescriptors.Registered);
        var stores = new NoOpStores();
        var controller = new DatabaseMaintenanceController(
            coordinator,
            stores,
            stores,
            NullLogger<DatabaseMaintenanceController>.Instance);
        foreach (LocationResetScope scope in Enum.GetValues<LocationResetScope>())
        {
            string value = $"  exact-{scope}  ";
            var page = new StaticResetGeoDataPage();
            SetInjected(page, "Maintenance", controller);
            SetInjected(page, "Db", new RecordingLocationReader(static () => []));
            SetField(page, "_selectedLocationScope", scope);
            SetField(page, "_selectedLocationValue", value);
            await using var renderer = new WebStatusRenderingTests.ComponentRenderer();
            await renderer.AttachAsync(page);

            WebStatusRenderingTests.RenderSnapshot before = await renderer.ReadAsync();
            Assert.IsFalse(before.Text.Contains("Yes, reset all geo data", StringComparison.Ordinal));
            await InvokeButtonAsync(renderer, $"Reset Matching {scope}");

            Assert.AreEqual(scope, stores.MatchingScope);
            Assert.AreEqual(value, stores.MatchingValue);
            DatabaseMaintenanceResult result = GetField<DatabaseMaintenanceResult?>(
                page,
                "_maintenanceResult")!;
            Assert.AreEqual(DatabaseMaintenanceDisposition.Complete, result.Disposition);
        }
    }

    [TestMethod]
    public async Task ActiveMutations_DisableEveryControlAndAnnounceNonCancellableWork()
    {
        await using var coordinator = new WorkerJobCoordinator(WorkerJobDescriptors.Registered);
        var postgres = new BlockingImmichStore();
        var controller = new DatabaseMaintenanceController(
            coordinator,
            postgres,
            new NoOpSkippedStore(),
            NullLogger<DatabaseMaintenanceController>.Instance);
        var resetPage = new StaticResetGeoDataPage();
        SetInjected(resetPage, "Maintenance", controller);
        SetInjected(resetPage, "Db", new RecordingLocationReader(static () => []));
        SetField(resetPage, "_selectedLocationScope", LocationResetScope.City);
        SetField(resetPage, "_selectedLocationValue", "Held");
        SetField(resetPage, "_assetIdsInput", Guid.NewGuid().ToString());
        await using var resetRenderer = new WebStatusRenderingTests.ComponentRenderer();
        await resetRenderer.AttachAsync(resetPage);
        Task? resetRun = null;
        Exception? resetFailure = null;
        try
        {
            resetRun = InvokeButtonAsync(resetRenderer, "Reset Matching City");
            await postgres.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            WebStatusRenderingTests.RenderSnapshot active = await resetRenderer.ReadAsync();

            AssertButtonDisabled(active, "Reset All Data…");
            AssertButtonDisabled(active, "Reset Selected Items");
            AssertButtonDisabled(active, "Reset Matching City");
            Assert.IsTrue(HasDisabledElement(active, "textarea"));
            Assert.AreEqual(3, CountDisabledElements(active, "select"));
            StringAssert.Contains(active.Text, "This operation cannot be cancelled.");
            StringAssert.Contains(active.Text, "role=\"status\"");
            StringAssert.Contains(active.Text, "aria-live=\"polite\"");
            Assert.IsFalse(HasButton(active, "Cancel"));
        }
        catch (Exception exception)
        {
            resetFailure = exception;
        }

        await ReleaseAndDrainAsync(postgres.Release, resetFailure, resetRun);

        var skipped = new BlockingSkippedStore();
        var dataController = new DatabaseMaintenanceController(
            coordinator,
            new NoOpImmichStore(),
            skipped,
            NullLogger<DatabaseMaintenanceController>.Instance);
        var dataPage = new StaticDataPage();
        SetInjected(dataPage, "Maintenance", dataController);
        SetInjected(dataPage, "Skipped", new RecordingCountReader(static () => 0));
        SetField(dataPage, "_skippedCount", 2L);
        await using var dataRenderer = new WebStatusRenderingTests.ComponentRenderer();
        await dataRenderer.AttachAsync(dataPage);
        Task? clearRun = null;
        Exception? clearFailure = null;
        try
        {
            clearRun = InvokeButtonAsync(dataRenderer, "Clear Skip List");
            await skipped.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            WebStatusRenderingTests.RenderSnapshot active = await dataRenderer.ReadAsync();

            AssertButtonDisabled(active, "Clear Skip List");
            StringAssert.Contains(active.Text, "This operation cannot be cancelled.");
            StringAssert.Contains(active.Text, "role=\"status\"");
            StringAssert.Contains(active.Text, "aria-live=\"polite\"");
            Assert.IsFalse(HasButton(active, "Cancel"));
        }
        catch (Exception exception)
        {
            clearFailure = exception;
        }

        await ReleaseAndDrainAsync(skipped.Release, clearFailure, clearRun);
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

    private static async Task InvokeTaskAsync(object target, string methodName)
    {
        MethodInfo method = target.GetType().BaseType!
            .GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic, [])
            ?? throw new AssertFailedException($"Could not find {methodName}.");
        await ((Task)method.Invoke(target, null)!).WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task ReleaseAndDrainAsync(
        TaskCompletionSource release,
        Exception? primaryFailure,
        params Task?[] operations)
    {
        release.TrySetResult();
        Exception? cleanupFailure = null;
        try
        {
            await Task.WhenAll(operations
                .Where(operation => operation is not null)
                .Select(operation => operation!.WaitAsync(TimeSpan.FromSeconds(5))));
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
        }

        if (primaryFailure is not null && cleanupFailure is not null)
        {
            throw new AggregateException(primaryFailure, cleanupFailure);
        }

        if (primaryFailure is not null)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }

        if (cleanupFailure is not null)
        {
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }
    }

    private static void AssertButtonDisabled(
        WebStatusRenderingTests.RenderSnapshot snapshot,
        string label)
    {
        Assert.IsTrue(
            FindButtonFrames(snapshot, label).Any(frame =>
                frame.FrameType == RenderTreeFrameType.Attribute
                && string.Equals(frame.AttributeName, "disabled", StringComparison.Ordinal)),
            $"Rendered button '{label}' was not disabled.");
    }

    private static bool HasButton(
        WebStatusRenderingTests.RenderSnapshot snapshot,
        string label) => FindButtonFrames(snapshot, label).Any();

    private static IEnumerable<RenderTreeFrame> FindButtonFrames(
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
            if (string.Equals(content.Trim(), label, StringComparison.Ordinal))
            {
                return subtree;
            }
        }

        return [];
    }

    private static bool HasDisabledElement(
        WebStatusRenderingTests.RenderSnapshot snapshot,
        string elementName) => CountDisabledElements(snapshot, elementName) > 0;

    private static int CountDisabledElements(
        WebStatusRenderingTests.RenderSnapshot snapshot,
        string elementName)
    {
        var count = 0;
        for (var elementIndex = 0; elementIndex < snapshot.Frames.Length; elementIndex++)
        {
            RenderTreeFrame element = snapshot.Frames[elementIndex];
            if (element.FrameType != RenderTreeFrameType.Element
                || !string.Equals(element.ElementName, elementName, StringComparison.Ordinal))
            {
                continue;
            }

            if (snapshot.Frames
                .Skip(elementIndex + 1)
                .Take(element.ElementSubtreeLength - 1)
                .Any(frame => frame.FrameType == RenderTreeFrameType.Attribute
                    && string.Equals(frame.AttributeName, "disabled", StringComparison.Ordinal)))
            {
                count++;
            }
        }

        return count;
    }

    private static void SetField(object target, string name, object value)
    {
        target.GetType().BaseType!
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(target, value);
    }

    private static void SetInjected(object target, string name, object value)
    {
        target.GetType().BaseType!
            .GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(target, value);
    }

    private static T GetField<T>(object target, string name)
    {
        return (T)target.GetType().BaseType!
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(target)!;
    }

    private sealed class StaticResetGeoDataPage
        : ImmichReverseGeo.Web.Components.Pages.ResetGeoData
    {
        protected override Task OnInitializedAsync() => Task.CompletedTask;
    }

    private sealed class StaticDataPage
        : ImmichReverseGeo.Web.Components.Pages.Data
    {
        protected override Task OnInitializedAsync() => Task.CompletedTask;
    }

    private sealed class NoOpImmichStore : IImmichLocationResetStore
    {
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
    }

    private sealed class NoOpSkippedStore : ISkippedAssetsMaintenanceStore
    {
        public Task<long> ClearAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0L);

        public Task<long> RemoveAsync(
            IEnumerable<Guid> assetIds,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0L);
    }

    private sealed class CounterexampleImmichStore(Guid returned) : IImmichLocationResetStore
    {
        internal int CallCount { get; private set; }
        internal string? Value { get; private set; }
        internal bool ValueStillMatches { get; private set; } = true;

        public Task<long> ClearAllLocationDataAsync(CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Reset All was outside the counterexample scope.");

        public Task<IReadOnlyList<Guid>> ClearLocationDataForAssetsAsync(
            IReadOnlyCollection<Guid> assetIds,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Selected reset was outside the counterexample scope.");

        public Task<IReadOnlyList<Guid>> ClearLocationDataByValueAsync(
            LocationResetScope scope,
            string value,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Value = value;
            ValueStillMatches = false;
            return Task.FromResult<IReadOnlyList<Guid>>([returned]);
        }
    }

    private sealed class CounterexampleSkippedStore(
        TaskCompletionSource entered,
        TaskCompletionSource release) : ISkippedAssetsMaintenanceStore
    {
        internal int CallCount { get; private set; }
        internal IReadOnlyList<Guid> Ids { get; private set; } = [];
        internal bool FaultReached { get; private set; }

        public Task<long> ClearAllAsync(CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Clear All was outside the counterexample scope.");

        public async Task<long> RemoveAsync(
            IEnumerable<Guid> assetIds,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Ids = assetIds.ToArray();
            entered.TrySetResult();
            await release.Task;
            FaultReached = true;
            throw new IOException("private skipped database path");
        }
    }

    private sealed class BlockingImmichStore : IImmichLocationResetStore
    {
        internal Task Entered => _entered.Task;
        internal TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<long> ClearAllLocationDataAsync(CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Reset All was outside the active-render scope.");

        public Task<IReadOnlyList<Guid>> ClearLocationDataForAssetsAsync(
            IReadOnlyCollection<Guid> assetIds,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Selected reset was outside the active-render scope.");

        public async Task<IReadOnlyList<Guid>> ClearLocationDataByValueAsync(
            LocationResetScope scope,
            string value,
            CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
            await Release.Task;
            return [];
        }
    }

    private sealed class BlockingSkippedStore : ISkippedAssetsMaintenanceStore
    {
        internal Task Entered => _entered.Task;
        internal TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<long> ClearAllAsync(CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
            await Release.Task;
            return 2;
        }

        public Task<long> RemoveAsync(
            IEnumerable<Guid> assetIds,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Targeted removal was outside the active-render scope.");
    }

    private sealed class NoOpStores : IImmichLocationResetStore, ISkippedAssetsMaintenanceStore
    {
        internal int PostgresCalls { get; private set; }
        internal int SkippedCalls { get; private set; }
        internal IReadOnlyList<Guid> SelectedIds { get; private set; } = [];
        internal IReadOnlyList<Guid> RemovedIds { get; private set; } = [];
        internal LocationResetScope? MatchingScope { get; private set; }
        internal string? MatchingValue { get; private set; }

        public Task<long> ClearAllLocationDataAsync(CancellationToken cancellationToken = default)
        {
            PostgresCalls++;
            return Task.FromResult(0L);
        }

        public Task<IReadOnlyList<Guid>> ClearLocationDataForAssetsAsync(
            IReadOnlyCollection<Guid> assetIds,
            CancellationToken cancellationToken = default)
        {
            PostgresCalls++;
            SelectedIds = assetIds.ToArray();
            return Task.FromResult<IReadOnlyList<Guid>>([]);
        }

        public Task<IReadOnlyList<Guid>> ClearLocationDataByValueAsync(
            LocationResetScope scope,
            string value,
            CancellationToken cancellationToken = default)
        {
            PostgresCalls++;
            MatchingScope = scope;
            MatchingValue = value;
            return Task.FromResult<IReadOnlyList<Guid>>([]);
        }

        public Task<long> ClearAllAsync(CancellationToken cancellationToken = default)
        {
            SkippedCalls++;
            return Task.FromResult(5L);
        }

        public Task<long> RemoveAsync(
            IEnumerable<Guid> assetIds,
            CancellationToken cancellationToken = default)
        {
            SkippedCalls++;
            RemovedIds = assetIds.ToArray();
            return Task.FromResult((long)RemovedIds.Count);
        }
    }

    private sealed class RecordingLocationReader(Func<IReadOnlyList<LocationValueOption>> read)
        : ILocationValueOptionsReader
    {
        internal int CallCount { get; private set; }

        public Task<IReadOnlyList<LocationValueOption>> GetLocationValueOptionsAsync(
            LocationResetScope scope,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(read());
        }
    }

    private sealed class RecordingCountReader(Func<long> read) : ISkippedAssetsCountReader
    {
        internal int CallCount { get; private set; }

        public Task<long> GetCountAsync()
        {
            CallCount++;
            return Task.FromResult(read());
        }
    }
}
