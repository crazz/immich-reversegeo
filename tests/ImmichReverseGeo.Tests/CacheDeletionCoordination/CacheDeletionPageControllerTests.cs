using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests.CacheDeletionCoordination;

[TestClass]
[TestCategory("Change52")]
public sealed class CacheDeletionPageControllerTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [TestMethod]
    [DataRow(CacheMutationSource.Overture, "Overture")]
    [DataRow(CacheMutationSource.Gadm, "GADM")]
    public async Task DeleteAllConfirmation_NamesSourceCopiesSnapshotAndDismissesWithoutCommand(
        CacheMutationSource source,
        string sourceName)
    {
        var commands = 0;
        var targets = new List<CacheDeletionTarget> { new(source, "CHE") };
        await using var controller = CreateController(
            deleteAll: (_, _) =>
            {
                commands++;
                return Task.FromResult(Completed());
            });

        controller.RequestDeleteAll(source, targets);
        targets.Add(new CacheDeletionTarget(source, "DEU"));

        CacheDeletionConfirmation confirmation = controller.State.Confirmation!;
        Assert.AreEqual(CacheDeletionPagePhase.Confirming, controller.State.Phase);
        Assert.IsTrue(controller.State.ControlsDisabled);
        StringAssert.Contains(confirmation.Prompt, sourceName);
        StringAssert.Contains(confirmation.Prompt, "best-effort");
        Assert.HasCount(1, confirmation.Targets);
        Assert.AreEqual("CHE", confirmation.Targets[0].Iso3);
        IList<CacheDeletionTarget> exposed =
            Assert.IsInstanceOfType<IList<CacheDeletionTarget>>(confirmation.Targets);
        Assert.IsTrue(exposed.IsReadOnly);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            exposed[0] = new CacheDeletionTarget(source, "DEU"));

        controller.DismissConfirmation(confirmation.Generation);
        await controller.ConfirmAsync(confirmation.Generation);

        Assert.AreEqual(0, commands);
        Assert.AreEqual(CacheDeletionPagePhase.Idle, controller.State.Phase);
        Assert.IsFalse(controller.State.ControlsDisabled);
    }

    [TestMethod]
    public async Task CompletedEmptyAndInvalidOnly_DeleteAllAlwaysReloads()
    {
        var reloads = 0;
        var results = new Queue<CacheDeletionOperationResult>(
        [
            Completed(),
            Completed(new CacheDeletionTargetResult(
                0,
                null,
                null,
                CacheDeletionTargetDisposition.Invalid,
                "The cache target is invalid."))
        ]);
        await using var controller = CreateController(
            deleteAll: (_, _) => Task.FromResult(results.Dequeue()),
            reload: () =>
            {
                reloads++;
                return Task.CompletedTask;
            });

        controller.RequestDeleteAll(CacheMutationSource.Overture, []);
        await controller.ConfirmAsync(controller.State.Confirmation!.Generation);
        Assert.AreEqual(1, reloads);
        Assert.AreEqual(CacheDeletionPagePhase.Completed, controller.State.Phase);
        StringAssert.Contains(controller.State.Status!, "No Overture cache files");

        controller.RequestDeleteAll(
            CacheMutationSource.Overture,
            [new CacheDeletionTarget(CacheMutationSource.Overture, "bad")]);
        await controller.ConfirmAsync(controller.State.Confirmation!.Generation);

        Assert.AreEqual(2, reloads);
        Assert.AreEqual(CacheDeletionPagePhase.Completed, controller.State.Phase);
        Assert.AreEqual(1, controller.State.Result!.InvalidCount);
    }

    [TestMethod]
    [DataRow(CacheMutationSource.Overture, "Overture")]
    [DataRow(CacheMutationSource.Gadm, "GADM")]
    public async Task PerDeleteConfirmation_NamesSourceAndDismissesWithoutCommand(
        CacheMutationSource source,
        string sourceName)
    {
        var commands = 0;
        await using var controller = CreateController(
            delete: _ =>
            {
                commands++;
                return Task.FromResult(Completed());
            });

        controller.RequestDelete(new CacheDeletionTarget(source, "CHE"));
        CacheDeletionConfirmation confirmation = controller.State.Confirmation!;
        Assert.IsFalse(confirmation.IsBatch);
        StringAssert.Contains(confirmation.Prompt, sourceName);
        StringAssert.Contains(confirmation.Prompt, "CHE");
        Assert.IsTrue(controller.State.ControlsDisabled);

        controller.DismissConfirmation(confirmation.Generation);
        await controller.ConfirmAsync(confirmation.Generation);

        Assert.AreEqual(0, commands);
        Assert.AreEqual(CacheDeletionPagePhase.Idle, controller.State.Phase);
    }

    [TestMethod]
    public async Task StaleConfirmationGeneration_CannotRunOrDismissNewerSnapshot()
    {
        var commands = 0;
        CacheMutationSource? executedSource = null;
        await using var controller = CreateController(
            deleteAll: (source, _) =>
            {
                commands++;
                executedSource = source;
                return Task.FromResult(Completed());
            });

        controller.RequestDeleteAll(
            CacheMutationSource.Overture,
            [new CacheDeletionTarget(CacheMutationSource.Overture, "CHE")]);
        long staleGeneration = controller.State.Confirmation!.Generation;
        controller.RequestDeleteAll(
            CacheMutationSource.Gadm,
            [new CacheDeletionTarget(CacheMutationSource.Gadm, "DEU")]);
        CacheDeletionConfirmation current = controller.State.Confirmation!;

        controller.DismissConfirmation(staleGeneration);
        await controller.ConfirmAsync(staleGeneration);
        Assert.AreEqual(0, commands);
        Assert.AreSame(current, controller.State.Confirmation);

        await controller.ConfirmAsync(current.Generation);
        Assert.AreEqual(1, commands);
        Assert.AreEqual(CacheMutationSource.Gadm, executedSource);
    }

    [TestMethod]
    public async Task BusyAndUnavailable_NeverReloadAndRetainRejectionDetails()
    {
        CacheDeletionOperationResult[] results =
        [
            new(
                CacheDeletionOperationDisposition.Busy,
                [],
                1,
                new ExclusiveHeavyOwnerBusyMetadata.CacheMaintenance(
                    new CacheMaintenanceBusyMetadata(
                        CacheMaintenanceRequestOrigin.GeoBoundariesPage,
                        DateTimeOffset.UnixEpoch)),
                null,
                "Cache deletion could not start because another operation is active."),
            new(
                CacheDeletionOperationDisposition.Unavailable,
                [],
                1,
                null,
                "worker-stopping",
                "Cache deletion is unavailable while the application is stopping.")
        ];
        var reloads = 0;
        var calls = 0;
        await using var controller = CreateController(
            delete: _ => Task.FromResult(results[calls++]),
            reload: () =>
            {
                reloads++;
                return Task.CompletedTask;
            });

        await ConfirmDeleteAsync(
            controller,
            new CacheDeletionTarget(CacheMutationSource.Overture, "CHE"));
        Assert.AreEqual(CacheDeletionPagePhase.Busy, controller.State.Phase);
        Assert.IsInstanceOfType<ExclusiveHeavyOwnerBusyMetadata.CacheMaintenance>(
            controller.State.Result!.BusyOwner);
        Assert.IsFalse(controller.State.ControlsDisabled);

        await ConfirmDeleteAsync(
            controller,
            new CacheDeletionTarget(CacheMutationSource.Gadm, "CHE"));
        Assert.AreEqual(CacheDeletionPagePhase.Unavailable, controller.State.Phase);
        StringAssert.Contains(controller.State.Error!, "worker-stopping");
        Assert.IsFalse(controller.State.ControlsDisabled);
        Assert.AreEqual(0, reloads);
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public async Task Completed_ControlsStayDisabledUntilReloadFinishesAfterCommandReturn()
    {
        var command = new TaskCompletionSource<CacheDeletionOperationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var reloadStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReload = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var controller = CreateController(
            delete: _ => command.Task,
            reload: async () =>
            {
                reloadStarted.TrySetResult();
                await releaseReload.Task;
            });

        Task attempt = ConfirmDeleteAsync(
            controller,
            new CacheDeletionTarget(CacheMutationSource.Overture, "CHE"));
        Assert.AreEqual(CacheDeletionPagePhase.Deleting, controller.State.Phase);
        Assert.IsTrue(controller.State.ControlsDisabled);

        CacheDeletionOperationResult finalized = Completed(Target(
            CacheMutationSource.Overture,
            "CHE",
            CacheDeletionTargetDisposition.Deleted));
        command.TrySetResult(finalized);
        await reloadStarted.Task.WaitAsync(Bound);

        Assert.AreEqual(CacheDeletionPagePhase.Reloading, controller.State.Phase);
        Assert.IsTrue(controller.State.ControlsDisabled);
        Assert.AreSame(finalized, controller.State.Result);
        Assert.IsFalse(attempt.IsCompleted);

        releaseReload.TrySetResult();
        await attempt.WaitAsync(Bound);

        Assert.AreEqual(CacheDeletionPagePhase.Completed, controller.State.Phase);
        Assert.IsFalse(controller.State.ControlsDisabled);
        Assert.AreSame(finalized, controller.State.Result);
    }

    [TestMethod]
    public async Task ReloadFailure_IsSeparateFromFinalizedDeletionResult()
    {
        CacheDeletionOperationResult finalized = Completed(Target(
            CacheMutationSource.Gadm,
            "CHE",
            CacheDeletionTargetDisposition.Deleted));
        await using var controller = CreateController(
            delete: _ => Task.FromResult(finalized),
            reload: () => throw new IOException("host path and provider details"));

        await ConfirmDeleteAsync(
            controller,
            new CacheDeletionTarget(CacheMutationSource.Gadm, "CHE"));

        Assert.AreEqual(CacheDeletionPagePhase.Completed, controller.State.Phase);
        Assert.AreSame(finalized, controller.State.Result);
        Assert.AreEqual(1, controller.State.Result!.DeletedCount);
        StringAssert.Contains(controller.State.ReloadError!, "deletion result is final");
        Assert.IsFalse(
            controller.State.ReloadError!.Contains("host path", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task CompletedCopy_DistinguishesAlreadyAbsentPartialAndCompleteFailure()
    {
        var batches = new Queue<CacheDeletionOperationResult>(
        [
            Completed(
                Target(CacheMutationSource.Overture, "CHE", CacheDeletionTargetDisposition.Deleted),
                Target(CacheMutationSource.Overture, "DEU", CacheDeletionTargetDisposition.Failed)),
            Completed(
                Target(CacheMutationSource.Overture, "CHE", CacheDeletionTargetDisposition.Failed),
                Target(CacheMutationSource.Overture, "DEU", CacheDeletionTargetDisposition.Failed))
        ]);
        await using var controller = CreateController(
            delete: _ => Task.FromResult(Completed(Target(
                CacheMutationSource.Overture,
                "CHE",
                CacheDeletionTargetDisposition.Missing))),
            deleteAll: (_, _) => Task.FromResult(batches.Dequeue()));

        await ConfirmDeleteAsync(
            controller,
            new CacheDeletionTarget(CacheMutationSource.Overture, "CHE"));
        StringAssert.Contains(controller.State.Status!, "already absent");

        controller.RequestDeleteAll(
            CacheMutationSource.Overture,
            [new CacheDeletionTarget(CacheMutationSource.Overture, "CHE")]);
        await controller.ConfirmAsync(controller.State.Confirmation!.Generation);
        StringAssert.Contains(controller.State.Status!, "partially completed");

        controller.RequestDeleteAll(
            CacheMutationSource.Overture,
            [new CacheDeletionTarget(CacheMutationSource.Overture, "CHE")]);
        await controller.ConfirmAsync(controller.State.Confirmation!.Generation);
        StringAssert.Contains(controller.State.Status!, "did not complete");
    }

    [TestMethod]
    public async Task DisposeDuringAdmittedAttempt_WaitsAndSuppressesReloadAndLaterRender()
    {
        var commandStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new TaskCompletionSource<CacheDeletionOperationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var reloads = 0;
        var renders = 0;
        var controller = CreateController(
            delete: _ =>
            {
                commandStarted.TrySetResult();
                return command.Task;
            },
            changed: () =>
            {
                renders++;
                return Task.CompletedTask;
            },
            reload: () =>
            {
                reloads++;
                return Task.CompletedTask;
            });

        Task attempt = ConfirmDeleteAsync(
            controller,
            new CacheDeletionTarget(CacheMutationSource.Overture, "CHE"));
        await commandStarted.Task.WaitAsync(Bound);
        int rendersBeforeDisposal = renders;
        Task disposal = controller.DisposeAsync().AsTask();
        Assert.IsFalse(disposal.IsCompleted);

        command.TrySetResult(Completed(Target(
            CacheMutationSource.Overture,
            "CHE",
            CacheDeletionTargetDisposition.Deleted)));
        await Task.WhenAll(attempt, disposal).WaitAsync(Bound);

        Assert.AreEqual(0, reloads);
        Assert.AreEqual(rendersBeforeDisposal, renders);
    }

    private static CacheDeletionPageController CreateController(
        Func<CacheDeletionTarget, Task<CacheDeletionOperationResult>>? delete = null,
        Func<CacheMutationSource, IEnumerable<CacheDeletionTarget>,
            Task<CacheDeletionOperationResult>>? deleteAll = null,
        Func<Task>? changed = null,
        Func<Task>? reload = null) => new(
            delete ?? (_ => Task.FromResult(Completed())),
            deleteAll ?? ((_, _) => Task.FromResult(Completed())),
            changed ?? (() => Task.CompletedTask),
            reload ?? (() => Task.CompletedTask));

    private static Task ConfirmDeleteAsync(
        CacheDeletionPageController controller,
        CacheDeletionTarget target)
    {
        controller.RequestDelete(target);
        return controller.ConfirmAsync(controller.State.Confirmation!.Generation);
    }

    private static CacheDeletionOperationResult Completed(
        params CacheDeletionTargetResult[] targets) => new(
            CacheDeletionOperationDisposition.Completed,
            targets,
            0,
            null,
            null,
            null);

    private static CacheDeletionTargetResult Target(
        CacheMutationSource source,
        string iso3,
        CacheDeletionTargetDisposition disposition) => new(
            0,
            source,
            iso3,
            disposition,
            "Safe result.");
}
