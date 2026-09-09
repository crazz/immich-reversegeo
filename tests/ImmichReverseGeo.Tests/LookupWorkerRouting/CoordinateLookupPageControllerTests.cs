using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Services;
using System.Threading.Channels;

namespace ImmichReverseGeo.Tests.LookupWorkerRouting;

[TestClass]
[TestCategory("Change49")]
public sealed class CoordinateLookupPageControllerTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private static readonly Guid FirstJobId =
        Guid.Parse("11111111-2222-3333-4444-555555555555");

    [TestMethod]
    public async Task InvalidCoordinates_StopBeforeIdentityAdmissionAndWorkerLaunch()
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
                return FirstJobId;
            });

        await controller.SubmitAsync(new CoordinateLookupSubmission(
            double.NaN,
            8.5,
            true,
            false,
            false));

        Assert.AreEqual(CoordinateLookupPagePhase.Failed, controller.State.Phase);
        StringAssert.Contains(controller.State.Error!, "Latitude");
        Assert.IsTrue(controller.State.FormControlsEnabled);
        Assert.AreEqual(0, identityCalls);
        Assert.AreEqual(0, admission.Attempts);
        Assert.AreEqual(0, worker.Starts);
    }

    [TestMethod]
    public async Task ValidSubmission_UsesOneExactIdentityAndImmutableRequestSnapshot()
    {
        var admission = new RecordingAdmissionGate();
        var worker = new RecordingWorkerClient();
        await using var controller = Create(admission, worker, () => FirstJobId);
        var submission = new CoordinateLookupSubmission(
            47.4,
            8.5,
            true,
            true,
            true);

        Task run = controller.SubmitAsync(submission);
        FakeWorkerSession session = await worker.WaitForSessionAsync();
        CoordinateLookupRequest request = worker.Request!;
        try
        {
            Assert.AreEqual(FirstJobId, admission.Dispatch!.Context.JobId);
            Assert.AreEqual(FirstJobId, session.JobId);
            Assert.AreEqual("11111111-2222-3333-4444-555555555555", controller.State.JobId);
            Assert.AreEqual(47.4, request.Latitude);
            Assert.AreEqual(8.5, request.Longitude);
            Assert.IsTrue(request.IncludeAirportInfrastructure);
            Assert.IsTrue(request.IncludeLiveOverturePlaces);
            Assert.IsTrue(request.PreferGadmAdministrativeAreas);
            CollectionAssert.AreEqual(
                new[] { "DNK", "USA" },
                request.CityResolverOverrides.CountryProfiles
                    .Select(static profile => profile.CountryCode)
                    .ToArray());
        }
        finally
        {
            session.Complete(new CoordinateLookupWorkerOutcome.Completed(Result(worker.Request!)));
            await run.WaitAsync(Bound);
        }

        Assert.AreEqual(CoordinateLookupPagePhase.Completed, controller.State.Phase);
        Assert.AreSame(request, controller.State.Result!.Request);
        Assert.AreEqual(1, admission.Lease!.DisposeCount);
        Assert.AreEqual(1, session.DisposeCount);
    }

    [TestMethod]
    public async Task ImmediateTerminalBeforeStartReturns_DoesNotUnlockOrLoseAuthoritativeResult()
    {
        var admission = new RecordingAdmissionGate();
        var worker = new RecordingWorkerClient { EmitTerminalBeforeReturn = true };
        await using var controller = Create(admission, worker, () => FirstJobId);

        Task first = controller.SubmitAsync(Submission());
        FakeWorkerSession session = await worker.WaitForSessionAsync();
        try
        {
            Assert.IsTrue(controller.State.TerminalObserved);
            Assert.IsFalse(controller.State.FormControlsEnabled);
            Assert.IsFalse(controller.State.CanCancel);
            await controller.CancelAsync();
            await controller.SubmitAsync(Submission());
            Assert.AreEqual(0, session.StopCount);
            Assert.AreEqual(1, worker.Starts);
            Assert.AreEqual(0, admission.Lease!.DisposeCount);
        }
        finally
        {
            session.Complete(new CoordinateLookupWorkerOutcome.Completed(Result(worker.Request!)));
            await first.WaitAsync(Bound);
        }

        Assert.AreEqual(CoordinateLookupPagePhase.Completed, controller.State.Phase);
        Assert.IsNotNull(controller.State.Result);
        Assert.IsTrue(controller.State.FormControlsEnabled);
        Assert.AreEqual(1, admission.Lease.DisposeCount);
    }

    [TestMethod]
    public async Task BusyAttempt_StartsNoWorkerAndRetainsLastCompletedResult()
    {
        var admission = new RecordingAdmissionGate();
        var worker = new RecordingWorkerClient();
        await using var controller = Create(admission, worker, () => FirstJobId);
        Task first = controller.SubmitAsync(Submission());
        FakeWorkerSession firstSession = await worker.WaitForSessionAsync();
        firstSession.Complete(new CoordinateLookupWorkerOutcome.Completed(Result(worker.Request!)));
        await first.WaitAsync(Bound);
        CoordinateLookupResult completed = controller.State.Result!;

        admission.Next = new WorkerJobAdmissionResult.Busy(new WorkerJobBusyMetadata(
            WorkerJobCapabilityFamily.Processing,
            WorkerJobRequestOrigin.Scheduled,
            true));
        await controller.SubmitAsync(Submission());

        Assert.AreEqual(CoordinateLookupPagePhase.Busy, controller.State.Phase);
        Assert.AreSame(completed, controller.State.Result);
        Assert.IsTrue(controller.State.ResultIsFromLastCompletedLookup);
        StringAssert.Contains(controller.State.Status, "another background job");
        Assert.AreEqual(1, worker.Starts);
    }

    [TestMethod]
    public async Task InvalidAttempt_RetainsLastCompletedResultWithoutNewIdentityOrAdmission()
    {
        var admission = new RecordingAdmissionGate();
        var worker = new RecordingWorkerClient();
        var identities = 0;
        await using var controller = Create(
            admission,
            worker,
            () =>
            {
                identities++;
                return FirstJobId;
            });
        Task first = controller.SubmitAsync(Submission());
        FakeWorkerSession session = await worker.WaitForSessionAsync();
        session.Complete(new CoordinateLookupWorkerOutcome.Completed(Result(worker.Request!)));
        await first.WaitAsync(Bound);
        CoordinateLookupResult completed = controller.State.Result!;

        await controller.SubmitAsync(new CoordinateLookupSubmission(
            91,
            8.5,
            true,
            false,
            false));

        Assert.AreEqual(CoordinateLookupPagePhase.Failed, controller.State.Phase);
        Assert.AreSame(completed, controller.State.Result);
        Assert.IsTrue(controller.State.ResultIsFromLastCompletedLookup);
        Assert.AreEqual(1, identities);
        Assert.AreEqual(1, admission.Attempts);
        Assert.AreEqual(1, worker.Starts);
    }

    [TestMethod]
    public async Task WorkerUnavailable_ReleasesAdmissionAndReturnsSafeRetryableState()
    {
        var admission = new RecordingAdmissionGate();
        string unavailableMessage = string.Concat(
            "The isolated lookup worker is unavailable. ",
            new string('x', WorkerJobProtocolV2.MaxSafeTextLength));
        var worker = new RecordingWorkerClient
        {
            StartUnavailable = true,
            StartUnavailableMessage = unavailableMessage
        };
        await using var controller = Create(admission, worker, () => FirstJobId);

        await controller.SubmitAsync(Submission());

        Assert.AreEqual(CoordinateLookupPagePhase.Unavailable, controller.State.Phase);
        StringAssert.Contains(controller.State.Error!, "isolated lookup worker");
        Assert.AreEqual(WorkerJobProtocolV2.MaxSafeTextLength, controller.State.Error.Length);
        StringAssert.EndsWith(controller.State.Error, "…");
        Assert.IsTrue(controller.State.FormControlsEnabled);
        Assert.AreEqual(1, worker.Starts);
        Assert.AreEqual(1, admission.Lease!.DisposeCount);
    }

    [TestMethod]
    public async Task RepeatedCancelAndDispose_JoinOneStopAndSuppressLateCallbacks()
    {
        var admission = new RecordingAdmissionGate();
        var worker = new RecordingWorkerClient();
        var notifications = 0;
        var controller = Create(
            admission,
            worker,
            () => FirstJobId,
            () => notifications++);
        Task run = controller.SubmitAsync(Submission());
        FakeWorkerSession session = await worker.WaitForSessionAsync();
        int beforeDispose = notifications;

        Task firstCancel = controller.CancelAsync();
        Task secondCancel = controller.CancelAsync();
        ValueTask dispose = controller.DisposeAsync();
        try
        {
            session.Emit(Progress(FirstJobId, "late"));
        }
        finally
        {
            session.Complete(new CoordinateLookupWorkerOutcome.Cancelled());
            await Task.WhenAll(firstCancel, secondCancel, run, dispose.AsTask())
                .WaitAsync(Bound);
        }
        Assert.AreEqual(1, session.StopCount);
        Assert.AreEqual(1, session.DisposeCount);
        Assert.AreEqual(1, admission.Lease!.DisposeCount);
        Assert.AreEqual(beforeDispose + 1, notifications);
    }

    [TestMethod]
    public async Task DisposeWhileStartIsPending_JoinsLateSessionAndReleasesOwnershipOnce()
    {
        var admission = new RecordingAdmissionGate();
        var worker = new RecordingWorkerClient { HoldStartReturn = true };
        var controller = Create(admission, worker, () => FirstJobId);
        Task run = controller.SubmitAsync(Submission());
        FakeWorkerSession session = await worker.WaitForSessionAsync();

        Task dispose = controller.DisposeAsync().AsTask();
        try
        {
            Assert.IsFalse(dispose.IsCompleted);
            Assert.AreEqual(0, admission.Lease!.DisposeCount);

            worker.ReleaseStart();
            await session.StopRequested.WaitAsync(Bound);
            Assert.IsFalse(dispose.IsCompleted);
        }
        finally
        {
            worker.ReleaseStart();
            session.Complete(new CoordinateLookupWorkerOutcome.Cancelled());
            await Task.WhenAll(run, dispose).WaitAsync(Bound);
        }
        Assert.AreEqual(1, session.StopCount);
        Assert.AreEqual(1, session.DisposeCount);
        Assert.AreEqual(1, admission.Lease.DisposeCount);
    }

    [TestMethod]
    public async Task CancelWhileStartIsPending_JoinsLateSessionAndReleasesOwnershipOnce()
    {
        var admission = new RecordingAdmissionGate();
        var worker = new RecordingWorkerClient { HoldStartReturn = true };
        await using var controller = Create(admission, worker, () => FirstJobId);
        Task run = controller.SubmitAsync(Submission());
        FakeWorkerSession session = await worker.WaitForSessionAsync();

        Task cancel = controller.CancelAsync();
        try
        {
            Assert.AreEqual(CoordinateLookupPagePhase.CancelRequested, controller.State.Phase);
            Assert.IsFalse(cancel.IsCompleted);
            worker.ReleaseStart();
            await session.StopRequested.WaitAsync(Bound);
            Assert.IsFalse(run.IsCompleted);
        }
        finally
        {
            worker.ReleaseStart();
            session.Complete(new CoordinateLookupWorkerOutcome.Cancelled());
            await Task.WhenAll(run, cancel).WaitAsync(Bound);
        }

        Assert.AreEqual(CoordinateLookupPagePhase.Cancelled, controller.State.Phase);
        Assert.AreEqual(1, session.StopCount);
        Assert.AreEqual(1, session.DisposeCount);
        Assert.AreEqual(1, admission.Lease!.DisposeCount);
    }

    [TestMethod]
    public async Task CancelRequested_RemainsMonotonicAcrossLateNonTerminalEventsUntilCompletedFinalityWins()
    {
        var admission = new RecordingAdmissionGate();
        var worker = new RecordingWorkerClient { HoldStartReturn = true };
        await using var controller = Create(admission, worker, () => FirstJobId);
        Task run = controller.SubmitAsync(Submission());
        FakeWorkerSession session = await worker.WaitForSessionAsync();
        Guid activityId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        Task cancel = controller.CancelAsync();
        try
        {
            AssertCancelling(controller.State);
            session.Emit(Ready());
            AssertCancelling(controller.State);
            session.Emit(Started(FirstJobId));
            AssertCancelling(controller.State);
            session.Emit(Progress(FirstJobId, "late progress"));
            AssertCancelling(controller.State);
            Assert.AreEqual(CoordinateLookupProgressStep.Country, controller.State.CurrentStep);
            session.Emit(ActivityStarted(FirstJobId, activityId));
            AssertCancelling(controller.State);
            Assert.AreEqual("Lookup cache activity", controller.State.CurrentActivity);
            session.Emit(Log(FirstJobId, "late warning"));
            AssertCancelling(controller.State);
            session.Emit(ActivityEnded(FirstJobId, activityId));
            AssertCancelling(controller.State);
            Assert.IsNull(controller.State.CurrentActivity);

            CoordinateLookupResult completed = Result(worker.Request!);
            session.Emit(Terminal(FirstJobId, completed));
            Assert.IsTrue(controller.State.TerminalObserved);
            Assert.IsFalse(controller.State.FormControlsEnabled);
            Assert.IsFalse(controller.State.CanCancel);
            Assert.AreEqual(0, admission.Lease!.DisposeCount);
            worker.ReleaseStart();
            await session.StopRequested.WaitAsync(Bound);
            session.Complete(new CoordinateLookupWorkerOutcome.Completed(completed));
            await Task.WhenAll(run, cancel).WaitAsync(Bound);
        }
        finally
        {
            worker.ReleaseStart();
            session.Complete(new CoordinateLookupWorkerOutcome.Cancelled());
        }

        Assert.AreEqual(CoordinateLookupPagePhase.Completed, controller.State.Phase);
        Assert.IsNotNull(controller.State.Result);
        Assert.IsTrue(controller.State.FormControlsEnabled);
        Assert.AreEqual(1, session.StopCount);
        Assert.AreEqual(1, session.DisposeCount);
        Assert.AreEqual(1, admission.Lease!.DisposeCount);
    }

    private static void AssertCancelling(CoordinateLookupPageState state)
    {
        Assert.AreEqual(CoordinateLookupPagePhase.CancelRequested, state.Phase);
        Assert.AreEqual("Cancelling lookup…", state.Status);
        Assert.IsFalse(state.CanCancel);
        Assert.IsFalse(state.FormControlsEnabled);
    }

    [TestMethod]
    public async Task HostStop_JoinsTheSamePendingSessionFinalityAndCleanupPath()
    {
        var admission = new RecordingAdmissionGate();
        var worker = new RecordingWorkerClient { HoldStartReturn = true };
        var lifetime = new CoordinateLookupPageControllerHostLifetime();
        var controller = new CoordinateLookupPageController(
            admission,
            worker,
            new SettingsProvider(),
            () => FirstJobId,
            () => { },
            lifetime.Unregister);
        Assert.IsTrue(lifetime.Register(controller));
        Task run = controller.SubmitAsync(Submission());
        FakeWorkerSession session = await worker.WaitForSessionAsync();

        Task stop = lifetime.StopAsync(CancellationToken.None);
        try
        {
            Assert.IsFalse(stop.IsCompleted);
            worker.ReleaseStart();
            await session.StopRequested.WaitAsync(Bound);
            Assert.IsFalse(stop.IsCompleted);
        }
        finally
        {
            worker.ReleaseStart();
            session.Complete(new CoordinateLookupWorkerOutcome.Cancelled());
            await Task.WhenAll(run, stop, controller.DisposeAsync().AsTask())
                .WaitAsync(Bound);
        }
        Assert.AreEqual(1, session.StopCount);
        Assert.AreEqual(1, session.DisposeCount);
        Assert.AreEqual(1, admission.Lease!.DisposeCount);
        Assert.IsFalse(lifetime.Register(Create(admission, worker, Guid.NewGuid)));
    }

    [TestMethod]
    [DataRow("job")]
    [DataRow("kind")]
    [DataRow("version")]
    public async Task WrongSessionIdentity_RequestsStopThenFailsReleasesAndAllowsReuse(
        string mismatch)
    {
        await using var admission = new TemporaryCoordinateLookupAdmissionGate();
        var worker = new RecordingWorkerClient
        {
            SessionJobId = mismatch == "job" ? Guid.NewGuid() : null,
            SessionJobKind = mismatch == "kind"
                ? WorkerJobKind.ProcessAssets
                : WorkerJobKind.CoordinateLookup,
            SessionProtocolVersion = mismatch == "version"
                ? InternalWorkerProtocolVersion.V1
                : InternalWorkerProtocolVersion.V2
        };
        var controller = new CoordinateLookupPageController(
            admission,
            worker,
            new SettingsProvider(),
            () => FirstJobId,
            () => { });
        Task run = controller.SubmitAsync(Submission());
        FakeWorkerSession session = await worker.WaitForSessionAsync();
        try
        {
            await session.StopRequested.WaitAsync(Bound);
            Assert.IsFalse(run.IsCompleted);
            session.Complete(new CoordinateLookupWorkerOutcome.Cancelled());
            await run.WaitAsync(Bound);
        }
        finally
        {
            session.Complete(new CoordinateLookupWorkerOutcome.Cancelled());
            if (run.IsCompleted)
            {
                await controller.DisposeAsync();
            }
        }

        Assert.AreEqual(CoordinateLookupPagePhase.Failed, controller.State.Phase);
        StringAssert.Contains(controller.State.Error!, "lookup-correlation");
        Assert.AreEqual(1, session.StopCount);
        Assert.AreEqual(1, session.DisposeCount);

        WorkerJobAdmissionResult reuse = admission.TryAdmit(
            new CoordinateLookupWorkerJobDispatch(Guid.NewGuid(), Request()));
        var admitted = Assert.IsInstanceOfType<WorkerJobAdmissionResult.Admitted>(reuse);
        await admitted.Lease.DisposeAsync();
    }

    [TestMethod]
    public async Task TemporaryGate_IsAtomicExactOwnerOnlyAndReusable()
    {
        await using var gate = new TemporaryCoordinateLookupAdmissionGate();
        var first = new CoordinateLookupWorkerJobDispatch(FirstJobId, Request());
        var second = new CoordinateLookupWorkerJobDispatch(Guid.NewGuid(), Request());

        var admitted = Assert.IsInstanceOfType<WorkerJobAdmissionResult.Admitted>(
            gate.TryAdmit(first));
        WorkerJobAdmissionResult.Busy busy =
            Assert.IsInstanceOfType<WorkerJobAdmissionResult.Busy>(gate.TryAdmit(second));
        Assert.AreEqual(WorkerJobCapabilityFamily.Lookup, busy.ActiveJob.CapabilityFamily);

        await admitted.Lease.DisposeAsync();
        await admitted.Lease.DisposeAsync();
        var reused = Assert.IsInstanceOfType<WorkerJobAdmissionResult.Admitted>(
            gate.TryAdmit(second));
        Assert.AreEqual(second.Context.JobId, reused.Lease.Context.JobId);
        await reused.Lease.DisposeAsync();
    }

    [TestMethod]
    public async Task PageFactory_CreatesFreshControllerAndOldGenerationEventsCannotMutateIt()
    {
        await using var admission = new TemporaryCoordinateLookupAdmissionGate();
        var worker = new RecordingWorkerClient();
        var lifetime = new CoordinateLookupPageControllerHostLifetime();
        var factory = new CoordinateLookupPageControllerFactory(
            admission,
            worker,
            new SettingsProvider(),
            lifetime);
        var processing = new ProcessingState();
        processing.StartRun(9);
        processing.IncrementProcessed();
        processing.SetActivity("Processing an Immich asset");
        string processingBefore = ProcessingProjection(processing);

        var first = factory.Create(() => { });
        Task firstRun = first.SubmitAsync(Submission());
        FakeWorkerSession firstSession = await worker.WaitForSessionAsync();
        Guid activityId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        try
        {
            firstSession.Emit(Progress(firstSession.JobId, "Lookup progress."));
            firstSession.Emit(ActivityStarted(firstSession.JobId, activityId));
            firstSession.Emit(Log(firstSession.JobId, "Lookup diagnostic."));
            firstSession.Emit(Terminal(
                firstSession.JobId,
                Result(worker.Request!)));
            firstSession.Complete(new CoordinateLookupWorkerOutcome.Completed(
                Result(worker.Request!)));
            await firstRun.WaitAsync(Bound);
        }
        finally
        {
            firstSession.Complete(new CoordinateLookupWorkerOutcome.Cancelled());
            await first.DisposeAsync();
        }

        Assert.AreEqual(processingBefore, ProcessingProjection(processing));

        var second = factory.Create(() => { });
        Assert.AreNotSame(first, second);
        Task secondRun = second.SubmitAsync(Submission());
        FakeWorkerSession secondSession = await worker.WaitForSessionAsync();
        try
        {
            CoordinateLookupPagePhase phaseBefore = second.State.Phase;
            string statusBefore = second.State.Status;
            firstSession.Emit(Progress(firstSession.JobId, "stale callback"));
            Assert.AreEqual(phaseBefore, second.State.Phase);
            Assert.AreEqual(statusBefore, second.State.Status);

            secondSession.Complete(new CoordinateLookupWorkerOutcome.Completed(
                Result(worker.Request!)));
            await secondRun.WaitAsync(Bound);
        }
        finally
        {
            secondSession.Complete(new CoordinateLookupWorkerOutcome.Cancelled());
            await second.DisposeAsync();
        }

        Assert.AreEqual(processingBefore, ProcessingProjection(processing));
    }

    private static CoordinateLookupPageController Create(
        RecordingAdmissionGate admission,
        RecordingWorkerClient worker,
        Func<Guid> identity,
        Action? changed = null)
    {
        return new CoordinateLookupPageController(
            admission,
            worker,
            new SettingsProvider(),
            identity,
            changed ?? (() => { }));
    }

    private static CoordinateLookupSubmission Submission()
    {
        return new CoordinateLookupSubmission(47.4, 8.5, true, false, false);
    }

    private static CoordinateLookupRequest Request()
    {
        return new CoordinateLookupRequest(
            47.4,
            8.5,
            true,
            false,
            false,
            new CoordinateLookupCityResolverOverrides(null, []));
    }

    private static CoordinateLookupResult Result(CoordinateLookupRequest request)
    {
        DateTimeOffset started = new(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);
        CoordinateLookupSourceResult skipped = new(
            CoordinateLookupSourceState.Skipped,
            null,
            null,
            null,
            [],
            [],
            null,
            null,
            null,
            null);
        return new CoordinateLookupResult(
            request,
            started,
            started.AddSeconds(1),
            new CoordinateLookupCountryResult(
                CoordinateLookupCountryStatus.NoMatch,
                null,
                null,
                null,
                null,
                null),
            skipped,
            skipped,
            skipped,
            skipped,
            new CoordinateLookupAdministrativeResult(null, null),
            new CoordinateLookupAdministrativeResult(null, null),
            new CoordinateLookupProfileSummary(
                null,
                ["locality"],
                CoordinateLookupTieBreak.SmallestArea),
            ["No country matched."],
            new CoordinateLookupFinalLocation(null, null, null));
    }

    private static WorkerJobOutputMessage Progress(Guid jobId, string message)
    {
        return new WorkerJobOutputMessage(
            WorkerJobProtocolV2.ProgressCategory,
            WorkerJobProtocolV2.ProgressChangedType,
            3,
            new DateTimeOffset(2026, 9, 9, 0, 0, 1, TimeSpan.Zero),
            jobId,
            WorkerJobKind.CoordinateLookup,
            new CoordinateLookupProgressPayload(
                CoordinateLookupProgressStep.Country,
                CoordinateLookupSourceState.Ready,
                "USA",
                message));
    }

    private static WorkerJobOutputMessage Ready() =>
        new(
            WorkerJobProtocolV2.LifecycleCategory,
            WorkerJobProtocolV2.ReadyType,
            1,
            new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero),
            null,
            null,
            new WorkerJobReadyPayload([WorkerJobKind.CoordinateLookup]));

    private static WorkerJobOutputMessage Started(Guid jobId) =>
        new(
            WorkerJobProtocolV2.LifecycleCategory,
            WorkerJobProtocolV2.JobStartedType,
            2,
            new DateTimeOffset(2026, 9, 9, 0, 0, 1, TimeSpan.Zero),
            jobId,
            WorkerJobKind.CoordinateLookup,
            new WorkerJobStartedPayload(
                "manual",
                new DateTimeOffset(2026, 9, 9, 0, 0, 1, TimeSpan.Zero)));

    private static WorkerJobOutputMessage ActivityStarted(Guid jobId, Guid activityId) =>
        new(
            WorkerJobProtocolV2.ActivityCategory,
            WorkerJobProtocolV2.ActivityStartedType,
            4,
            new DateTimeOffset(2026, 9, 9, 0, 0, 2, TimeSpan.Zero),
            jobId,
            WorkerJobKind.CoordinateLookup,
            new WorkerJobActivityStartedPayload(activityId, "Lookup cache activity"));

    private static WorkerJobOutputMessage ActivityEnded(Guid jobId, Guid activityId) =>
        new(
            WorkerJobProtocolV2.ActivityCategory,
            WorkerJobProtocolV2.ActivityEndedType,
            5,
            new DateTimeOffset(2026, 9, 9, 0, 0, 3, TimeSpan.Zero),
            jobId,
            WorkerJobKind.CoordinateLookup,
            new WorkerJobActivityEndedPayload(activityId));

    private static WorkerJobOutputMessage Log(Guid jobId, string message) =>
        new(
            WorkerJobProtocolV2.DiagnosticCategory,
            WorkerJobProtocolV2.LogEmittedType,
            5,
            new DateTimeOffset(2026, 9, 9, 0, 0, 3, TimeSpan.Zero),
            jobId,
            WorkerJobKind.CoordinateLookup,
            new WorkerJobLogPayload("warning", message));

    private static WorkerJobOutputMessage Terminal(
        Guid jobId,
        CoordinateLookupResult result) =>
        new(
            WorkerJobProtocolV2.TerminalCategory,
            WorkerJobProtocolV2.TerminalType,
            6,
            result.EndedAtUtc,
            jobId,
            WorkerJobKind.CoordinateLookup,
            new WorkerJobTerminalPayload(
                WorkerJobTerminalOutcome.Completed,
                result.StartedAtUtc,
                result.EndedAtUtc,
                null,
                result,
                null));

    private static string ProcessingProjection(ProcessingState state) =>
        string.Join(
            "|",
            state.IsRunning,
            state.TotalUnprocessed,
            state.ProcessedThisRun,
            state.ErrorsThisRun,
            state.SkippedThisRun,
            state.CurrentActivity,
            state.LastError,
            string.Join("\n", state.GetRecentLog()));

    private sealed class SettingsProvider : ICoordinateLookupSettingsSnapshotProvider
    {
        public ValueTask<CoordinateLookupCityResolverOverrides> GetAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new CoordinateLookupCityResolverOverrides(
                new CoordinateLookupCityProfile(
                    ["locality"],
                    CoordinateLookupTieBreak.SmallestArea),
                [
                    new CoordinateLookupCountryProfile(
                        "DNK",
                        new CoordinateLookupCityProfile(
                            ["locality"],
                            CoordinateLookupTieBreak.SmallestArea)),
                    new CoordinateLookupCountryProfile(
                        "USA",
                        new CoordinateLookupCityProfile(
                            ["locality"],
                            CoordinateLookupTieBreak.SmallestArea))
                ]));
        }
    }

    private sealed class RecordingAdmissionGate : IWorkerJobAdmissionGate
    {
        internal int Attempts { get; private set; }
        internal CoordinateLookupWorkerJobDispatch? Dispatch { get; private set; }
        internal RecordingLease? Lease { get; private set; }
        internal WorkerJobAdmissionResult? Next { get; set; }

        public WorkerJobAdmissionResult TryAdmit(WorkerJobDispatch dispatch)
        {
            Attempts++;
            Dispatch = Assert.IsInstanceOfType<CoordinateLookupWorkerJobDispatch>(dispatch);
            if (Next is { } next)
            {
                Next = null;
                return next;
            }

            Lease = new RecordingLease(dispatch.Context, dispatch.Descriptor);
            return new WorkerJobAdmissionResult.Admitted(Lease);
        }
    }

    private sealed class RecordingLease(
        WorkerJobContext context,
        WorkerJobDescriptor descriptor) : IWorkerJobAdmissionLease
    {
        internal int DisposeCount { get; private set; }
        public WorkerJobContext Context { get; } = context;
        public WorkerJobDescriptor Descriptor { get; } = descriptor;

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingWorkerClient : ICoordinateLookupWorkerClient
    {
        private readonly Channel<FakeWorkerSession> _started =
            Channel.CreateUnbounded<FakeWorkerSession>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
        private readonly TaskCompletionSource _startRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool EmitTerminalBeforeReturn { get; init; }
        internal bool HoldStartReturn { get; init; }
        internal bool StartUnavailable { get; init; }
        internal string StartUnavailableMessage { get; init; } =
            "The isolated lookup worker could not be started.";
        internal Guid? SessionJobId { get; init; }
        internal WorkerJobKind SessionJobKind { get; init; } =
            WorkerJobKind.CoordinateLookup;
        internal InternalWorkerProtocolVersion SessionProtocolVersion { get; init; } =
            InternalWorkerProtocolVersion.V2;
        internal int Starts { get; private set; }
        internal CoordinateLookupRequest? Request { get; private set; }

        public async ValueTask<CoordinateLookupWorkerStartResult> StartAsync(
            IWorkerJobAdmissionLease admission,
            CoordinateLookupRequest request,
            IWorkerJobEventSink eventSink,
            CancellationToken cancellationToken)
        {
            Starts++;
            Request = request;
            if (StartUnavailable)
            {
                return new CoordinateLookupWorkerStartResult.Unavailable(
                    "lookup-worker-start",
                    StartUnavailableMessage);
            }

            var session = new FakeWorkerSession(
                SessionJobId ?? admission.Context.JobId,
                SessionJobKind,
                SessionProtocolVersion,
                message => eventSink.AcceptAsync(message, CancellationToken.None)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult());
            if (EmitTerminalBeforeReturn)
            {
                await eventSink.AcceptAsync(
                    Terminal(admission.Context.JobId, Result(request)),
                    CancellationToken.None);
            }

            Assert.IsTrue(_started.Writer.TryWrite(session));
            if (HoldStartReturn)
            {
                await _startRelease.Task;
            }

            return new CoordinateLookupWorkerStartResult.Started(session);
        }

        internal async Task<FakeWorkerSession> WaitForSessionAsync()
        {
            return await _started.Reader.ReadAsync().AsTask().WaitAsync(Bound);
        }

        internal void ReleaseStart()
        {
            _startRelease.TrySetResult();
        }

        private static WorkerJobOutputMessage Terminal(
            Guid jobId,
            CoordinateLookupResult result)
        {
            return new WorkerJobOutputMessage(
                WorkerJobProtocolV2.TerminalCategory,
                WorkerJobProtocolV2.TerminalType,
                3,
                result.EndedAtUtc,
                jobId,
                WorkerJobKind.CoordinateLookup,
                new WorkerJobTerminalPayload(
                    WorkerJobTerminalOutcome.Completed,
                    result.StartedAtUtc,
                    result.EndedAtUtc,
                    null,
                    result,
                    null));
        }
    }

    private sealed class FakeWorkerSession(
        Guid jobId,
        WorkerJobKind jobKind,
        InternalWorkerProtocolVersion protocolVersion,
        Action<WorkerJobOutputMessage> emit) : ICoordinateLookupWorkerSession
    {
        private readonly TaskCompletionSource<CoordinateLookupWorkerOutcome> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stopRequested =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Guid JobId { get; } = jobId;
        public WorkerJobKind JobKind { get; } = jobKind;
        public InternalWorkerProtocolVersion ProtocolVersion { get; } = protocolVersion;
        public bool IsCancellable => true;
        public Task<CoordinateLookupWorkerOutcome> Completion => _completion.Task;
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
            return ValueTask.CompletedTask;
        }

        internal void Complete(CoordinateLookupWorkerOutcome outcome)
        {
            _completion.TrySetResult(outcome);
        }

        internal void Emit(WorkerJobOutputMessage message)
        {
            emit(message);
        }
    }
}
