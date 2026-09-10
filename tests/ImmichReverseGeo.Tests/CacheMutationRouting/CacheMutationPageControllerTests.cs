using System.Threading.Channels;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests.CacheMutationRouting;

[TestClass]
[TestCategory("Change51")]
public sealed class CacheMutationPageControllerTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private static readonly Guid JobId =
        Guid.Parse("11111111-2222-3333-4444-555555555555");

    [TestMethod]
    public async Task Refresh_UsesOneEnvelopeIdentityAndImmutableRefreshRequest()
    {
        var admission = new RecordingAdmissionGate();
        var worker = new RecordingWorkerClient();
        var identityCalls = 0;
        await using var controller = Create(
            admission,
            worker,
            () =>
            {
                identityCalls++;
                return JobId;
            });

        Task run = controller.RefreshAsync(CacheMutationSource.Overture, "CHE");
        FakeWorkerSession? session = null;
        try
        {
            session = await worker.WaitForSessionAsync();
            await session.CompletionObserved.WaitAsync(Bound);
            var dispatch = Assert.IsInstanceOfType<CacheMutationWorkerJobDispatch>(admission.Dispatch);
            Assert.AreEqual(1, identityCalls);
            Assert.AreEqual(JobId, dispatch.Context.JobId);
            Assert.AreEqual(WorkerJobRequestOrigin.Manual, dispatch.Context.Origin);
            Assert.AreSame(dispatch.Request, worker.Request);
            Assert.AreEqual(CacheMutationSource.Overture, worker.Request!.Source);
            Assert.AreEqual(CacheMutationOperation.Refresh, worker.Request.Operation);
            Assert.AreEqual("CHE", worker.Request.Iso3);
            Assert.AreEqual(JobId, session.JobId);
            Assert.AreSame(WorkerJobDescriptors.CacheMutation, dispatch.Descriptor);

            await controller.RefreshAsync(CacheMutationSource.Gadm, "USA");
            Assert.AreEqual(1, identityCalls);
            Assert.AreEqual(1, admission.Attempts);
            Assert.AreEqual(1, worker.Starts);
            Assert.AreEqual(CacheMutationSource.Overture, worker.Request.Source);
            Assert.AreEqual("CHE", worker.Request.Iso3);
        }
        finally
        {
            session ??= worker.LastSession;
            session?.Complete(new CacheMutationWorkerOutcome.Completed(Result()));
            await run.WaitAsync(Bound);
        }
    }

    [TestMethod]
    public async Task Completed_ReleasesAfterExactSessionFinalityThenReloadsAndSignalsSuccess()
    {
        var ledger = new List<string>();
        var admission = new RecordingAdmissionGate(ledger);
        var worker = new RecordingWorkerClient(ledger);
        var completion = new RecordingCompletionSink(ledger);
        await using var controller = Create(
            admission,
            worker,
            reload: () =>
            {
                ledger.Add("reload");
                return Task.CompletedTask;
            },
            completion: completion);

        Task run = controller.RefreshAsync(CacheMutationSource.Overture, "CHE");
        FakeWorkerSession? session = null;
        try
        {
            session = await worker.WaitForSessionAsync();
            await session.CompletionObserved.WaitAsync(Bound);
            Assert.AreEqual(0, admission.Lease!.DisposeCount);
            Assert.IsTrue(controller.State.HasAdmittedOperation);
            CollectionAssert.DoesNotContain(ledger, "reload");
            CollectionAssert.DoesNotContain(ledger, "success");
        }
        finally
        {
            session ??= worker.LastSession;
            session?.Complete(new CacheMutationWorkerOutcome.Completed(Result()));
            await run.WaitAsync(Bound);
        }

        CollectionAssert.AreEqual(
            new[] { "session-final", "session-dispose", "lease-dispose", "reload", "success" },
            ledger.Where(static item => item is
                "session-final" or "session-dispose" or "lease-dispose" or "reload" or "success")
                .ToArray());
        Assert.AreEqual(CacheMutationPagePhase.Completed, controller.State.Phase);
        Assert.AreEqual(1, completion.Count);
        Assert.AreEqual(1, admission.Lease!.DisposeCount);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task RejectedAdmission_StartsNoWorkerAndDoesNotReload(bool busy)
    {
        var ledger = new List<string>();
        var admission = new RecordingAdmissionGate(ledger)
        {
            Next = busy
                ? new WorkerJobAdmissionResult.Busy(
                    new ExclusiveHeavyOwnerBusyMetadata.Worker(new WorkerJobBusyMetadata(
                        WorkerJobCapabilityFamily.Processing,
                        WorkerJobRequestOrigin.Scheduled,
                        true)))
                : new WorkerJobAdmissionResult.Unavailable(
                    "worker-stopping",
                    "Worker admission is unavailable.")
        };
        var worker = new RecordingWorkerClient(ledger);
        var completion = new RecordingCompletionSink(ledger);
        await using var controller = Create(
            admission,
            worker,
            reload: () =>
            {
                ledger.Add("reload");
                return Task.CompletedTask;
            },
            completion: completion);

        await controller.RefreshAsync(CacheMutationSource.Gadm, "CHE");

        Assert.AreEqual(busy ? CacheMutationPagePhase.Busy : CacheMutationPagePhase.Unavailable,
            controller.State.Phase);
        Assert.AreEqual(0, worker.Starts);
        CollectionAssert.DoesNotContain(ledger, "reload");
        CollectionAssert.DoesNotContain(ledger, "success");
        Assert.AreEqual(0, completion.Count);
        Assert.IsFalse(controller.State.HasAdmittedOperation);
        Assert.IsFalse(controller.State.CanCancel);
        Assert.IsNull(controller.State.JobId);
        Assert.IsNull(admission.Lease);
        Assert.AreEqual(1, admission.Attempts);
        if (busy)
        {
            Assert.AreEqual(WorkerJobCapabilityFamily.Processing,
                controller.State.BusyOwner.RequireWorker().CapabilityFamily);
            Assert.AreEqual(WorkerJobRequestOrigin.Scheduled,
                controller.State.BusyOwner.RequireWorker().Origin);
            Assert.AreEqual(WorkerJobLifecycle.Admitted,
                controller.State.BusyOwner.RequireWorker().Lifecycle);
        }
        else
        {
            Assert.IsNull(controller.State.BusyOwner);
        }
    }

    [TestMethod]
    public async Task AdmittedStartupUnavailable_ReleasesAndReloadsWithoutSuccessNotification()
    {
        var ledger = new List<string>();
        var admission = new RecordingAdmissionGate(ledger);
        var releaseStart = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new RecordingWorkerClient(ledger)
        {
            StartUnavailable = true,
            StartRelease = releaseStart
        };
        var completion = new RecordingCompletionSink(ledger);
        await using var controller = Create(
            admission,
            worker,
            reload: () =>
            {
                ledger.Add("reload");
                return Task.CompletedTask;
            },
            completion: completion);

        Task run = controller.RefreshAsync(CacheMutationSource.Overture, "CHE");
        try
        {
            await worker.WaitForStartAsync();
            Assert.AreEqual(0, admission.Lease!.DisposeCount);
            Assert.IsTrue(controller.State.HasAdmittedOperation);
            Assert.AreEqual(0, completion.Count);
            CollectionAssert.DoesNotContain(ledger, "reload");
        }
        finally
        {
            releaseStart.TrySetResult();
            await run.WaitAsync(Bound);
        }

        Assert.AreEqual(CacheMutationPagePhase.Unavailable, controller.State.Phase);
        Assert.AreEqual(1, admission.Lease!.DisposeCount);
        CollectionAssert.Contains(ledger, "reload");
        Assert.AreEqual(0, completion.Count);
    }

    [TestMethod]
    public async Task Cancellation_IsIdempotentAndReloadCallbackFailureCannotLeakLease()
    {
        var admission = new RecordingAdmissionGate();
        var worker = new RecordingWorkerClient();
        var completion = new RecordingCompletionSink();
        await using var controller = Create(
            admission,
            worker,
            reload: static () => throw new ObjectDisposedException("page"),
            completion: completion);

        Task run = controller.RefreshAsync(CacheMutationSource.Overture, "CHE");
        FakeWorkerSession? session = null;
        Task? first = null;
        Task? second = null;
        try
        {
            session = await worker.WaitForSessionAsync();
            await session.CompletionObserved.WaitAsync(Bound);
            first = controller.CancelAsync();
            second = controller.CancelAsync();
            await session.StopRequested.WaitAsync(Bound);
            Assert.AreEqual(CacheMutationPagePhase.CancelRequested, controller.State.Phase);
            Assert.AreEqual(0, admission.Lease!.DisposeCount);
        }
        finally
        {
            session ??= worker.LastSession;
            session?.Complete(new CacheMutationWorkerOutcome.Cancelled());
            await run.WaitAsync(Bound);
            if (first is not null && second is not null)
            {
                await Task.WhenAll(first, second).WaitAsync(Bound);
            }
        }

        Assert.AreEqual(1, session!.StopCount);
        Assert.AreEqual(1, session.DisposeCount);
        Assert.AreEqual(1, admission.Lease!.DisposeCount);
        Assert.AreEqual(CacheMutationPagePhase.Cancelled, controller.State.Phase);
        Assert.IsNull(controller.State.Result);
        Assert.AreEqual(0, completion.Count);
        CollectionAssert.AreEqual(
            new[]
            {
                WorkerJobLifecycle.Starting,
                WorkerJobLifecycle.Stopping,
                WorkerJobLifecycle.Finalizing
            },
            admission.Lease.Advances.Select(static advance => advance.Lifecycle).ToArray());
    }

    [TestMethod]
    public async Task TerminalAcceptedBeforeCancel_PreservesCompletedOutcome()
    {
        string tempDir = CreateTempDir();
        string cachePath = Path.Combine(tempDir, "CHE.db");
        File.WriteAllText(cachePath, "old-cache-bytes");
        var coordinator = new WorkerJobCoordinator(WorkerJobDescriptors.Registered);
        var ownerWorker = new RecordingWorkerClient { ChildProcessId = 4242 };
        var contenderWorker = new RecordingWorkerClient();
        var completion = new RecordingCompletionSink();
        var reloadCount = 0;
        string? reloadedBytes = null;
        CacheMutationPageController owner = Create(
            coordinator,
            ownerWorker,
            reload: () =>
            {
                reloadCount++;
                reloadedBytes = File.ReadAllText(cachePath);
                return Task.CompletedTask;
            },
            completion: completion);
        CacheMutationPageController contender = Create(coordinator, contenderWorker);
        Task ownerRun = Task.CompletedTask;
        FakeWorkerSession? ownerSession = null;
        try
        {
            ownerRun = owner.RefreshAsync(CacheMutationSource.Overture, "CHE");
            ownerSession = await ownerWorker.WaitForSessionAsync();
            await ownerSession.CompletionObserved.WaitAsync(Bound);
            File.WriteAllText(cachePath, "replacement-visible-to-reload");
            Assert.AreEqual("replacement-visible-to-reload", File.ReadAllText(cachePath));

            ownerSession.Emit(Terminal(Result(), ownerSession.JobId));
            await owner.CancelAsync();
            await contender.RefreshAsync(CacheMutationSource.Gadm, "CHE");

            Assert.IsTrue(owner.State.TerminalObserved);
            Assert.IsTrue(owner.State.HasAdmittedOperation);
            Assert.IsTrue(owner.State.IsActive);
            Assert.IsFalse(owner.State.CanCancel);
            Assert.IsNull(owner.State.Result);
            Assert.AreEqual(0, ownerSession.StopCount);
            Assert.AreEqual(0, reloadCount);
            Assert.AreEqual(0, completion.Count);
            Assert.AreEqual(CacheMutationPagePhase.Busy, contender.State.Phase);
            Assert.AreEqual(0, contenderWorker.Starts);
            Assert.IsNotNull(coordinator.Snapshot.ActiveOwner);
            Assert.AreEqual(WorkerJobLifecycle.Running,
                coordinator.Snapshot.ActiveOwner.RequireWorker().Lifecycle);
            Assert.AreEqual(4242, coordinator.ActiveOwner.RequireWorker().ChildProcessId);

            ownerSession.Complete(new CacheMutationWorkerOutcome.Completed(Result()));
            await ownerRun.WaitAsync(Bound);

            Assert.AreEqual(CacheMutationPagePhase.Completed, owner.State.Phase);
            Assert.AreEqual(1, reloadCount);
            Assert.AreEqual("replacement-visible-to-reload", reloadedBytes);
            Assert.AreEqual(1, completion.Count);
            Assert.AreEqual(1, ownerSession.DisposeCount);
            Assert.IsNull(coordinator.Snapshot.ActiveOwner);
        }
        finally
        {
            ownerSession ??= ownerWorker.LastSession;
            ownerSession?.Complete(new CacheMutationWorkerOutcome.Cancelled());
            await DrainCleanupAsync(
                () => ownerRun.WaitAsync(Bound),
                () => owner.DisposeAsync().AsTask().WaitAsync(Bound),
                () => contender.DisposeAsync().AsTask().WaitAsync(Bound),
                () => coordinator.DisposeAsync().AsTask().WaitAsync(Bound),
                () =>
                {
                    Directory.Delete(tempDir, recursive: true);
                    return Task.CompletedTask;
                });
        }

    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task FailedAndCancelledCompletion_ReloadOnlyAfterCapabilityFinality(
        bool cancelled)
    {
        var ledger = new List<string>();
        var admission = new RecordingAdmissionGate(ledger);
        var worker = new RecordingWorkerClient(ledger);
        var completion = new RecordingCompletionSink(ledger);
        await using var controller = Create(
            admission,
            worker,
            reload: () =>
            {
                ledger.Add("reload");
                return Task.CompletedTask;
            },
            completion: completion);

        Task run = controller.RefreshAsync(CacheMutationSource.Overture, "CHE");
        FakeWorkerSession? session = null;
        try
        {
            session = await worker.WaitForSessionAsync();
            await session.CompletionObserved.WaitAsync(Bound);
            Assert.AreEqual(0, admission.Lease!.DisposeCount);
            Assert.IsTrue(controller.State.HasAdmittedOperation);
            Assert.IsNull(controller.State.Result);
            Assert.AreEqual(0, completion.Count);
            CollectionAssert.DoesNotContain(ledger, "reload");
        }
        finally
        {
            session ??= worker.LastSession;
            session?.Complete(cancelled
                    ? new CacheMutationWorkerOutcome.Cancelled()
                    : new CacheMutationWorkerOutcome.Failed(
                        "controlled-failure",
                        "Controlled cache failure."));
            await run.WaitAsync(Bound);
        }

        Assert.AreEqual(
            cancelled ? CacheMutationPagePhase.Cancelled : CacheMutationPagePhase.Failed,
            controller.State.Phase);
        Assert.IsNull(controller.State.Result);
        Assert.AreEqual(1, ledger.Count(static item => item == "reload"));
        Assert.AreEqual(0, completion.Count);
        Assert.AreEqual(1, admission.Lease!.DisposeCount);
    }

    [TestMethod]
    public async Task CorrelatedLifecycle_UsesExactOwnerAndSetsPidOnlyAtRunning()
    {
        var admission = new RecordingAdmissionGate();
        var worker = new RecordingWorkerClient { ChildProcessId = 4242 };
        await using var controller = Create(admission, worker);

        Task run = controller.RefreshAsync(CacheMutationSource.Overture, "CHE");
        FakeWorkerSession? session = null;
        try
        {
            session = await worker.WaitForSessionAsync();
            await session.CompletionObserved.WaitAsync(Bound);
            session.Emit(Started(JobId));
            Assert.AreEqual(CacheMutationPagePhase.Running, controller.State.Phase);
            RecordingLease lease = admission.Lease!;
            Assert.AreSame(lease.Context, lease.BoundContext);
            CollectionAssert.AreEqual(
                new[] { WorkerJobLifecycle.Starting, WorkerJobLifecycle.Running },
                lease.Advances.Select(static advance => advance.Lifecycle).ToArray());
            Assert.IsNull(lease.Advances[0].ChildProcessId);
            Assert.AreEqual(4242, lease.Advances[1].ChildProcessId);
            Assert.IsTrue(lease.Advances.All(advance => ReferenceEquals(lease.Context, advance.Context)));
            Assert.AreEqual(0, lease.DisposeCount);
        }
        finally
        {
            session ??= worker.LastSession;
            session?.Complete(new CacheMutationWorkerOutcome.Completed(Result()));
            await run.WaitAsync(Bound);
        }

        RecordingLease finalized = admission.Lease!;
        CollectionAssert.AreEqual(
            new[]
            {
                WorkerJobLifecycle.Starting,
                WorkerJobLifecycle.Running,
                WorkerJobLifecycle.Finalizing
            },
            finalized.Advances.Select(static advance => advance.Lifecycle).ToArray());
        Assert.IsNull(finalized.Advances[2].ChildProcessId);
        Assert.AreEqual(1, finalized.DisposeCount);
    }

    [TestMethod]
    public async Task WrongEventsAndStaleGeneration_DoNotProjectIntoCurrentAttempt()
    {
        var worker = new RecordingWorkerClient();
        var admission = new RecordingAdmissionGate();
        await using var controller = Create(admission, worker);
        Task firstRun = controller.RefreshAsync(CacheMutationSource.Overture, "CHE");
        FakeWorkerSession? firstSession = null;
        FakeWorkerSession? secondSession = null;
        Task? secondRun = null;
        try
        {
            firstSession = await worker.WaitForSessionAsync();
            await firstSession.CompletionObserved.WaitAsync(Bound);
            CacheMutationPageState original = controller.State;
            firstSession.Emit(Progress(Guid.NewGuid(), WorkerJobKind.CacheMutation));
            firstSession.Emit(Started(JobId, WorkerJobKind.CoordinateLookup));
            firstSession.Emit(Progress(
                JobId,
                WorkerJobKind.CacheMutation,
                CacheMutationSource.Gadm));
            Assert.AreEqual(original, controller.State);

            firstSession.Emit(Progress(JobId, WorkerJobKind.CacheMutation));
            Assert.AreEqual(CacheMutationProgressStep.ValidatingCandidate,
                controller.State.CurrentStep);
            Assert.AreEqual("Validating cache candidate.", controller.State.Status);
            Guid activityId = Guid.Parse("cccccccc-1111-2222-3333-444444444444");
            firstSession.Emit(ActivityStarted(JobId, activityId));
            Assert.AreEqual("Exporting controlled cache", controller.State.CurrentActivity);
            CacheMutationPageState beforeLog = controller.State;
            firstSession.Emit(Log(JobId));
            Assert.AreEqual(beforeLog, controller.State,
                "Logs remain outside page-state projection.");
            firstSession.Emit(ActivityEnded(JobId, activityId));
            Assert.IsNull(controller.State.CurrentActivity);
            firstSession.Complete(new CacheMutationWorkerOutcome.Cancelled());
            await firstRun.WaitAsync(Bound);

            secondRun = controller.RefreshAsync(CacheMutationSource.Overture, "CHE");
            secondSession = await worker.WaitForSessionAsync();
            await secondSession.CompletionObserved.WaitAsync(Bound);
            Assert.IsNull(controller.State.CurrentStep);
            CacheMutationPageState secondOriginal = controller.State;
            firstSession.Emit(Progress(
                JobId,
                WorkerJobKind.CacheMutation,
                message: "stale generation"));
            Assert.AreEqual(secondOriginal, controller.State);

            secondSession.Emit(Progress(
                JobId,
                WorkerJobKind.CacheMutation,
                message: "current generation"));
            Assert.AreEqual("current generation", controller.State.Status);
        }
        finally
        {
            firstSession ??= worker.LastSession;
            firstSession?.Complete(new CacheMutationWorkerOutcome.Cancelled());
            if (secondSession is not null)
            {
                secondSession.Complete(new CacheMutationWorkerOutcome.Cancelled());
            }

            await firstRun.WaitAsync(Bound);
            if (secondRun is not null)
            {
                await secondRun.WaitAsync(Bound);
            }
        }
    }

    [TestMethod]
    [DataRow("job-id")]
    [DataRow("job-kind")]
    [DataRow("protocol")]
    public async Task MismatchedSession_IsStoppedBeforeFailureReloadAndRelease(string mismatch)
    {
        var ledger = new List<string>();
        var admission = new RecordingAdmissionGate(ledger);
        var worker = new RecordingWorkerClient(ledger)
        {
            SessionFactory = (jobId, sessionLedger, emit) => new FakeWorkerSession(
                mismatch == "job-id" ? Guid.NewGuid() : jobId,
                sessionLedger,
                emit,
                mismatch == "job-kind"
                    ? WorkerJobKind.CoordinateLookup
                    : WorkerJobKind.CacheMutation,
                mismatch == "protocol"
                    ? InternalWorkerProtocolVersion.V1
                    : InternalWorkerProtocolVersion.V2)
        };
        var completion = new RecordingCompletionSink(ledger);
        await using var controller = Create(
            admission,
            worker,
            reload: () =>
            {
                ledger.Add("reload");
                return Task.CompletedTask;
            },
            completion: completion);

        Task run = controller.RefreshAsync(CacheMutationSource.Overture, "CHE");
        FakeWorkerSession? session = null;
        try
        {
            session = await worker.WaitForSessionAsync();
            await session.StopRequested.WaitAsync(Bound);
            Assert.AreEqual(1, session.StopCount);
            Assert.AreEqual(0, admission.Lease!.DisposeCount);
            Assert.IsTrue(controller.State.HasAdmittedOperation);
            Assert.IsNull(controller.State.Result);
            Assert.AreEqual(0, completion.Count);
            CollectionAssert.DoesNotContain(ledger, "reload");
        }
        finally
        {
            session ??= worker.LastSession;
            session?.Complete(new CacheMutationWorkerOutcome.Cancelled());
            await run.WaitAsync(Bound);
        }

        Assert.AreEqual(CacheMutationPagePhase.Failed, controller.State.Phase);
        StringAssert.Contains(controller.State.Error!, "cache-correlation");
        Assert.AreEqual(1, admission.Lease!.DisposeCount);
        Assert.AreEqual(1, ledger.Count(static item => item == "reload"));
        Assert.AreEqual(0, completion.Count);
    }

    [TestMethod]
    public async Task CancelBeforeStartReturns_IsIdempotentAndHoldsOwnerUntilCompletion()
    {
        var ledger = new List<string>();
        var releaseStart = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var admission = new RecordingAdmissionGate(ledger);
        var worker = new RecordingWorkerClient(ledger) { StartRelease = releaseStart };
        var completion = new RecordingCompletionSink(ledger);
        await using var controller = Create(
            admission,
            worker,
            reload: () =>
            {
                ledger.Add("reload");
                return Task.CompletedTask;
            },
            completion: completion);

        Task run = controller.RefreshAsync(CacheMutationSource.Overture, "CHE");
        Task? firstCancel = null;
        Task? secondCancel = null;
        FakeWorkerSession? session = null;
        try
        {
            await worker.WaitForStartAsync();
            firstCancel = controller.CancelAsync();
            secondCancel = controller.CancelAsync();
            Assert.AreEqual(CacheMutationPagePhase.CancelRequested, controller.State.Phase);
            Assert.IsTrue(controller.State.HasAdmittedOperation);
            Assert.IsFalse(controller.State.CanCancel);
            Assert.AreEqual(0, admission.Lease!.DisposeCount);
            CollectionAssert.DoesNotContain(ledger, "reload");

            releaseStart.TrySetResult();
            session = await worker.WaitForSessionAsync();
            await session.StopRequested.WaitAsync(Bound);
            await session.CompletionObserved.WaitAsync(Bound);
            Assert.AreEqual(1, session.StopCount);
            Assert.AreEqual(0, admission.Lease.DisposeCount);
            Assert.AreEqual(0, completion.Count);
        }
        finally
        {
            releaseStart.TrySetResult();
            if (session is null)
            {
                session = await worker.WaitForSessionAsync();
            }

            session.Complete(new CacheMutationWorkerOutcome.Cancelled());
            await run.WaitAsync(Bound);
            if (firstCancel is not null)
            {
                await firstCancel.WaitAsync(Bound);
            }

            if (secondCancel is not null)
            {
                await secondCancel.WaitAsync(Bound);
            }
        }

        Assert.AreEqual(CacheMutationPagePhase.Cancelled, controller.State.Phase);
        Assert.AreEqual(1, admission.Lease!.DisposeCount);
        Assert.AreEqual(1, ledger.Count(static item => item == "reload"));
        Assert.AreEqual(0, completion.Count);
    }

    [TestMethod]
    public async Task CancelBeforeTerminalCommit_ReloadsCurrentDiskBytesOnlyAfterCapabilityFinality()
    {
        string tempDir = CreateTempDir();
        string cachePath = Path.Combine(tempDir, "CHE.db");
        File.WriteAllText(cachePath, "old-cache-bytes");
        var coordinator = new WorkerJobCoordinator(WorkerJobDescriptors.Registered);
        var ownerWorker = new RecordingWorkerClient { ChildProcessId = 4242 };
        var contenderWorker = new RecordingWorkerClient();
        Guid busyAttemptId = Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444");
        Guid retryId = Guid.Parse("bbbbbbbb-1111-2222-3333-444444444444");
        Guid[] contenderIds = [busyAttemptId, retryId];
        var contenderIdentityCalls = 0;
        var reloadCount = 0;
        string? reloadedBytes = null;
        var completion = new RecordingCompletionSink();
        CacheMutationPageController owner = Create(
            coordinator,
            ownerWorker,
            reload: () =>
            {
                reloadCount++;
                reloadedBytes = File.ReadAllText(cachePath);
                return Task.CompletedTask;
            },
            completion: completion);
        CacheMutationPageController contender = Create(
            coordinator,
            contenderWorker,
            identity: () => contenderIds[contenderIdentityCalls++]);
        Task ownerRun = Task.CompletedTask;
        Task? cancel = null;
        Task? retryRun = null;
        FakeWorkerSession? ownerSession = null;
        FakeWorkerSession? retrySession = null;
        try
        {
            ownerRun = owner.RefreshAsync(CacheMutationSource.Overture, "CHE");
            ownerSession = await ownerWorker.WaitForSessionAsync();
            await ownerSession.CompletionObserved.WaitAsync(Bound);
            File.WriteAllText(cachePath, "replacement-visible-to-reload");
            Assert.AreEqual("replacement-visible-to-reload", File.ReadAllText(cachePath));

            cancel = owner.CancelAsync();
            await ownerSession.StopRequested.WaitAsync(Bound);
            await contender.RefreshAsync(CacheMutationSource.Gadm, "CHE");

            Assert.AreEqual(CacheMutationPagePhase.CancelRequested, owner.State.Phase);
            Assert.IsTrue(owner.State.HasAdmittedOperation);
            Assert.IsTrue(owner.State.IsActive);
            Assert.IsFalse(owner.State.CanCancel);
            Assert.IsNull(owner.State.Result);
            Assert.AreEqual(1, ownerSession.StopCount);
            Assert.AreEqual(0, reloadCount);
            Assert.AreEqual(0, completion.Count);
            Assert.AreEqual(CacheMutationPagePhase.Busy, contender.State.Phase);
            Assert.AreEqual(0, contenderWorker.Starts);
            Assert.IsNull(contender.State.JobId);
            Assert.AreEqual(1, contenderIdentityCalls);
            Assert.AreEqual(WorkerJobLifecycle.Stopping,
                coordinator.Snapshot.ActiveOwner.RequireWorker().Lifecycle);

            ownerSession.Complete(new CacheMutationWorkerOutcome.Cancelled());
            await Task.WhenAll(ownerRun, cancel!).WaitAsync(Bound);

            Assert.AreEqual(CacheMutationPagePhase.Cancelled, owner.State.Phase);
            Assert.IsNull(owner.State.Result);
            Assert.AreEqual(1, reloadCount);
            Assert.AreEqual("replacement-visible-to-reload", reloadedBytes);
            Assert.AreEqual(0, completion.Count);
            Assert.AreEqual(1, ownerSession.DisposeCount);
            Assert.IsNull(coordinator.Snapshot.ActiveOwner);

            retryRun = contender.RefreshAsync(CacheMutationSource.Gadm, "CHE");
            retrySession = await contenderWorker.WaitForSessionAsync();
            await retrySession.CompletionObserved.WaitAsync(Bound);
            Assert.AreEqual(2, contenderIdentityCalls);
            Assert.AreEqual(retryId, retrySession.JobId);
            Assert.AreEqual(1, contenderWorker.Starts);
            Assert.AreEqual(CacheMutationSource.Gadm, contenderWorker.Request!.Source);
            Assert.AreEqual(CacheMutationOperation.Refresh, contenderWorker.Request.Operation);
            retrySession.Complete(new CacheMutationWorkerOutcome.Cancelled());
            await retryRun.WaitAsync(Bound);
            Assert.IsNull(coordinator.Snapshot.ActiveOwner);
        }
        finally
        {
            ownerSession ??= ownerWorker.LastSession;
            retrySession ??= contenderWorker.LastSession;
            ownerSession?.Complete(new CacheMutationWorkerOutcome.Cancelled());
            retrySession?.Complete(new CacheMutationWorkerOutcome.Cancelled());
            await DrainCleanupAsync(
                () => ownerRun.WaitAsync(Bound),
                () => cancel is { } cancelTask
                    ? cancelTask.WaitAsync(Bound)
                    : Task.CompletedTask,
                () => retryRun is { } retryTask
                    ? retryTask.WaitAsync(Bound)
                    : Task.CompletedTask,
                () => owner.DisposeAsync().AsTask().WaitAsync(Bound),
                () => contender.DisposeAsync().AsTask().WaitAsync(Bound),
                () => coordinator.DisposeAsync().AsTask().WaitAsync(Bound),
                () =>
                {
                    Directory.Delete(tempDir, recursive: true);
                    return Task.CompletedTask;
                });
        }
    }

    [TestMethod]
    public async Task Disposal_WaitsForCapabilityFinalityThenReleasesCoordinatorForReuse()
    {
        var coordinator = new WorkerJobCoordinator(WorkerJobDescriptors.Registered);
        var ownerWorker = new RecordingWorkerClient();
        var reuseWorker = new RecordingWorkerClient();
        var reloadCount = 0;
        var completion = new RecordingCompletionSink();
        CacheMutationPageController owner = Create(
            coordinator,
            ownerWorker,
            reload: () =>
            {
                reloadCount++;
                return Task.CompletedTask;
            },
            completion: completion);
        CacheMutationPageController reuse = Create(coordinator, reuseWorker);
        Task ownerRun = Task.CompletedTask;
        Task? disposal = null;
        Task? reuseRun = null;
        FakeWorkerSession? ownerSession = null;
        FakeWorkerSession? reuseSession = null;
        try
        {
            ownerRun = owner.RefreshAsync(CacheMutationSource.Overture, "CHE");
            ownerSession = await ownerWorker.WaitForSessionAsync();
            await ownerSession.CompletionObserved.WaitAsync(Bound);
            disposal = owner.DisposeAsync().AsTask();
            await ownerSession.StopRequested.WaitAsync(Bound);

            Assert.AreEqual(1, ownerSession.StopCount);
            Assert.IsNotNull(coordinator.Snapshot.ActiveOwner);
            Assert.AreEqual(0, reloadCount);
            Assert.AreEqual(0, completion.Count);

            ownerSession.Complete(new CacheMutationWorkerOutcome.Cancelled());
            await Task.WhenAll(ownerRun, disposal!).WaitAsync(Bound);
            Assert.IsNull(coordinator.Snapshot.ActiveOwner);
            Assert.AreEqual(0, reloadCount,
                "Disposed pages suppress stale reload callbacks after owner cleanup.");
            Assert.AreEqual(0, completion.Count);

            reuseRun = reuse.RefreshAsync(CacheMutationSource.Gadm, "CHE");
            reuseSession = await reuseWorker.WaitForSessionAsync();
            await reuseSession.CompletionObserved.WaitAsync(Bound);
            Assert.AreEqual(1, reuseWorker.Starts);
            Assert.AreEqual(WorkerJobKind.CacheMutation,
                coordinator.Snapshot.ActiveOwner.RequireWorker().JobKind);
            reuseSession.Complete(new CacheMutationWorkerOutcome.Cancelled());
            await reuseRun.WaitAsync(Bound);
            Assert.IsNull(coordinator.Snapshot.ActiveOwner);
        }
        finally
        {
            ownerSession ??= ownerWorker.LastSession;
            reuseSession ??= reuseWorker.LastSession;
            ownerSession?.Complete(new CacheMutationWorkerOutcome.Cancelled());
            reuseSession?.Complete(new CacheMutationWorkerOutcome.Cancelled());
            await DrainCleanupAsync(
                () => ownerRun.WaitAsync(Bound),
                () => disposal is { } disposalTask
                    ? disposalTask.WaitAsync(Bound)
                    : Task.CompletedTask,
                () => reuseRun is { } reuseTask
                    ? reuseTask.WaitAsync(Bound)
                    : Task.CompletedTask,
                () => owner.DisposeAsync().AsTask().WaitAsync(Bound),
                () => reuse.DisposeAsync().AsTask().WaitAsync(Bound),
                () => coordinator.DisposeAsync().AsTask().WaitAsync(Bound));
        }
    }

    [TestMethod]
    public async Task CallbackFailures_DoNotLeakLeaseOrRewriteCompletedResult()
    {
        var admission = new RecordingAdmissionGate();
        var worker = new RecordingWorkerClient();
        var completion = new RecordingCompletionSink(throwAfterRecord: true);
        await using var controller = Create(
            admission,
            worker,
            stateChanged: static () => throw new ObjectDisposedException("render"),
            reload: static () => throw new ObjectDisposedException("reload"),
            completion: completion);

        Task run = controller.RefreshAsync(CacheMutationSource.Overture, "CHE");
        FakeWorkerSession? session = null;
        try
        {
            session = await worker.WaitForSessionAsync();
            await session.CompletionObserved.WaitAsync(Bound);
            Assert.AreEqual(0, admission.Lease!.DisposeCount);
        }
        finally
        {
            session ??= worker.LastSession;
            session?.Complete(new CacheMutationWorkerOutcome.Completed(Result()));
            await run.WaitAsync(Bound);
        }

        Assert.AreEqual(CacheMutationPagePhase.Completed, controller.State.Phase);
        Assert.AreEqual(Result(), controller.State.Result);
        Assert.AreEqual(1, completion.Count);
        Assert.AreEqual(1, admission.Lease!.DisposeCount);
    }

    private static CacheMutationPageController Create(
        IWorkerJobAdmissionGate admission,
        RecordingWorkerClient worker,
        Func<Guid>? identity = null,
        Func<Task>? stateChanged = null,
        Func<Task>? reload = null,
        ICacheMutationCompletionSink? completion = null) =>
        new(
            admission,
            worker,
            identity ?? (static () => JobId),
            stateChanged ?? (static () => Task.CompletedTask),
            reload ?? (static () => Task.CompletedTask),
            completion);

    private static CacheMutationResult Result()
    {
        var now = new DateTimeOffset(2026, 9, 9, 8, 0, 0, TimeSpan.Zero);
        return new CacheMutationResult(
            now,
            now.AddSeconds(1),
            new CacheMutationSourceResult(
                CacheMutationSource.Overture,
                CacheMutationOperation.Refresh,
                "CHE",
                CacheMutationDisposition.Published,
                7,
                now,
                4096,
                "2026-09-03.0",
                null));
    }

    private static WorkerJobOutputMessage Terminal(
        CacheMutationResult result,
        Guid? jobId = null,
        WorkerJobKind jobKind = WorkerJobKind.CacheMutation) =>
        new(
            WorkerJobProtocolV2.TerminalCategory,
            WorkerJobProtocolV2.TerminalType,
            3,
            result.EndedAtUtc,
            jobId ?? JobId,
            jobKind,
            new WorkerJobTerminalPayload(
                WorkerJobTerminalOutcome.Completed,
                result.StartedAtUtc,
                result.EndedAtUtc,
                null,
                null,
                result,
                null));

    private static WorkerJobOutputMessage Progress(
        Guid jobId,
        WorkerJobKind jobKind,
        CacheMutationSource source = CacheMutationSource.Overture,
        string iso3 = "CHE",
        string message = "Validating cache candidate.") =>
        new(
            WorkerJobProtocolV2.ProgressCategory,
            WorkerJobProtocolV2.ProgressChangedType,
            2,
            new DateTimeOffset(2026, 9, 9, 8, 0, 0, TimeSpan.Zero),
            jobId,
            jobKind,
            new CacheMutationProgressPayload(
                CacheMutationProgressStep.ValidatingCandidate,
                source,
                CacheMutationOperation.Refresh,
                iso3,
                message,
                source == CacheMutationSource.Gadm
                    ? new CacheMutationGadmAttribution(
                        CacheMutationGadmAttribution.OfficialDatasetName,
                        "4.1",
                        CacheMutationGadmAttribution.OfficialLicenseUrl,
                        CacheMutationGadmAttribution.NonCommercialUseNotice)
                    : null));

    private static WorkerJobOutputMessage Started(
        Guid jobId,
        WorkerJobKind jobKind = WorkerJobKind.CacheMutation) =>
        new(
            WorkerJobProtocolV2.LifecycleCategory,
            WorkerJobProtocolV2.JobStartedType,
            1,
            new DateTimeOffset(2026, 9, 9, 8, 0, 0, TimeSpan.Zero),
            jobId,
            jobKind,
            new WorkerJobStartedPayload(
                "manual",
                new DateTimeOffset(2026, 9, 9, 8, 0, 0, TimeSpan.Zero)));

    private static WorkerJobOutputMessage ActivityStarted(Guid jobId, Guid activityId) =>
        new(
            WorkerJobProtocolV2.ActivityCategory,
            WorkerJobProtocolV2.ActivityStartedType,
            3,
            new DateTimeOffset(2026, 9, 9, 8, 0, 1, TimeSpan.Zero),
            jobId,
            WorkerJobKind.CacheMutation,
            new WorkerJobActivityStartedPayload(activityId, "Exporting controlled cache"));

    private static WorkerJobOutputMessage ActivityEnded(Guid jobId, Guid activityId) =>
        new(
            WorkerJobProtocolV2.ActivityCategory,
            WorkerJobProtocolV2.ActivityEndedType,
            4,
            new DateTimeOffset(2026, 9, 9, 8, 0, 2, TimeSpan.Zero),
            jobId,
            WorkerJobKind.CacheMutation,
            new WorkerJobActivityEndedPayload(activityId));

    private static WorkerJobOutputMessage Log(Guid jobId) =>
        new(
            WorkerJobProtocolV2.DiagnosticCategory,
            WorkerJobProtocolV2.LogEmittedType,
            5,
            new DateTimeOffset(2026, 9, 9, 8, 0, 3, TimeSpan.Zero),
            jobId,
            WorkerJobKind.CacheMutation,
            new WorkerJobLogPayload("information", "Controlled cache log"));

    private static string CreateTempDir()
    {
        string path = Path.Combine(Path.GetTempPath(), $"cache-controller-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task DrainCleanupAsync(params Func<Task>[] cleanupSteps)
    {
        List<Exception> failures = [];
        foreach (Func<Task> cleanup in cleanupSteps)
        {
            try
            {
                await cleanup().ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                failures.Add(failure);
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("One or more test cleanup steps failed.", failures);
        }
    }

    private sealed class RecordingAdmissionGate(List<string>? ledger = null) : IWorkerJobAdmissionGate
    {
        private readonly List<string> _ledger = ledger ?? [];
        internal WorkerJobDispatch? Dispatch { get; private set; }
        internal RecordingLease? Lease { get; private set; }
        internal WorkerJobAdmissionResult? Next { get; init; }
        internal int Attempts { get; private set; }

        public WorkerJobAdmissionResult TryAdmit(WorkerJobDispatch dispatch)
        {
            Attempts++;
            Dispatch = dispatch;
            if (Next is not null)
            {
                return Next;
            }

            Lease = new RecordingLease(dispatch.Context, dispatch.Descriptor, _ledger);
            return new WorkerJobAdmissionResult.Admitted(Lease);
        }

        public CacheMaintenanceAdmissionResult TryReserveCacheMaintenance(
            CacheMaintenanceRequestOrigin origin) =>
            throw new AssertFailedException("Worker refresh tests do not reserve cache deletion.");
    }

    private sealed class RecordingLease(
        WorkerJobContext context,
        WorkerJobDescriptor descriptor,
        List<string> ledger) : IWorkerJobAdmissionLease
    {
        private Func<Task>? _stop;
        public WorkerJobContext Context { get; } = context;
        public WorkerJobDescriptor Descriptor { get; } = descriptor;
        public bool IsStopRequested { get; private set; }
        internal int DisposeCount { get; private set; }
        internal List<(WorkerJobContext Context, WorkerJobLifecycle Lifecycle, int? ChildProcessId)>
            Advances { get; } = [];
        internal WorkerJobContext? BoundContext { get; private set; }

        public bool TryBindOwnerStop(WorkerJobContext context, Func<Task> requestStopAsync)
        {
            if (!ReferenceEquals(Context, context) || _stop is not null)
            {
                return false;
            }

            BoundContext = context;
            _stop = requestStopAsync;
            return true;
        }

        public bool TryAdvance(
            WorkerJobContext context,
            WorkerJobLifecycle lifecycle,
            int? childProcessId = null)
        {
            if (!ReferenceEquals(Context, context))
            {
                return false;
            }

            IsStopRequested |= lifecycle == WorkerJobLifecycle.Stopping;
            Advances.Add((context, lifecycle, childProcessId));
            return true;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            ledger.Add("lease-dispose");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingWorkerClient(List<string>? ledger = null) : ICacheMutationWorkerClient
    {
        private readonly Channel<FakeWorkerSession> _sessions = Channel.CreateUnbounded<FakeWorkerSession>();
        private readonly Channel<CacheMutationRequest> _startRequests =
            Channel.CreateUnbounded<CacheMutationRequest>();
        private readonly List<string> _ledger = ledger ?? [];
        internal int Starts { get; private set; }
        internal CacheMutationRequest? Request { get; private set; }
        internal bool StartUnavailable { get; init; }
        internal TaskCompletionSource? StartRelease { get; init; }
        internal int? ChildProcessId { get; init; }
        internal FakeWorkerSession? LastSession { get; private set; }
        internal Func<Guid, List<string>, Action<WorkerJobOutputMessage>, FakeWorkerSession>?
            SessionFactory { get; init; }

        public async ValueTask<CacheMutationWorkerStartResult> StartAsync(
            IWorkerJobAdmissionLease admission,
            CacheMutationRequest request,
            IWorkerJobEventSink eventSink,
            CancellationToken cancellationToken)
        {
            Starts++;
            Request = request;
            _startRequests.Writer.TryWrite(request);
            if (StartRelease is not null)
            {
                await StartRelease.Task.ConfigureAwait(false);
            }

            if (StartUnavailable)
            {
                return new CacheMutationWorkerStartResult.Unavailable(
                    "cache-worker-start",
                    "Cache worker unavailable.");
            }

            Action<WorkerJobOutputMessage> emit = message =>
                eventSink.AcceptAsync(message, CancellationToken.None).AsTask()
                    .GetAwaiter().GetResult();
            FakeWorkerSession session = SessionFactory?.Invoke(
                    admission.Context.JobId,
                    _ledger,
                    emit)
                ?? new FakeWorkerSession(admission.Context.JobId, _ledger, emit);
            LastSession = session;
            _sessions.Writer.TryWrite(session);
            return new CacheMutationWorkerStartResult.Started(session, ChildProcessId);
        }

        internal async Task<CacheMutationRequest> WaitForStartAsync() =>
            await _startRequests.Reader.ReadAsync().AsTask().WaitAsync(Bound);

        internal async Task<FakeWorkerSession> WaitForSessionAsync() =>
            await _sessions.Reader.ReadAsync().AsTask().WaitAsync(Bound);
    }

    private sealed class FakeWorkerSession(
        Guid jobId,
        List<string> ledger,
        Action<WorkerJobOutputMessage> emit,
        WorkerJobKind jobKind = WorkerJobKind.CacheMutation,
        InternalWorkerProtocolVersion protocolVersion = InternalWorkerProtocolVersion.V2,
        bool isCancellable = true) : ICacheMutationWorkerSession
    {
        private readonly TaskCompletionSource<CacheMutationWorkerOutcome> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completionObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stopRequested =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _completionReleased;
        public Guid JobId { get; } = jobId;
        public WorkerJobKind JobKind { get; } = jobKind;
        public InternalWorkerProtocolVersion ProtocolVersion { get; } = protocolVersion;
        public bool IsCancellable { get; } = isCancellable;
        public Task<CacheMutationWorkerOutcome> Completion
        {
            get
            {
                _completionObserved.TrySetResult();
                return _completion.Task;
            }
        }

        internal Task CompletionObserved => _completionObserved.Task;
        internal Task StopRequested => _stopRequested.Task;
        internal int StopCount { get; private set; }
        internal int DisposeCount { get; private set; }

        public async Task RequestStopAsync()
        {
            StopCount++;
            _stopRequested.TrySetResult();
            await _completion.Task;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            ledger.Add("session-dispose");
            return ValueTask.CompletedTask;
        }

        internal void Complete(CacheMutationWorkerOutcome outcome)
        {
            if (Interlocked.Exchange(ref _completionReleased, 1) == 0)
            {
                ledger.Add("session-final");
                _completion.TrySetResult(outcome);
            }
        }

        internal void Emit(WorkerJobOutputMessage message) => emit(message);
    }

    private sealed class RecordingCompletionSink(
        List<string>? ledger = null,
        bool throwAfterRecord = false) :
        ICacheMutationCompletionSink
    {
        private readonly List<string> _ledger = ledger ?? [];
        internal int Count { get; private set; }

        public ValueTask CompletedAsync(CacheMutationResult result)
        {
            Count++;
            _ledger.Add("success");
            if (throwAfterRecord)
            {
                throw new InvalidOperationException("controlled completion callback failure");
            }

            return ValueTask.CompletedTask;
        }
    }
}
