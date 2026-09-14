using System.Reflection;
using System.Threading.Channels;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Gadm.Services;
using ImmichReverseGeo.Overture.Services;
using ImmichReverseGeo.Tests.LookupWorkerRouting;
using ImmichReverseGeo.Tests.ChildWorkerCancellation;
using ImmichReverseGeo.Web.WorkerEventDelivery;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Services;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

#pragma warning disable BL0006

namespace ImmichReverseGeo.Tests.CacheMutationRouting;

[TestClass]
[TestCategory("Change51")]
[DoNotParallelize]
public sealed class GeoBoundariesCacheMutationBindingTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [TestMethod]
    [TestCategory("Change65")]
    public async Task DataCadence_UsesActualControllerCallbackForBurstAndImmediateFinalState()
    {
        var clock = new CancellationTestClock();
        await using var fixture = await RenderedPageFixture.CreateAsync(
            includeOverture: true, includeGadm: false, time: clock, policy: new WorkerEventDeliveryPolicy());
        var controller = (CacheMutationPageController)fixture.Page.GetType()
            .GetField("_cacheController", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Page)!;
        fixture.Click = controller.RefreshAsync(CacheMutationSource.Overture, "CHE");
        var session = await fixture.Worker.WaitForSessionAsync();
        await session.CompletionObserved.WaitAsync(Bound);
        int before = fixture.Renderer.RenderCount;
        for (int index = 1; index <= 1000; index++)
        {
            await session.EmitAsync(new WorkerJobOutputMessage(
                WorkerJobProtocolV2.ProgressCategory, WorkerJobProtocolV2.ProgressChangedType, index + 1,
                DateTimeOffset.UnixEpoch, session.JobId, WorkerJobKind.CacheMutation,
                new CacheMutationProgressPayload(CacheMutationProgressStep.ValidatingCandidate,
                    CacheMutationSource.Overture, CacheMutationOperation.Refresh, "CHE", $"Rendered cache step {index}", null)));
        }

        Assert.AreEqual(0L, controller.NotificationObservation!.OrdinaryDispatched);
        Assert.AreEqual(before, fixture.Renderer.RenderCount);
        Task rendered = fixture.Renderer.NextRenderAsync();
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await rendered.WaitAsync(Bound);
        StringAssert.Contains((await fixture.Renderer.ReadAsync()).Text, "Rendered cache step 1000");
        Assert.AreEqual(before + 1, fixture.Renderer.RenderCount);
        AssertMutationControls((await fixture.Renderer.ReadAsync()).Frames, disabled: true);
        session.Complete(new CacheMutationWorkerOutcome.Completed(Result("cadence-release")));
        await fixture.Click.WaitAsync(Bound);
        await fixture.Renderer.Dispatcher.InvokeAsync(() => { });
        Assert.AreEqual(1L, controller.NotificationObservation.FinalAccepted);
        Assert.AreEqual(CacheMutationPagePhase.Completed, controller.State.Phase);
        // Reload is an existing separate callback; final cadence still posts the complete result.
        while (true)
        {
            Task next = fixture.Renderer.NextRenderAsync();
            if ((await fixture.Renderer.ReadAsync()).Text.Contains("Cache refresh completed.", StringComparison.Ordinal))
            {
                break;
            }

            await next.WaitAsync(Bound);
        }

        StringAssert.Contains((await fixture.Renderer.ReadAsync()).Text, "Cache refresh completed.");
        AssertMutationControls((await fixture.Renderer.ReadAsync()).Frames, disabled: false);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(1L, controller.NotificationObservation.OrdinaryDispatched);
    }

    [TestMethod]
    [DataRow("completed-after-publication")]
    [DataRow("cancelled-after-publication")]
    public async Task RenderedRedownloadAction_ReloadsPublishedDiskStateAfterOwnedFinality(
        string outcome)
    {
        using var root = new TemporaryDirectory();
        CreateOvertureCache(root.Path, "old-release");
        string cachePath = Path.Combine(root.Path, "overture-divisions", "CHE.db");
        byte[] oldCache = File.ReadAllBytes(cachePath);
        var overture = new OvertureDivisionCacheService(
            NullLogger<OvertureDivisionCacheService>.Instance,
            root.Path,
            static iso3 => iso3 == "CHE" ? "CH" : null);
        var gadm = new GadmDivisionCacheService(
            NullLogger<GadmDivisionCacheService>.Instance,
            root.Path);
        var admission = new RecordingAdmissionGate();
        var worker = new RecordingWorkerClient();
        var lifetime = new CacheMutationPageControllerHostLifetime();
        var factory = new CacheMutationPageControllerFactory(admission, worker, lifetime);
        var inventory = CreateInventory(root.Path);
        var page = new ImmichReverseGeo.Web.Components.Pages.GeoBoundaries();
        SetInjected(page, "CacheInventory", inventory);
        SetInjected(page, "CacheMutations", factory);
        SetInjected(page, "CacheDeletions", CreateDeletionFactory(root.Path, admission, inventory));
        await using var renderer = new WebStatusRenderingTests.ComponentRenderer();
        await renderer.AttachAsync(page);

        Task? click = null;
        FakeSession? session = null;
        try
        {
            click = InvokeButtonAsync(renderer, "Re-download");
            Task<FakeSession> pendingSession = worker.WaitForSessionAsync();
            Task first = await Task.WhenAny(click, pendingSession);
            if (ReferenceEquals(first, click))
            {
                await click;
                Assert.Fail("The rendered action completed without starting a cache worker.");
            }

            session = await pendingSession;
            Assert.AreEqual(CacheMutationSource.Overture, worker.Request!.Source);
            Assert.AreEqual(CacheMutationOperation.Refresh, worker.Request.Operation);
            Assert.AreEqual("CHE", worker.Request.Iso3);
            Assert.AreEqual(WorkerJobKind.CacheMutation, admission.Dispatch!.Context.JobKind);
            CollectionAssert.AreEqual(
                oldCache,
                File.ReadAllBytes(cachePath),
                "rendered refresh preserves the readable old cache through worker readiness");
            Assert.AreEqual("old-release", overture.GetStatus()["CHE"].Release);

            CreateOvertureCache(root.Path, "new-release");
            session.Complete(outcome == "completed-after-publication"
                ? new CacheMutationWorkerOutcome.Completed(Result("new-release"))
                : new CacheMutationWorkerOutcome.Cancelled());
            await click.WaitAsync(Bound);

            WebStatusRenderingTests.RenderSnapshot rendered = await renderer.ReadAsync();
            StringAssert.Contains(rendered.Text, "new-release");
            StringAssert.Contains(
                rendered.Text,
                outcome == "completed-after-publication"
                    ? "Cache refresh completed."
                    : "Cache refresh cancelled.");
            Assert.AreEqual(1, admission.Lease!.DisposeCount);
        }
        finally
        {
            session ??= worker.Session;
            session?.Complete(new CacheMutationWorkerOutcome.Cancelled());
            await DrainCleanupAsync(
                () => page.DisposeAsync().AsTask().WaitAsync(Bound),
                () => click?.WaitAsync(Bound) ?? Task.CompletedTask);
        }
    }

    [TestMethod]
    [DataRow("available")]
    [DataRow("active")]
    [DataRow("completed")]
    [DataRow("failed")]
    [DataRow("cancelled")]
    public async Task RenderedGadmNotice_RemainsSeparateFromMutationStatus(
        string phase)
    {
        await using RenderedPageFixture fixture = await RenderedPageFixture.CreateAsync(
            includeOverture: false,
            includeGadm: true);

        if (phase != "available")
        {
            fixture.Click = InvokeButtonAsync(fixture.Renderer, "Re-download");
            FakeSession session = await fixture.Worker.WaitForSessionAsync();
            await session.CompletionObserved.WaitAsync(Bound);

            if (phase == "active")
            {
                Task rendered = fixture.Renderer.NextRenderAsync();
                await session.EmitAsync(Started(session.JobId));
                await rendered.WaitAsync(Bound);
            }
            else
            {
                session.Complete(phase switch
                {
                    "completed" => new CacheMutationWorkerOutcome.Completed(GadmResult()),
                    "failed" => new CacheMutationWorkerOutcome.Failed(
                        "controlled-technical-error",
                        "Controlled storage failure."),
                    "cancelled" => new CacheMutationWorkerOutcome.Cancelled(),
                    _ => throw new AssertFailedException($"Unknown phase '{phase}'.")
                });
                await fixture.Click!.WaitAsync(Bound);
            }
        }

        WebStatusRenderingTests.RenderSnapshot snapshot = await fixture.Renderer.ReadAsync();
        string? expectedStatus = phase switch
        {
            "available" => null,
            "active" => "Cache worker started.",
            "completed" => "Cache refresh completed.",
            "failed" => "Cache refresh failed.",
            "cancelled" => "Cache refresh cancelled.",
            _ => throw new AssertFailedException($"Unknown phase '{phase}'.")
        };
        AssertGadmNoticeAndSeparateStatus(
            snapshot,
            expectedStatus,
            phase == "failed" ? "Controlled storage failure. (controlled-technical-error)" : null,
            phase switch
            {
                "available" => null,
                "completed" => "alert-success",
                "failed" => "alert-error",
                _ => "alert-info"
            });
        AssertMutationControls(snapshot.Frames, disabled: phase == "active");
        Assert.AreEqual(
            phase == "active",
            HasButton(snapshot.Frames, "Cancel", disabled: false));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task RenderedRejectedAdmission_DistinguishesBusyFromUnavailable(
        bool busy)
    {
        WorkerJobAdmissionResult rejection = busy
            ? new WorkerJobAdmissionResult.Busy(
                new ExclusiveHeavyOwnerBusyMetadata.Worker(
                    new WorkerJobBusyMetadata(
                        WorkerJobCapabilityFamily.Processing,
                        WorkerJobRequestOrigin.Scheduled,
                        true)))
            : new WorkerJobAdmissionResult.Unavailable(
                "worker-stopping",
                "Cache worker admission is unavailable.");
        await using RenderedPageFixture fixture = await RenderedPageFixture.CreateAsync(
            includeOverture: false,
            includeGadm: true,
            rejection: rejection);

        fixture.Click = InvokeButtonAsync(fixture.Renderer, "Re-download");
        await fixture.Click!.WaitAsync(Bound);
        WebStatusRenderingTests.RenderSnapshot snapshot = await fixture.Renderer.ReadAsync();

        StringAssert.Contains(
            snapshot.Text,
            busy
                ? "Another worker job is active. Try again after it finishes."
                : "Cache worker admission is unavailable.");
        if (busy)
        {
            StringAssert.Contains(snapshot.Text, "Active: Processing / Scheduled / Admitted");
        }
        else
        {
            Assert.IsFalse(snapshot.Text.Contains("Active:", StringComparison.Ordinal));
        }

        Assert.AreEqual(0, fixture.Worker.Starts);
        Assert.IsFalse(HasButton(snapshot.Frames, "Cancel"));
        AssertMutationControls(snapshot.Frames, disabled: false);
        AssertGadmNoticeAndSeparateStatus(
            snapshot,
            busy
                ? "Another worker job is active. Try again after it finishes."
                : "Cache worker admission is unavailable.",
            expectedError: null,
            expectedAlertClass: "alert-info");
    }

    [TestMethod]
    public async Task RenderedControls_StayDisabledThroughAcceptedTerminalUntilCapabilityFinality()
    {
        var startRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using RenderedPageFixture fixture = await RenderedPageFixture.CreateAsync(
            includeOverture: false,
            includeGadm: true,
            startRelease: startRelease);

        Task admissionRender = fixture.Renderer.NextRenderAsync();
        fixture.Click = InvokeButtonAsync(fixture.Renderer, "Re-download");
        await fixture.Worker.StartObserved.WaitAsync(Bound);
        await admissionRender.WaitAsync(Bound);
        WebStatusRenderingTests.RenderSnapshot admitted = await fixture.Renderer.ReadAsync();
        AssertMutationControls(admitted.Frames, disabled: true);

        startRelease.TrySetResult();
        FakeSession session = await fixture.Worker.WaitForSessionAsync();
        await session.CompletionObserved.WaitAsync(Bound);
        Task runningRender = fixture.Renderer.NextRenderAsync();
        await session.EmitAsync(Started(session.JobId));
        await runningRender.WaitAsync(Bound);
        WebStatusRenderingTests.RenderSnapshot running = await fixture.Renderer.ReadAsync();
        AssertMutationControls(running.Frames, disabled: true);
        Assert.IsTrue(HasButton(running.Frames, "Cancel", disabled: false));

        Task terminalRender = fixture.Renderer.NextRenderAsync();
        await session.EmitAsync(Terminal(GadmResult(), session.JobId));
        await terminalRender.WaitAsync(Bound);
        WebStatusRenderingTests.RenderSnapshot terminal = await fixture.Renderer.ReadAsync();
        StringAssert.Contains(terminal.Text, "Finishing cache refresh…");
        AssertMutationControls(terminal.Frames, disabled: true);
        Assert.IsFalse(HasButton(terminal.Frames, "Cancel"));

        session.Complete(new CacheMutationWorkerOutcome.Completed(GadmResult()));
        await fixture.Click!.WaitAsync(Bound);
        WebStatusRenderingTests.RenderSnapshot completed = await fixture.Renderer.ReadAsync();
        StringAssert.Contains(completed.Text, "Cache refresh completed.");
        AssertMutationControls(completed.Frames, disabled: false);
        Assert.IsFalse(HasButton(completed.Frames, "Cancel"));
        Assert.AreEqual(1, fixture.Admission.Lease!.DisposeCount);
    }

    [TestMethod]
    [TestCategory("Change52")]
    public async Task RenderedDeleteAll_RequiresSourceNamedConfirmationAndDismissDoesNotRunCommand()
    {
        await using RenderedPageFixture fixture = await RenderedPageFixture.CreateAsync(
            includeOverture: true,
            includeGadm: false);
        string cachePath = Path.Combine(
            fixture.RootPath,
            "overture-divisions",
            "CHE.db");

        await InvokeButtonAsync(fixture.Renderer, "Delete All Overture Divisions");
        WebStatusRenderingTests.RenderSnapshot confirmation = await fixture.Renderer.ReadAsync();
        StringAssert.Contains(confirmation.Text, "Delete all Overture cache files?");
        StringAssert.Contains(confirmation.Text, "best-effort");
        Assert.IsTrue(HasButton(confirmation.Frames, "Confirm Delete All", disabled: false));
        Assert.IsTrue(HasButton(confirmation.Frames, "Keep caches", disabled: false));
        AssertMutationControls(confirmation.Frames, disabled: true);
        Assert.AreEqual(0, fixture.Admission.MaintenanceAttempts);

        await InvokeButtonAsync(fixture.Renderer, "Keep caches");
        Assert.IsTrue(File.Exists(cachePath));
        Assert.AreEqual(0, fixture.Admission.MaintenanceAttempts);

        await InvokeButtonAsync(fixture.Renderer, "Delete All Overture Divisions");
        await InvokeButtonAsync(fixture.Renderer, "Confirm Delete All");
        WebStatusRenderingTests.RenderSnapshot completed = await fixture.Renderer.ReadAsync();

        Assert.IsFalse(File.Exists(cachePath));
        Assert.AreEqual(1, fixture.Admission.MaintenanceAttempts);
        Assert.AreEqual(
            CacheMaintenanceRequestOrigin.GeoBoundariesPage,
            fixture.Admission.MaintenanceOrigin);
        Assert.AreEqual(1, fixture.Admission.MaintenanceReservation!.DisposeCount);
        Assert.AreEqual(0, fixture.Worker.Starts);
        StringAssert.Contains(completed.Text, "Overture cache deletion completed");
        StringAssert.Contains(
            completed.Text,
            "No cached administrative area data matches the current filters.");
        Assert.IsFalse(HasButton(completed.Frames, "Cancel"));
    }

    [TestMethod]
    [TestCategory("Change53")]
    public async Task RenderedIncompleteSource_BlocksDeleteAllWhileReadyPeerKeepsNormalConfirmation()
    {
        await using RenderedPageFixture fixture = await RenderedPageFixture.CreateAsync(
            includeOverture: true,
            includeGadm: true,
            inventoryOptions: new CacheInventoryOptions
            {
                MaxVisitedEntriesPerSource = 1,
                MaxLogicalCandidatesPerSource = 1
            },
            overtureJunkEntries: 1);
        string hiddenOvertureCache = Path.Combine(
            fixture.RootPath,
            "overture-divisions",
            "CHE.db");
        WebStatusRenderingTests.RenderSnapshot rendered = await fixture.Renderer.ReadAsync();

        await InvokePrivateTask(fixture.Page, "DeleteAllOvertureDivisions");
        CacheDeletionPageController deletion = GetPrivateField<CacheDeletionPageController>(
            fixture.Page,
            "_deletionController");
        Assert.AreEqual(CacheDeletionPagePhase.Idle, deletion.State.Phase,
            "server-handler-rejects-incomplete-source-before-confirmation");
        Assert.IsNull(deletion.State.Confirmation);
        Assert.AreEqual(0, fixture.Admission.MaintenanceAttempts);
        Assert.IsTrue(File.Exists(hiddenOvertureCache),
            "discarded-prefix-cache-is-never-treated-as-an-empty-delete-all");

        Assert.IsTrue(HasButton(
            rendered.Frames,
            "Delete All Overture Divisions",
            disabled: true));
        Assert.IsTrue(HasButton(
            rendered.Frames,
            "Delete All GADM Caches",
            disabled: false));
        StringAssert.Contains(rendered.Text,
            "Resolve the Overture cache directory issue and refresh before deleting all caches for this source.");

        await InvokeButtonAsync(fixture.Renderer, "Delete All GADM Caches");
        Assert.AreEqual(CacheDeletionPagePhase.Confirming, deletion.State.Phase);
        Assert.AreEqual(CacheMutationSource.Gadm, deletion.State.Confirmation?.Source);
        Assert.AreEqual(0, fixture.Admission.MaintenanceAttempts,
            "ready-peer-retains-the-existing-confirmation-before-command-admission");
    }

    [TestMethod]
    [TestCategory("Change53")]
    [DataRow("Overture", "Truncated")]
    [DataRow("Overture", "Unreadable")]
    [DataRow("Overture", "Unsafe")]
    [DataRow("Overture", null)]
    [DataRow("Gadm", "Truncated")]
    [DataRow("Gadm", "Unreadable")]
    [DataRow("Gadm", "Unsafe")]
    [DataRow("Gadm", null)]
    public async Task DeleteAllHandler_FailsClosedForEveryNonReadyOrMissingSource(
        string sourceName,
        string? sourceStatusName)
    {
        CacheMutationSource source = Enum.Parse<CacheMutationSource>(sourceName);
        CacheInventorySourceStatus? sourceStatus = sourceStatusName is null
            ? null
            : Enum.Parse<CacheInventorySourceStatus>(sourceStatusName);
        using var root = new TemporaryDirectory();
        string hiddenCache;
        string handler;
        if (source == CacheMutationSource.Overture)
        {
            CreateOvertureCache(root.Path, "hidden-release");
            hiddenCache = Path.Combine(root.Path, "overture-divisions", "CHE.db");
            handler = "DeleteAllOvertureDivisions";
        }
        else
        {
            CreateGadmCache(root.Path);
            hiddenCache = Path.Combine(root.Path, "gadm-divisions", "CHE.db");
            handler = "DeleteAllGadmDivisions";
        }

        var admission = new RecordingAdmissionGate();
        await using CacheInventoryService inventory = CreateInventory(root.Path);
        var page = new ImmichReverseGeo.Web.Components.Pages.GeoBoundaries();
        CacheDeletionPageController controller = CreateDeletionFactory(
            root.Path,
            admission,
            inventory).Create(
                () => Task.CompletedTask,
                () => Task.CompletedTask);
        SetPrivateField(page, "_deletionController", controller);
        SetPrivateField(
            page,
            "_sourceSnapshots",
            sourceStatus is null
                ? Array.Empty<CacheInventorySourceSnapshot>()
                : new[]
                {
                    new CacheInventorySourceSnapshot(
                        source,
                        sourceStatus.Value,
                        DiagnosticFor(sourceStatus.Value),
                        [])
                });

        await InvokePrivateTask(page, handler);

        Assert.AreEqual(CacheDeletionPagePhase.Idle, controller.State.Phase);
        Assert.IsNull(controller.State.Confirmation);
        Assert.AreEqual(0, admission.MaintenanceAttempts);
        Assert.IsTrue(File.Exists(hiddenCache));
        await page.DisposeAsync();
    }

    [TestMethod]
    [TestCategory("Change52")]
    public async Task RenderedPerCacheDelete_RoutesGadmIdentityThroughCommandAndReloadsFullSchemaStatus()
    {
        await using RenderedPageFixture fixture = await RenderedPageFixture.CreateAsync(
            includeOverture: false,
            includeGadm: true);
        string cachePath = Path.Combine(fixture.RootPath, "gadm-divisions", "CHE.db");

        await InvokeButtonAsync(fixture.Renderer, "Delete");
        WebStatusRenderingTests.RenderSnapshot confirmation = await fixture.Renderer.ReadAsync();
        StringAssert.Contains(confirmation.Text, "Delete the GADM cache for CHE?");
        Assert.AreEqual(0, fixture.Admission.MaintenanceAttempts);
        await InvokeButtonAsync(fixture.Renderer, "Keep caches");
        Assert.IsTrue(File.Exists(cachePath));
        Assert.AreEqual(0, fixture.Admission.MaintenanceAttempts);

        await InvokeButtonAsync(fixture.Renderer, "Delete");
        await InvokeButtonAsync(fixture.Renderer, "Confirm Delete");
        WebStatusRenderingTests.RenderSnapshot completed = await fixture.Renderer.ReadAsync();

        Assert.IsFalse(File.Exists(cachePath));
        Assert.AreEqual(1, fixture.Admission.MaintenanceAttempts);
        Assert.AreEqual(1, fixture.Admission.MaintenanceReservation!.DisposeCount);
        Assert.AreEqual(0, fixture.Worker.Starts);
        StringAssert.Contains(completed.Text, "Deleted the GADM cache for CHE.");
        StringAssert.Contains(
            completed.Text,
            "No cached administrative area data matches the current filters.");
        Assert.IsTrue(HasButton(
            completed.Frames,
            "Delete All GADM Caches",
            disabled: false));
        Assert.IsFalse(HasButton(completed.Frames, "Delete"));
        Assert.IsFalse(HasButton(completed.Frames, "Re-download"));
    }

    [TestMethod]
    [TestCategory("Change52")]
    public async Task RenderedDelete_KeepsControlsDisabledUntilReservationReleaseThenReloads()
    {
        var releaseReservation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using RenderedPageFixture fixture = await RenderedPageFixture.CreateAsync(
            includeOverture: true,
            includeGadm: false,
            maintenanceRelease: releaseReservation);
        string cachePath = Path.Combine(
            fixture.RootPath,
            "overture-divisions",
            "CHE.db");

        await InvokeButtonAsync(fixture.Renderer, "Delete");
        fixture.Click = InvokeButtonAsync(fixture.Renderer, "Confirm Delete");
        RecordingMaintenanceReservation reservation =
            await fixture.Admission.MaintenanceReservationAvailable.WaitAsync(Bound);
        await reservation.DisposeObserved.WaitAsync(Bound);
        WebStatusRenderingTests.RenderSnapshot releasing = await fixture.Renderer.ReadAsync();

        Assert.IsFalse(File.Exists(cachePath), "the final database is removed before release");
        Assert.IsFalse(fixture.Click.IsCompleted);
        AssertMutationControls(releasing.Frames, disabled: true);
        StringAssert.Contains(releasing.Text, "CHE");
        Assert.AreEqual(0, fixture.Worker.Starts);

        releaseReservation.TrySetResult();
        await fixture.Click.WaitAsync(Bound);
        fixture.Click = null;
        WebStatusRenderingTests.RenderSnapshot completed = await fixture.Renderer.ReadAsync();
        Assert.AreEqual(1, reservation.DisposeCount);
        Assert.IsTrue(HasButton(
            completed.Frames,
            "Delete All Overture Divisions",
            disabled: false));
        Assert.IsFalse(HasButton(completed.Frames, "Delete"));
        Assert.IsFalse(HasButton(completed.Frames, "Re-download"));
        StringAssert.Contains(completed.Text, "Deleted the Overture cache for CHE.");
        StringAssert.Contains(
            completed.Text,
            "No cached administrative area data matches the current filters.");
    }

    [TestMethod]
    [TestCategory("Change52")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task RenderedDelete_RejectedBusyOrUnavailableHasNoFileOrReloadSideEffect(
        bool busy)
    {
        CacheMaintenanceAdmissionResult rejection = busy
            ? new CacheMaintenanceAdmissionResult.Busy(
                new ExclusiveHeavyOwnerBusyMetadata.Worker(
                    new WorkerJobBusyMetadata(
                        WorkerJobCapabilityFamily.Processing,
                        WorkerJobRequestOrigin.Scheduled,
                        true)))
            : new CacheMaintenanceAdmissionResult.Unavailable(
                "worker-stopping",
                "Cache deletion is unavailable while the application is stopping.");
        await using RenderedPageFixture fixture = await RenderedPageFixture.CreateAsync(
            includeOverture: true,
            includeGadm: false,
            maintenanceRejection: rejection);
        string cachePath = Path.Combine(
            fixture.RootPath,
            "overture-divisions",
            "CHE.db");

        await InvokeButtonAsync(fixture.Renderer, "Delete");
        WebStatusRenderingTests.RenderSnapshot confirmation = await fixture.Renderer.ReadAsync();
        StringAssert.Contains(confirmation.Text, "Delete the Overture cache for CHE?");
        Assert.AreEqual(0, fixture.Admission.MaintenanceAttempts);
        await InvokeButtonAsync(fixture.Renderer, "Confirm Delete");
        WebStatusRenderingTests.RenderSnapshot rejected = await fixture.Renderer.ReadAsync();

        Assert.IsTrue(File.Exists(cachePath));
        Assert.AreEqual(1, fixture.Admission.MaintenanceAttempts);
        Assert.IsNull(fixture.Admission.MaintenanceReservation);
        Assert.AreEqual(0, fixture.Worker.Starts);
        AssertMutationControls(rejected.Frames, disabled: false);
        StringAssert.Contains(
            rejected.Text,
            busy
                ? "Cache deletion could not start because another operation is active."
                : "Cache deletion is unavailable while the application is stopping.");
        StringAssert.Contains(rejected.Text, "CHE");
        Assert.IsFalse(HasButton(rejected.Frames, "Cancel"));
    }

    [TestMethod]
    [TestCategory("Change52")]
    public async Task QueuedPageCallbacks_AfterDisposalDoNotRenderOrReloadStatus()
    {
        await using RenderedPageFixture fixture = await RenderedPageFixture.CreateAsync(
            includeOverture: true,
            includeGadm: false);
        string cachePath = Path.Combine(
            fixture.RootPath,
            "overture-divisions",
            "CHE.db");
        int initialRenderCount = fixture.Renderer.RenderCount;
        using var dispatcherEntered = new ManualResetEventSlim();
        using var releaseDispatcher = new ManualResetEventSlim();
        Task? dispatcherBlock = null;
        Task? queuedRender = null;
        Task? queuedReload = null;
        Task? disposal = null;
        try
        {
            dispatcherBlock = Task.Run(() =>
                fixture.Renderer.Dispatcher.InvokeAsync(() =>
                {
                    dispatcherEntered.Set();
                    if (!releaseDispatcher.Wait(Bound))
                    {
                        throw new TimeoutException("The dispatcher test hold was not released.");
                    }
                }));
            Assert.IsTrue(
                dispatcherEntered.Wait(Bound),
                "the renderer dispatcher did not enter the non-yielding hold");

            queuedRender = InvokePrivateTask(fixture.Page, "NotifyPageChangedAsync");
            queuedReload = InvokePrivateTask(fixture.Page, "ReloadPageStateAsync");
            Assert.IsFalse(queuedRender.IsCompleted);
            Assert.IsFalse(queuedReload.IsCompleted);
            File.Delete(cachePath);
            disposal = fixture.Page.DisposeAsync().AsTask();
            await disposal.WaitAsync(Bound);
            Assert.IsFalse(queuedRender.IsCompleted);
            Assert.IsFalse(queuedReload.IsCompleted);

            releaseDispatcher.Set();
            await Task.WhenAll(dispatcherBlock, queuedRender, queuedReload).WaitAsync(Bound);

            Assert.AreEqual(initialRenderCount, fixture.Renderer.RenderCount);
            IReadOnlyList<CacheInventorySourceSnapshot> status =
                GetPrivateField<IReadOnlyList<CacheInventorySourceSnapshot>>(
                    fixture.Page,
                    "_sourceSnapshots");
            Assert.IsTrue(status
                .Single(source => source.Source == CacheMutationSource.Overture)
                .Entries.Any(entry => entry.Iso3 == "CHE"));
            Assert.IsFalse(File.Exists(cachePath));
        }
        finally
        {
            releaseDispatcher.Set();
            await DrainCleanupAsync(
                () => dispatcherBlock?.WaitAsync(Bound) ?? Task.CompletedTask,
                () => queuedRender?.WaitAsync(Bound) ?? Task.CompletedTask,
                () => queuedReload?.WaitAsync(Bound) ?? Task.CompletedTask,
                () => disposal?.WaitAsync(Bound)
                    ?? fixture.Page.DisposeAsync().AsTask().WaitAsync(Bound));
        }
    }

    [TestMethod]
    [TestCategory("Change52")]
    public async Task RenderedDeleteAll_ShutdownAndPageDisposalAwaitRealMaintenanceOwnerWithoutStaleReload()
    {
        using var root = new TemporaryDirectory();
        CreateOvertureCache(root.Path, "release-che", "CHE");
        CreateOvertureCache(root.Path, "release-deu", "DEU");
        string chePath = Path.Combine(root.Path, "overture-divisions", "CHE.db");
        string deuPath = Path.Combine(root.Path, "overture-divisions", "DEU.db");
        var overture = new OvertureDivisionCacheService(
            NullLogger<OvertureDivisionCacheService>.Instance,
            root.Path,
            static iso3 => iso3 switch
            {
                "CHE" => "CH",
                "DEU" => "DE",
                _ => null
            });
        var gadm = new GadmDivisionCacheService(
            NullLogger<GadmDivisionCacheService>.Instance,
            root.Path);
        await using var coordinator = new WorkerJobCoordinator(WorkerJobDescriptors.Registered);
        var fileSystem = new OrderedBarrierDeletionFileSystem();
        string bundledData = Path.Combine(AppContext.BaseDirectory, "data");
        var command = new CacheDeletionCommand(
            coordinator,
            fileSystem,
            new StorageOptions(root.Path, bundledData),
            CountryCodeService.CreateForTest(bundledData),
            NullLogger<CacheDeletionCommand>.Instance);
        var commandReturned = new TaskCompletionSource<CacheDeletionOperationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<CacheDeletionOperationResult> ObserveDeleteAllAsync(
            CacheMutationSource source,
            IEnumerable<CacheDeletionTarget> targets)
        {
            CacheDeletionOperationResult result = await command.DeleteAllAsync(source, targets);
            commandReturned.TrySetResult(result);
            return result;
        }

        var inventory = CreateInventory(root.Path);
        var deletionFactory = new CacheInventoryDeletionPageControllerFactory(
            new CacheInventoryDeletionOperations(command, inventory));
        var worker = new RecordingWorkerClient();
        var workerLifetime = new CacheMutationPageControllerHostLifetime();
        var mutationFactory = new CacheMutationPageControllerFactory(
            coordinator,
            worker,
            workerLifetime);
        var page = new ImmichReverseGeo.Web.Components.Pages.GeoBoundaries();
        SetInjected(page, "CacheInventory", inventory);
        SetInjected(page, "CacheMutations", mutationFactory);
        SetInjected(page, "CacheDeletions", deletionFactory);
        await using var renderer = new WebStatusRenderingTests.ComponentRenderer();
        await renderer.AttachAsync(page);
        CacheDeletionPageController initializedController =
            GetPrivateField<CacheDeletionPageController>(page, "_deletionController");
        await initializedController.DisposeAsync();
        var observedController = new CacheDeletionPageController(
            command.DeleteAsync,
            ObserveDeleteAllAsync,
            () => InvokePrivateTask(page, "NotifyPageChangedAsync"),
            () => InvokePrivateTask(page, "ReloadPageStateAsync"));
        SetPrivateField(page, "_deletionController", observedController);

        var ownerLedger = new List<ExclusiveHeavyOwnerSnapshot?>();
        var ownerLedgerGate = new object();
        coordinator.Changed += () =>
        {
            lock (ownerLedgerGate)
            {
                ownerLedger.Add(coordinator.ActiveOwner);
            }
        };
        Task? click = null;
        Task? disposal = null;
        Task? shutdown = null;
        try
        {
            await InvokeButtonAsync(renderer, "Delete All Overture Divisions");
            click = InvokeButtonAsync(renderer, "Confirm Delete All");
            await fileSystem.FirstDeleted.WaitAsync(Bound);
            await fileSystem.SecondEntered.WaitAsync(Bound);

            ExclusiveHeavyOwnerSnapshot.CacheMaintenance admittedOwner =
                Assert.IsInstanceOfType<ExclusiveHeavyOwnerSnapshot.CacheMaintenance>(
                    coordinator.ActiveOwner);
            Assert.IsFalse(File.Exists(chePath));
            Assert.IsTrue(File.Exists(deuPath));
            Assert.IsFalse(click.IsCompleted);

            shutdown = coordinator.BeginShutdown();
            disposal = page.DisposeAsync().AsTask();
            await renderer.ReadAsync();
            int renderCountAtDisposal = renderer.RenderCount;

            WorkerJobAdmissionResult workerAdmission = coordinator.TryAdmit(
                new ProcessAssetsWorkerJobDispatch(
                    new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual)));
            WorkerJobAdmissionResult.Unavailable unavailable =
                Assert.IsInstanceOfType<WorkerJobAdmissionResult.Unavailable>(workerAdmission);
            Assert.AreEqual("worker-admission-stopped", unavailable.Code);
            Assert.AreEqual(admittedOwner, coordinator.ActiveOwner);
            Assert.AreEqual(0, worker.Starts);
            Assert.IsFalse(shutdown.IsCompleted);
            Assert.IsFalse(disposal.IsCompleted);
            Assert.IsFalse(click.IsCompleted);
            Assert.IsFalse(commandReturned.Task.IsCompleted);

            fileSystem.ReleaseSecond();
            CacheDeletionOperationResult finalized =
                await commandReturned.Task.WaitAsync(Bound);
            Assert.AreEqual(CacheDeletionOperationDisposition.Completed, finalized.Disposition);
            Assert.AreEqual(2, finalized.DeletedCount);
            Assert.AreEqual(0, finalized.MissingCount);
            Assert.AreEqual(0, finalized.InvalidCount);
            Assert.AreEqual(0, finalized.FailedCount);
            CollectionAssert.AreEqual(
                new[] { "CHE", "DEU" },
                finalized.Targets.Select(target => target.Iso3).ToArray());
            Assert.IsTrue(finalized.Targets.All(target =>
                target.Disposition == CacheDeletionTargetDisposition.Deleted));

            await Task.WhenAll(click, disposal, shutdown).WaitAsync(Bound);
            Assert.IsFalse(File.Exists(chePath));
            Assert.IsFalse(File.Exists(deuPath));
            Assert.IsNull(coordinator.ActiveOwner);
            IReadOnlyList<CacheInventorySourceSnapshot> pageStatus =
                GetPrivateField<IReadOnlyList<CacheInventorySourceSnapshot>>(
                    page,
                    "_sourceSnapshots");
            CacheInventorySourceSnapshot overturePageStatus = pageStatus
                .Single(source => source.Source == CacheMutationSource.Overture);
            Assert.IsTrue(overturePageStatus.Entries.Any(entry => entry.Iso3 == "CHE"));
            Assert.IsTrue(overturePageStatus.Entries.Any(entry => entry.Iso3 == "DEU"));
            WebStatusRenderingTests.RenderSnapshot stalePage = await renderer.ReadAsync();
            StringAssert.Contains(stalePage.Text, "CHE");
            StringAssert.Contains(stalePage.Text, "DEU");
            Assert.IsFalse(stalePage.Text.Contains(
                "cache deletion completed",
                StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual(renderCountAtDisposal, renderer.RenderCount);

            ExclusiveHeavyOwnerSnapshot?[] ownerEvents;
            lock (ownerLedgerGate)
            {
                ownerEvents = ownerLedger.ToArray();
            }

            Assert.IsGreaterThanOrEqualTo(3, ownerEvents.Length);
            Assert.AreEqual(1, ownerEvents.Count(owner => owner is null));
            Assert.IsTrue(ownerEvents
                .Where(owner => owner is ExclusiveHeavyOwnerSnapshot.CacheMaintenance)
                .All(owner => Equals(owner, admittedOwner)));
        }
        finally
        {
            fileSystem.ReleaseSecond();
            await DrainCleanupAsync(
                () =>
                {
                    worker.Session?.Complete(new CacheMutationWorkerOutcome.Cancelled());
                    return Task.CompletedTask;
                },
                () => click?.WaitAsync(Bound) ?? Task.CompletedTask,
                () => disposal?.WaitAsync(Bound)
                    ?? page.DisposeAsync().AsTask().WaitAsync(Bound),
                () => shutdown?.WaitAsync(Bound) ?? coordinator.BeginShutdown().WaitAsync(Bound));
        }
    }

    private static void AssertGadmNoticeAndSeparateStatus(
        WebStatusRenderingTests.RenderSnapshot snapshot,
        string? expectedStatus,
        string? expectedError,
        string? expectedAlertClass)
    {
        const string noticeText =
            "GADM data is available for academic and other non-commercial use.";
        RenderedElement? notice = Elements(snapshot.Frames).SingleOrDefault(element =>
            element.Name == "p"
            && HasNestedLink(
                snapshot.Frames,
                element,
                CacheMutationGadmAttribution.OfficialLicenseUrl));
        string renderedNotice;
        if (notice is not null)
        {
            Assert.IsTrue(HasCssClass(snapshot.Frames, notice, "section-intro"));
            renderedNotice = NormalizeText(ElementText(snapshot.Frames, notice));
        }
        else
        {
            string markup = snapshot.Frames
                .Where(frame => frame.FrameType == RenderTreeFrameType.Markup)
                .Select(frame => frame.MarkupContent)
                .Single(value => value.Contains(noticeText, StringComparison.Ordinal));
            string paragraph = ContainingMarkupElement(markup, "p", noticeText);
            StringAssert.Contains(paragraph, "class=\"section-intro\"");
            AssertMarkupAnchorHref(
                paragraph,
                CacheMutationGadmAttribution.OfficialLicenseUrl);
            renderedNotice = NormalizeText(paragraph);
        }

        StringAssert.Contains(renderedNotice, noticeText);
        StringAssert.Contains(renderedNotice, "Review the GADM license");

        if (expectedStatus is null)
        {
            return;
        }

        RenderedElement status = Elements(snapshot.Frames).Single(element =>
            element.Name == "div"
            && HasCssClass(snapshot.Frames, element, "alert")
            && ElementText(snapshot.Frames, element)
                .Contains(expectedStatus, StringComparison.Ordinal));
        Assert.IsNotNull(expectedAlertClass);
        Assert.IsTrue(
            HasCssClass(snapshot.Frames, status, expectedAlertClass),
            $"Expected mutation status to use {expectedAlertClass}.");
        string renderedStatus = NormalizeText(ElementText(snapshot.Frames, status));
        StringAssert.Contains(renderedStatus, expectedStatus);
        Assert.IsFalse(renderedStatus.Contains(noticeText, StringComparison.Ordinal));
        Assert.IsFalse(HasNestedLink(
            snapshot.Frames,
            status,
            CacheMutationGadmAttribution.OfficialLicenseUrl));
        if (expectedError is not null)
        {
            StringAssert.Contains(renderedStatus, expectedError);
            Assert.IsFalse(renderedNotice.Contains(expectedError, StringComparison.Ordinal));
        }
    }

    private static void AssertMutationControls(RenderTreeFrame[] frames, bool disabled)
    {
        foreach (string label in new[]
        {
            "Delete All Overture Divisions",
            "Delete All GADM Caches",
            "Delete",
            "Re-download"
        })
        {
            Assert.IsTrue(
                HasButton(frames, label, disabled),
                $"Expected rendered '{label}' disabled={disabled}.");
        }
    }

    private static bool HasButton(
        RenderTreeFrame[] frames,
        string label,
        bool? disabled = null) =>
        Elements(frames).Any(element =>
            element.Name == "button"
            && string.Equals(ElementText(frames, element).Trim(), label, StringComparison.Ordinal)
            && (disabled is null || IsDisabled(frames, element) == disabled.Value));

    private static bool IsDisabled(RenderTreeFrame[] frames, RenderedElement element)
    {
        foreach (RenderTreeFrame attribute in Attributes(frames, element))
        {
            if (string.Equals(attribute.AttributeName, "disabled", StringComparison.Ordinal))
            {
                return attribute.AttributeValue is not bool value || value;
            }
        }

        return false;
    }

    private static bool HasCssClass(
        RenderTreeFrame[] frames,
        RenderedElement element,
        string expected) =>
        Attributes(frames, element).Any(frame =>
            string.Equals(frame.AttributeName, "class", StringComparison.Ordinal)
            && (frame.AttributeValue?.ToString() ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains(expected, StringComparer.Ordinal));

    private static bool HasNestedLink(
        RenderTreeFrame[] frames,
        RenderedElement parent,
        string href) =>
        Elements(frames)
            .Where(element => element.Name == "a"
                && element.Index > parent.Index
                && element.Index < parent.Index + parent.Length)
            .Any(element => Attributes(frames, element).Any(frame =>
                string.Equals(frame.AttributeName, "href", StringComparison.Ordinal)
                && string.Equals(frame.AttributeValue?.ToString(), href, StringComparison.Ordinal)));

    private static string ElementText(
        RenderTreeFrame[] frames,
        RenderedElement element) =>
        string.Concat(frames
            .Skip(element.Index)
            .Take(element.Length)
            .Where(frame => frame.FrameType is RenderTreeFrameType.Text or RenderTreeFrameType.Markup)
            .Select(frame => frame.FrameType == RenderTreeFrameType.Text
                ? frame.TextContent
                : frame.MarkupContent));

    private static string NormalizeText(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string ContainingMarkupElement(
        string markup,
        string elementName,
        string containedText)
    {
        int textIndex = markup.IndexOf(containedText, StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, textIndex);
        int start = markup.LastIndexOf($"<{elementName}", textIndex, StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, start);
        int end = markup.IndexOf($"</{elementName}>", textIndex, StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, end);
        return markup[start..(end + elementName.Length + 3)];
    }

    private static void AssertMarkupAnchorHref(string markup, string href)
    {
        int anchor = markup.IndexOf("<a ", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, anchor);
        int tagEnd = markup.IndexOf('>', anchor);
        Assert.IsGreaterThan(anchor, tagEnd);
        string openingTag = markup[anchor..(tagEnd + 1)];
        StringAssert.Contains(openingTag, $"href=\"{href}\"");
    }

    private static IEnumerable<RenderTreeFrame> Attributes(
        RenderTreeFrame[] frames,
        RenderedElement element) =>
        frames.Skip(element.Index + 1)
            .Take(element.Length - 1)
            .TakeWhile(frame => frame.FrameType == RenderTreeFrameType.Attribute);

    private static IEnumerable<RenderedElement> Elements(RenderTreeFrame[] frames)
    {
        for (var index = 0; index < frames.Length; index++)
        {
            RenderTreeFrame frame = frames[index];
            if (frame.FrameType == RenderTreeFrameType.Element)
            {
                yield return new RenderedElement(index, frame.ElementName, frame.ElementSubtreeLength);
            }
        }
    }

    private static async Task InvokeButtonAsync(
        WebStatusRenderingTests.ComponentRenderer renderer,
        string label)
    {
        WebStatusRenderingTests.RenderSnapshot snapshot = await renderer.ReadAsync();
        object? callback = null;
        for (var textIndex = 0; textIndex < snapshot.Frames.Length; textIndex++)
        {
            RenderTreeFrame textFrame = snapshot.Frames[textIndex];
            string? content = textFrame.FrameType switch
            {
                RenderTreeFrameType.Text => textFrame.TextContent,
                RenderTreeFrameType.Markup => textFrame.MarkupContent,
                _ => null
            };
            if (!string.Equals(content?.Trim(), label, StringComparison.Ordinal))
            {
                continue;
            }

            for (var elementIndex = textIndex - 1; elementIndex >= 0; elementIndex--)
            {
                RenderTreeFrame element = snapshot.Frames[elementIndex];
                if (element.FrameType != RenderTreeFrameType.Element
                    || !string.Equals(element.ElementName, "button", StringComparison.Ordinal)
                    || elementIndex + element.ElementSubtreeLength <= textIndex)
                {
                    continue;
                }

                callback = snapshot.Frames
                    .Skip(elementIndex + 1)
                    .Take(textIndex - elementIndex - 1)
                    .First(item => item.FrameType == RenderTreeFrameType.Attribute
                        && string.Equals(item.AttributeName, "onclick", StringComparison.Ordinal))
                    .AttributeValue;
                break;
            }

            break;
        }

        Assert.IsNotNull(
            callback,
            $"Rendered button '{label}' was not found. Text: {snapshot.Text}");
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            if (callback is Func<Task> asynchronous)
            {
                await asynchronous();
                return;
            }

            if (callback is Action synchronous)
            {
                synchronous();
                return;
            }

            MethodInfo invokeAsync = callback.GetType().GetMethods()
                .Single(method => method.Name == "InvokeAsync" && method.GetParameters().Length == 1);
            ParameterInfo parameter = invokeAsync.GetParameters()[0];
            object? argument = parameter.ParameterType == typeof(object)
                ? new MouseEventArgs()
                : Activator.CreateInstance(parameter.ParameterType);
            await Assert.IsInstanceOfType<Task>(invokeAsync.Invoke(callback, [argument]));
        });
    }

    private static void CreateOvertureCache(
        string root,
        string release,
        string iso3 = "CHE")
    {
        string directory = Path.Combine(root, "overture-divisions");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"{iso3}.db");
        File.Delete(path);
        using var connection = new SqliteConnection($"Data Source={path};Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE division_area (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                subtype TEXT NULL,
                class_name TEXT NULL,
                admin_level INTEGER NULL,
                country TEXT NULL,
                is_land INTEGER NOT NULL,
                is_territorial INTEGER NOT NULL,
                geom_wkb BLOB NULL,
                bbox_xmin REAL NULL,
                bbox_ymin REAL NULL,
                bbox_xmax REAL NULL,
                bbox_ymax REAL NULL
            );
            CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO division_area
                (id, name, subtype, class_name, admin_level, country, is_land, is_territorial,
                 geom_wkb, bbox_xmin, bbox_ymin, bbox_xmax, bbox_ymax)
            VALUES ('fixture', 'Region', 'region', 'land', 4, 'CH', 1, 0,
                    $geometry, 7, 46, 9, 48);
            INSERT INTO _meta VALUES ('downloadedAt', '2026-09-09T08:00:00Z');
            INSERT INTO _meta VALUES ('release', $release);
            """;
        command.Parameters.AddWithValue("$geometry", ValidPolygonWkb());
        command.Parameters.AddWithValue("$release", release);
        command.ExecuteNonQuery();
    }

    private static CacheInventoryDeletionPageControllerFactory CreateDeletionFactory(
        string root,
        IWorkerJobAdmissionGate admission,
        ICacheInventoryInvalidator invalidator)
    {
        string bundledData = Path.Combine(AppContext.BaseDirectory, "data");
        var command = new CacheDeletionCommand(
            admission,
            new PhysicalCacheDeletionFileSystem(),
            new StorageOptions(root, bundledData),
            CountryCodeService.CreateForTest(bundledData),
            NullLogger<CacheDeletionCommand>.Instance);
        return new CacheInventoryDeletionPageControllerFactory(
            new CacheInventoryDeletionOperations(command, invalidator));
    }

    private static CacheInventoryService CreateInventory(
        string root,
        CacheInventoryOptions? options = null) =>
        new(new CacheInventoryStorageScanner(
            new StorageOptions(root, Path.Combine(AppContext.BaseDirectory, "data")),
            new PhysicalCacheInventoryFileSystem(),
            new CacheInventorySqliteMetadataReader(),
            options ?? new CacheInventoryOptions(),
            TimeProvider.System));

    private static CacheInventoryDiagnosticCode DiagnosticFor(
        CacheInventorySourceStatus status) => status switch
        {
            CacheInventorySourceStatus.Truncated =>
                CacheInventoryDiagnosticCode.EnumerationTruncated,
            CacheInventorySourceStatus.Unreadable =>
                CacheInventoryDiagnosticCode.SourceUnreadable,
            CacheInventorySourceStatus.Unsafe =>
                CacheInventoryDiagnosticCode.SourceUnsafe,
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };

    private static byte[] ValidPolygonWkb()
    {
        var factory = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
        var polygon = factory.CreatePolygon(
        [
            new Coordinate(7, 46),
            new Coordinate(9, 46),
            new Coordinate(9, 48),
            new Coordinate(7, 48),
            new Coordinate(7, 46)
        ]);
        return new WKBWriter().Write(polygon);
    }

    private static void CreateGadmCache(string root)
    {
        string directory = Path.Combine(root, "gadm-divisions");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "CHE.db");
        using var connection = new SqliteConnection($"Data Source={path};Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE gadm_area (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                english_type TEXT NULL,
                local_type TEXT NULL,
                admin_level INTEGER NOT NULL,
                geom_wkb BLOB NOT NULL,
                bbox_xmin REAL NOT NULL,
                bbox_ymin REAL NOT NULL,
                bbox_xmax REAL NOT NULL,
                bbox_ymax REAL NOT NULL
            );
            CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO gadm_area VALUES (
                'fixture', 'Region', 'Region', NULL, 1, $geometry, 7, 46, 9, 48);
            INSERT INTO _meta VALUES ('downloadedAt', '2026-09-09T08:00:00Z');
            INSERT INTO _meta VALUES ('version', '4.1');
            """;
        command.Parameters.AddWithValue("$geometry", ValidPolygonWkb());
        command.ExecuteNonQuery();
    }

    private static CacheMutationResult Result(string release)
    {
        DateTimeOffset now = new(2026, 9, 9, 8, 0, 0, TimeSpan.Zero);
        return new CacheMutationResult(
            now,
            now.AddSeconds(1),
            new CacheMutationSourceResult(
                CacheMutationSource.Overture,
                CacheMutationOperation.Refresh,
                "CHE",
                CacheMutationDisposition.Published,
                1,
                now,
                4096,
                release,
                null));
    }

    private static CacheMutationResult GadmResult()
    {
        DateTimeOffset now = new(2026, 9, 9, 8, 0, 0, TimeSpan.Zero);
        var attribution = new CacheMutationGadmAttribution(
            CacheMutationGadmAttribution.OfficialDatasetName,
            "4.1",
            CacheMutationGadmAttribution.OfficialLicenseUrl,
            CacheMutationGadmAttribution.NonCommercialUseNotice);
        return new CacheMutationResult(
            now,
            now.AddSeconds(1),
            new CacheMutationSourceResult(
                CacheMutationSource.Gadm,
                CacheMutationOperation.Refresh,
                "CHE",
                CacheMutationDisposition.Published,
                1,
                now,
                4096,
                "4.1",
                attribution));
    }

    private static WorkerJobOutputMessage Started(Guid jobId) =>
        new(
            WorkerJobProtocolV2.LifecycleCategory,
            WorkerJobProtocolV2.JobStartedType,
            1,
            new DateTimeOffset(2026, 9, 9, 8, 0, 0, TimeSpan.Zero),
            jobId,
            WorkerJobKind.CacheMutation,
            new WorkerJobStartedPayload(
                "manual",
                new DateTimeOffset(2026, 9, 9, 8, 0, 0, TimeSpan.Zero)));

    private static WorkerJobOutputMessage Terminal(CacheMutationResult result, Guid jobId) =>
        new(
            WorkerJobProtocolV2.TerminalCategory,
            WorkerJobProtocolV2.TerminalType,
            2,
            result.EndedAtUtc,
            jobId,
            WorkerJobKind.CacheMutation,
            new WorkerJobTerminalPayload(
                WorkerJobTerminalOutcome.Completed,
                result.StartedAtUtc,
                result.EndedAtUtc,
                null,
                null,
                result,
                null));

    private static async Task DrainCleanupAsync(params Func<Task>[] operations)
    {
        List<Exception>? failures = null;
        foreach (Func<Task> operation in operations)
        {
            try
            {
                await operation();
            }
            catch (Exception exception)
            {
                failures ??= [];
                failures.Add(exception);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException(failures);
        }
    }

    private static void SetInjected(object page, string name, object value)
    {
        page.GetType()
            .GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(page, value);
    }

    private static void SetPrivateField(object instance, string name, object value)
    {
        instance.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(instance, value);
    }

    private static T GetPrivateField<T>(object instance, string name)
    {
        return Assert.IsInstanceOfType<T>(instance.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(instance));
    }

    private static Task InvokePrivateTask(object instance, string name)
    {
        return Assert.IsInstanceOfType<Task>(instance.GetType()
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(instance, null));
    }

    private sealed class RecordingAdmissionGate : IWorkerJobAdmissionGate
    {
        private readonly TaskCompletionSource<RecordingMaintenanceReservation>
            _maintenanceReservationAvailable = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
        internal WorkerJobDispatch? Dispatch { get; private set; }
        internal RecordingLease? Lease { get; private set; }
        internal WorkerJobAdmissionResult? Rejection { get; init; }
        internal CacheMaintenanceAdmissionResult? MaintenanceRejection { get; init; }
        internal TaskCompletionSource? MaintenanceRelease { get; init; }
        internal int MaintenanceAttempts { get; private set; }
        internal CacheMaintenanceRequestOrigin? MaintenanceOrigin { get; private set; }
        internal RecordingMaintenanceReservation? MaintenanceReservation { get; private set; }
        internal Task<RecordingMaintenanceReservation> MaintenanceReservationAvailable =>
            _maintenanceReservationAvailable.Task;

        public WorkerJobAdmissionResult TryAdmit(WorkerJobDispatch dispatch)
        {
            Dispatch = dispatch;
            if (Rejection is not null)
            {
                return Rejection;
            }

            Lease = new RecordingLease(dispatch);
            return new WorkerJobAdmissionResult.Admitted(Lease);
        }

        public CacheMaintenanceAdmissionResult TryReserveCacheMaintenance(
            CacheMaintenanceRequestOrigin origin)
        {
            MaintenanceAttempts++;
            MaintenanceOrigin = origin;
            if (MaintenanceRejection is not null)
            {
                return MaintenanceRejection;
            }

            MaintenanceReservation = new RecordingMaintenanceReservation(MaintenanceRelease);
            _maintenanceReservationAvailable.TrySetResult(MaintenanceReservation);
            return new CacheMaintenanceAdmissionResult.Reserved(MaintenanceReservation);
        }
    }

    private sealed class RecordingMaintenanceReservation(TaskCompletionSource? release)
        : ICacheMaintenanceReservation
    {
        private readonly TaskCompletionSource _disposeObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int DisposeCount { get; private set; }
        internal Task DisposeObserved => _disposeObserved.Task;

        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            _disposeObserved.TrySetResult();
            if (release is not null)
            {
                await release.Task;
            }
        }
    }

    private sealed class OrderedBarrierDeletionFileSystem : ICacheDeletionFileSystem
    {
        private readonly PhysicalCacheDeletionFileSystem _inner = new();
        private readonly TaskCompletionSource _firstDeleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseSecond =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task FirstDeleted => _firstDeleted.Task;
        internal Task SecondEntered => _secondEntered.Task;

        public ValueTask<CacheDeletionFileInspection> InspectAsync(
            string sourceRoot,
            string finalPath) => _inner.InspectAsync(sourceRoot, finalPath);

        public async ValueTask DeleteAsync(string finalPath)
        {
            string iso3 = Path.GetFileNameWithoutExtension(finalPath);
            if (string.Equals(iso3, "DEU", StringComparison.Ordinal))
            {
                _secondEntered.TrySetResult();
                await _releaseSecond.Task;
            }

            await _inner.DeleteAsync(finalPath);
            if (string.Equals(iso3, "CHE", StringComparison.Ordinal))
            {
                _firstDeleted.TrySetResult();
            }
        }

        internal void ReleaseSecond() => _releaseSecond.TrySetResult();
    }

    private sealed class RecordingLease(WorkerJobDispatch dispatch) : IWorkerJobAdmissionLease
    {
        public WorkerJobContext Context => dispatch.Context;
        public WorkerJobDescriptor Descriptor => dispatch.Descriptor;
        public bool IsStopRequested { get; private set; }
        internal int DisposeCount { get; private set; }

        public bool TryBindOwnerStop(WorkerJobContext context, Func<Task> requestStopAsync) =>
            ReferenceEquals(Context, context);

        public bool TryAdvance(
            WorkerJobContext context,
            WorkerJobLifecycle lifecycle,
            int? childProcessId = null)
        {
            IsStopRequested |= lifecycle == WorkerJobLifecycle.Stopping;
            return ReferenceEquals(Context, context);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingWorkerClient : ICacheMutationWorkerClient
    {
        private readonly Channel<FakeSession> _sessions = Channel.CreateUnbounded<FakeSession>();
        private readonly TaskCompletionSource _startObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<FakeSession> _sessionAvailable =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CacheMutationRequest? Request { get; private set; }
        internal FakeSession? Session { get; private set; }
        internal TaskCompletionSource? StartRelease { get; init; }
        internal Task StartObserved => _startObserved.Task;
        internal Task<FakeSession> SessionAvailable => _sessionAvailable.Task;
        internal int Starts { get; private set; }

        public async ValueTask<CacheMutationWorkerStartResult> StartAsync(
            IWorkerJobAdmissionLease admission,
            CacheMutationRequest request,
            IWorkerJobEventSink eventSink,
            CancellationToken cancellationToken)
        {
            Starts++;
            Request = request;
            _startObserved.TrySetResult();
            if (StartRelease is not null)
            {
                await StartRelease.Task.WaitAsync(cancellationToken);
            }

            var session = new FakeSession(admission.Context.JobId, eventSink);
            Session = session;
            _sessionAvailable.TrySetResult(session);
            _sessions.Writer.TryWrite(session);
            return new CacheMutationWorkerStartResult.Started(session);
        }

        internal async Task<FakeSession> WaitForSessionAsync() =>
            await _sessions.Reader.ReadAsync().AsTask().WaitAsync(Bound);
    }

    private sealed class FakeSession(Guid jobId, IWorkerJobEventSink eventSink)
        : ICacheMutationWorkerSession
    {
        private readonly TaskCompletionSource<CacheMutationWorkerOutcome> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completionObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Guid JobId { get; } = jobId;
        public WorkerJobKind JobKind => WorkerJobKind.CacheMutation;
        public InternalWorkerProtocolVersion ProtocolVersion => InternalWorkerProtocolVersion.V2;
        public bool IsCancellable => true;
        public Task<CacheMutationWorkerOutcome> Completion
        {
            get
            {
                _completionObserved.TrySetResult();
                return _completion.Task;
            }
        }

        internal Task CompletionObserved => _completionObserved.Task;
        public Task RequestStopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        internal ValueTask EmitAsync(WorkerJobOutputMessage message) =>
            eventSink.AcceptAsync(message, CancellationToken.None);
        internal void Complete(CacheMutationWorkerOutcome outcome) => _completion.TrySetResult(outcome);
    }

    private sealed class RenderedPageFixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory _root;

        private RenderedPageFixture(
            TemporaryDirectory root,
            RecordingAdmissionGate admission,
            RecordingWorkerClient worker,
            ImmichReverseGeo.Web.Components.Pages.GeoBoundaries page,
            WebStatusRenderingTests.ComponentRenderer renderer)
        {
            _root = root;
            Admission = admission;
            Worker = worker;
            Page = page;
            Renderer = renderer;
        }

        internal RecordingAdmissionGate Admission { get; }
        internal RecordingWorkerClient Worker { get; }
        internal ImmichReverseGeo.Web.Components.Pages.GeoBoundaries Page { get; }
        internal WebStatusRenderingTests.ComponentRenderer Renderer { get; }
        internal string RootPath => _root.Path;
        internal Task? Click { get; set; }

        internal static async Task<RenderedPageFixture> CreateAsync(
            bool includeOverture,
            bool includeGadm,
            WorkerJobAdmissionResult? rejection = null,
            TaskCompletionSource? startRelease = null,
            CacheMaintenanceAdmissionResult? maintenanceRejection = null,
            TaskCompletionSource? maintenanceRelease = null,
            CacheInventoryOptions? inventoryOptions = null,
            int overtureJunkEntries = 0,
            TimeProvider? time = null,
            WorkerEventDeliveryPolicy? policy = null)
        {
            var root = new TemporaryDirectory();
            if (includeOverture)
            {
                CreateOvertureCache(root.Path, "old-release");
                for (var index = 0; index < overtureJunkEntries; index++)
                {
                    File.WriteAllText(
                        Path.Combine(root.Path, "overture-divisions", $"junk-{index}.txt"),
                        "junk");
                }
            }

            if (includeGadm)
            {
                CreateGadmCache(root.Path);
            }

            var overture = new OvertureDivisionCacheService(
                NullLogger<OvertureDivisionCacheService>.Instance,
                root.Path,
                static iso3 => iso3 == "CHE" ? "CH" : null);
            var gadm = new GadmDivisionCacheService(
                NullLogger<GadmDivisionCacheService>.Instance,
                root.Path);
            var admission = new RecordingAdmissionGate
            {
                Rejection = rejection,
                MaintenanceRejection = maintenanceRejection,
                MaintenanceRelease = maintenanceRelease
            };
            var worker = new RecordingWorkerClient { StartRelease = startRelease };
            var lifetime = new CacheMutationPageControllerHostLifetime();
            var factory = new CacheMutationPageControllerFactory(admission, worker, lifetime, time: time, policy: policy);
            var inventory = CreateInventory(root.Path, inventoryOptions);
            var page = new ImmichReverseGeo.Web.Components.Pages.GeoBoundaries();
            SetInjected(page, "CacheInventory", inventory);
            SetInjected(page, "CacheMutations", factory);
            SetInjected(page, "CacheDeletions", CreateDeletionFactory(root.Path, admission, inventory));
            var renderer = new WebStatusRenderingTests.ComponentRenderer();
            await renderer.AttachAsync(page);
            return new RenderedPageFixture(root, admission, worker, page, renderer);
        }

        public async ValueTask DisposeAsync()
        {
            Worker.StartRelease?.TrySetResult();
            Admission.MaintenanceRelease?.TrySetResult();
            await DrainCleanupAsync(
                async () =>
                {
                    if (Click is not null
                        && !Click.IsCompleted
                        && Admission.Dispatch is not null
                        && Admission.Rejection is null)
                    {
                        await Worker.StartObserved.WaitAsync(Bound);
                        FakeSession session = await Worker.SessionAvailable.WaitAsync(Bound);
                        session.Complete(new CacheMutationWorkerOutcome.Cancelled());
                    }
                    else
                    {
                        Worker.Session?.Complete(new CacheMutationWorkerOutcome.Cancelled());
                    }
                },
                () => Page.DisposeAsync().AsTask().WaitAsync(Bound),
                () => Click?.WaitAsync(Bound) ?? Task.CompletedTask,
                () => Renderer.DisposeAsync().AsTask().WaitAsync(Bound),
                () =>
                {
                    _root.Dispose();
                    return Task.CompletedTask;
                });
        }
    }

    private sealed record RenderedElement(int Index, string Name, int Length);

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            string temporaryRoot = OperatingSystem.IsMacOS()
                ? "/private/tmp"
                : System.IO.Path.GetTempPath();
            Path = System.IO.Path.Combine(
                temporaryRoot,
                $"immich-reversegeo-geoboundaries-{Guid.NewGuid():N}");
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
