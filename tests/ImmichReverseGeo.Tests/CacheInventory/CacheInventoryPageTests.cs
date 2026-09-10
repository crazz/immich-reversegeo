using System.Collections.Immutable;
using System.Reflection;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerFailureRecovery;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests.CacheInventory;

[TestClass]
[TestCategory("Change53")]
public sealed class CacheInventoryPageTests
{
    [TestMethod]
    public async Task DataSummary_CountsOnlyAvailableInventoryEntries()
    {
        using var root = new TemporaryDirectory();
        var inventory = new FakeInventory(Snapshot());
        var page = new ImmichReverseGeo.Web.Components.Pages.Data();
        SetInjected(page, "CacheInventory", inventory);
        SetInjected(page, "Skipped", new SkippedAssetsRepository(
            NullLogger<SkippedAssetsRepository>.Instance,
            new StorageOptions(root.Path, root.Path)));
        await using var renderer = new WebStatusRenderingTests.ComponentRenderer();

        await renderer.AttachAsync(page);
        WebStatusRenderingTests.RenderSnapshot rendered = await renderer.ReadAsync();

        StringAssert.Contains(rendered.Text, "1 Overture cache(s), 0 GADM cache(s)");
        Assert.AreEqual(1, inventory.RefreshCalls);
    }

    [TestMethod]
    public async Task DataSummary_RendersSafeIncompleteSourceWarning()
    {
        using var root = new TemporaryDirectory();
        var inventory = new FakeInventory(new CacheInventorySnapshot(
            3,
            DateTimeOffset.UnixEpoch,
            [
                new CacheInventorySourceSnapshot(
                    CacheMutationSource.Overture,
                    CacheInventorySourceStatus.Truncated,
                    CacheInventoryDiagnosticCode.EnumerationTruncated,
                    []),
                new CacheInventorySourceSnapshot(
                    CacheMutationSource.Gadm,
                    CacheInventorySourceStatus.Ready,
                    null,
                    [])
            ]));
        var page = new ImmichReverseGeo.Web.Components.Pages.Data();
        SetInjected(page, "CacheInventory", inventory);
        SetInjected(page, "Skipped", new SkippedAssetsRepository(
            NullLogger<SkippedAssetsRepository>.Instance,
            new StorageOptions(root.Path, root.Path)));
        await using var renderer = new WebStatusRenderingTests.ComponentRenderer();

        await renderer.AttachAsync(page);
        WebStatusRenderingTests.RenderSnapshot rendered = await renderer.ReadAsync();

        StringAssert.Contains(rendered.Text,
            "Overture cache inventory is incomplete: too many directory entries.");
        Assert.IsFalse(rendered.Text.Contains(root.Path, StringComparison.Ordinal));
        Assert.AreEqual(1, inventory.RefreshCalls);
    }

    [TestMethod]
    public async Task GeoBoundaries_RendersStatusMetadataAndNoAreaCount()
    {
        using var root = new TemporaryDirectory();
        var inventory = new FakeInventory(Snapshot());
        var admission = new IdleAdmissionGate();
        var mutationFactory = new CacheMutationPageControllerFactory(
            admission,
            new UnusedMutationClient(),
            new CacheMutationPageControllerHostLifetime());
        string bundledData = Path.Combine(AppContext.BaseDirectory, "data");
        var deletionCommand = new CacheDeletionCommand(
            admission,
            new PhysicalCacheDeletionFileSystem(),
            new StorageOptions(root.Path, bundledData),
            CountryCodeService.CreateForTest(bundledData),
            NullLogger<CacheDeletionCommand>.Instance);
        var deletionFactory = new CacheInventoryDeletionPageControllerFactory(
            new CacheInventoryDeletionOperations(deletionCommand, inventory));
        var page = new ImmichReverseGeo.Web.Components.Pages.GeoBoundaries();
        SetInjected(page, "CacheInventory", inventory);
        SetInjected(page, "CacheMutations", mutationFactory);
        SetInjected(page, "CacheDeletions", deletionFactory);
        await using var renderer = new WebStatusRenderingTests.ComponentRenderer();

        await renderer.AttachAsync(page);
        WebStatusRenderingTests.RenderSnapshot rendered = await renderer.ReadAsync();

        StringAssert.Contains(rendered.Text, "Status");
        StringAssert.Contains(rendered.Text, "Last Modified");
        StringAssert.Contains(rendered.Text, "Available (operation in progress)");
        StringAssert.Contains(rendered.Text, "Invalid: required cache schema is missing");
        StringAssert.Contains(rendered.Text, "2026-09-10");
        StringAssert.Contains(rendered.Text, "Review the GADM license");
        Assert.IsFalse(rendered.Text.Contains("Area Count", StringComparison.Ordinal));
        Assert.AreEqual(1, inventory.RefreshCalls);
        await page.DisposeAsync();
    }

    [TestMethod]
    public async Task GeoBoundaries_RendersSourceWarningAndKeepsFilterSortProjectionDeterministic()
    {
        using var root = new TemporaryDirectory();
        DateTimeOffset modified = new(2026, 9, 10, 1, 2, 3, TimeSpan.Zero);
        var inventory = new FakeInventory(new CacheInventorySnapshot(
            8,
            modified,
            [
                new CacheInventorySourceSnapshot(
                    CacheMutationSource.Overture,
                    CacheInventorySourceStatus.Truncated,
                    CacheInventoryDiagnosticCode.EnumerationTruncated,
                    []),
                new CacheInventorySourceSnapshot(
                    CacheMutationSource.Gadm,
                    CacheInventorySourceStatus.Ready,
                    null,
                    [
                        new CacheInventoryEntry(
                            CacheMutationSource.Gadm,
                            "AUT",
                            CacheInventoryEntryStatus.Available,
                            null,
                            null,
                            null,
                            null,
                            false,
                            null),
                        new CacheInventoryEntry(
                            CacheMutationSource.Gadm,
                            "JPN",
                            CacheInventoryEntryStatus.InProgress,
                            null,
                            null,
                            null,
                            null,
                            true,
                            null)
                    ])
            ]));
        var admission = new IdleAdmissionGate();
        var mutationFactory = new CacheMutationPageControllerFactory(
            admission,
            new UnusedMutationClient(),
            new CacheMutationPageControllerHostLifetime());
        string bundledData = Path.Combine(AppContext.BaseDirectory, "data");
        var deletionFactory = new CacheInventoryDeletionPageControllerFactory(
            new CacheInventoryDeletionOperations(
                new CacheDeletionCommand(
                    admission,
                    new PhysicalCacheDeletionFileSystem(),
                    new StorageOptions(root.Path, bundledData),
                    CountryCodeService.CreateForTest(bundledData),
                    NullLogger<CacheDeletionCommand>.Instance),
                inventory));
        var page = new ImmichReverseGeo.Web.Components.Pages.GeoBoundaries();
        SetInjected(page, "CacheInventory", inventory);
        SetInjected(page, "CacheMutations", mutationFactory);
        SetInjected(page, "CacheDeletions", deletionFactory);
        await using var renderer = new WebStatusRenderingTests.ComponentRenderer();

        await renderer.AttachAsync(page);
        WebStatusRenderingTests.RenderSnapshot rendered = await renderer.ReadAsync();

        StringAssert.Contains(rendered.Text,
            "Overture cache inventory: Truncated: too many directory entries");
        StringAssert.Contains(rendered.Text, "In progress");
        StringAssert.Contains(rendered.Text, "—");

        SetPrivateEnumField(page, "_selectedSourceFilter", "Gadm");
        SetPrivateEnumField(page, "_sortColumn", "Country");
        SetPrivateField(page, "_sortAscending", false);
        CollectionAssert.AreEqual(
            new[] { "JPN", "AUT" },
            ProjectIso3Rows(page));
        SetPrivateField(page, "_countrySearch", "au");
        CollectionAssert.AreEqual(new[] { "AUT" }, ProjectIso3Rows(page));
        await page.DisposeAsync();
    }

    private static CacheInventorySnapshot Snapshot()
    {
        DateTimeOffset modified = new(2026, 9, 10, 1, 2, 3, TimeSpan.Zero);
        return new CacheInventorySnapshot(
            4,
            modified,
            [
                new CacheInventorySourceSnapshot(
                    CacheMutationSource.Overture,
                    CacheInventorySourceStatus.Ready,
                    null,
                    [
                        new CacheInventoryEntry(
                            CacheMutationSource.Overture,
                            "CHE",
                            CacheInventoryEntryStatus.Available,
                            2048,
                            modified,
                            modified,
                            "2026-09-01",
                            true,
                            null)
                    ]),
                new CacheInventorySourceSnapshot(
                    CacheMutationSource.Gadm,
                    CacheInventorySourceStatus.Ready,
                    null,
                    [
                        new CacheInventoryEntry(
                            CacheMutationSource.Gadm,
                            "JPN",
                            CacheInventoryEntryStatus.Invalid,
                            512,
                            modified,
                            null,
                            null,
                            false,
                            CacheInventoryDiagnosticCode.MissingExpectedTable)
                    ])
            ]);
    }

    private static void SetInjected(object component, string propertyName, object value)
    {
        PropertyInfo property = component.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new AssertFailedException($"Missing injected property {propertyName}.");
        property.SetValue(component, value);
    }

    private static void SetPrivateField(object component, string fieldName, object value)
    {
        FieldInfo field = component.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new AssertFailedException($"Missing field {fieldName}.");
        field.SetValue(component, value);
    }

    private static void SetPrivateEnumField(object component, string fieldName, string value)
    {
        FieldInfo field = component.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new AssertFailedException($"Missing field {fieldName}.");
        field.SetValue(component, Enum.Parse(field.FieldType, value));
    }

    private static string[] ProjectIso3Rows(object component)
    {
        MethodInfo method = component.GetType().GetMethod(
            "GetFilteredRows",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new AssertFailedException("Missing GetFilteredRows.");
        var rows = (System.Collections.IEnumerable)(method.Invoke(component, null)
            ?? throw new AssertFailedException("GetFilteredRows returned null."));
        return rows.Cast<object>()
            .Select(row => (string)(row.GetType().GetProperty("Iso3")?.GetValue(row)
                ?? throw new AssertFailedException("Cache row has no ISO3.")))
            .ToArray();
    }

    private sealed class FakeInventory(CacheInventorySnapshot snapshot) :
        ICacheInventory,
        ICacheInventoryInvalidator
    {
        internal int RefreshCalls { get; private set; }

        public Task<CacheInventorySnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(snapshot);

        public Task<CacheInventorySnapshot> RefreshAsync(
            CancellationToken cancellationToken = default)
        {
            RefreshCalls++;
            return Task.FromResult(snapshot);
        }

        public Task<CacheInventoryExactResult> GetExactAsync(
            CacheMutationSource source,
            string iso3,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void InvalidateKey(CacheMutationSource source, string iso3)
        {
        }

        public void InvalidateSource(CacheMutationSource source)
        {
        }

        public void InvalidateAll()
        {
        }
    }

    private sealed class IdleAdmissionGate : IWorkerJobAdmissionGate
    {
        public WorkerJobAdmissionResult TryAdmit(WorkerJobDispatch dispatch) =>
            throw new AssertFailedException("No worker action is expected while rendering inventory.");

        public CacheMaintenanceAdmissionResult TryReserveCacheMaintenance(
            CacheMaintenanceRequestOrigin origin) =>
            throw new AssertFailedException("No deletion is expected while rendering inventory.");
    }

    private sealed class UnusedMutationClient : ICacheMutationWorkerClient
    {
        public ValueTask<CacheMutationWorkerStartResult> StartAsync(
            IWorkerJobAdmissionLease admission,
            CacheMutationRequest request,
            IWorkerJobEventSink eventSink,
            CancellationToken cancellationToken) =>
            throw new AssertFailedException("No worker is expected while rendering inventory.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "cache-inventory-page-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }
}
