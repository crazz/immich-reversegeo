using System.Text;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerEventStateBridge;
using ImmichReverseGeo.Web.WorkerFailureRecovery;

namespace ImmichReverseGeo.Tests.ChildWorkerLaunching;

public sealed partial class ChildWorkerLaunchingTests
{
    private static readonly TimeSpan VersionParityBound = TimeSpan.FromSeconds(5);

    [TestMethod]
    [TestCategory("Change47")]
    [DataRow(1)]
    [DataRow(2)]
    public async Task VersionParity_CompletedTerminalWinsContradictoryExit(int versionValue)
    {
        InternalWorkerProtocolVersion version = Version(versionValue);
        await using VersionParityFixture fixture = await CreateVersionParityFixtureAsync(
            version,
            9100 + versionValue);
        IReadOnlyList<byte[]> frames = VersionedFrames(
            version,
            fixture.Request,
            ProcessingRunOutcome.Completed,
            includeTerminal: true);

        fixture.Process.StandardOutput.Write(frames[0]);
        Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyAccepted>(
            await fixture.Session.Startup.WaitAsync(VersionParityBound),
            $"v{versionValue}-parity-ready");
        AssertExecuteFrame(version, fixture.Request, fixture.Process.StandardInput.ToArray());
        foreach (byte[] frame in frames.Skip(1))
        {
            fixture.Process.StandardOutput.Write(frame);
        }

        fixture.Process.StandardOutput.Complete();
        fixture.Process.StandardError.Complete();
        fixture.Process.Exit(19);

        ProcessingRunResult result = await fixture.Result.WaitAsync(VersionParityBound);
        Assert.AreEqual(ProcessingRunOutcome.Completed, result.Outcome, $"v{versionValue}-parity-outcome");
        Assert.AreEqual(2, result.ProcessedCount, $"v{versionValue}-parity-processed");
        Assert.AreEqual(1, result.UpdatedCount, $"v{versionValue}-parity-updated");
        Assert.AreEqual(1, result.FailedCount, $"v{versionValue}-parity-handled-failed");
        Assert.AreEqual(1, fixture.State.ProcessedThisRun, $"v{versionValue}-parity-state-updated");
        Assert.AreEqual(1, fixture.State.ErrorsThisRun, $"v{versionValue}-parity-state-failed");
        Assert.AreEqual(
            WorkerRunAuthority.CommittedReceipt,
            fixture.Finalizer.Decision?.Authority,
            $"v{versionValue}-parity-terminal-authority");
        Assert.IsTrue(
            fixture.Finalizer.Decision?.Anomalies.HasFlag(
                WorkerRunAnomaly.TerminalExitMismatch) == true,
            $"v{versionValue}-parity-contradictory-exit-anomaly");
        Assert.AreEqual(
            version,
            fixture.Finalizer.Evidence?.ProtocolVersion,
            $"v{versionValue}-parity-evidence-version");
        Assert.AreEqual(
            fixture.Request.RunId,
            fixture.Finalizer.Evidence?.JobId,
            $"v{versionValue}-parity-evidence-job-id");
        Assert.AreEqual(fixture.Request.RunId, fixture.Session.JobId, $"v{versionValue}-parity-session-job-id");
        Assert.AreEqual(fixture.Session.JobId, fixture.Session.RunId, $"v{versionValue}-parity-single-identity-alias");
        Assert.AreEqual(WorkerJobKind.ProcessAssets, fixture.Session.JobKind, $"v{versionValue}-parity-job-kind");
        Assert.IsTrue(fixture.Session.IsCancellable, $"v{versionValue}-parity-descriptor-cancellable");
        Assert.IsNull(fixture.State.CurrentActivity, $"v{versionValue}-parity-activity-closed");
        Assert.IsTrue(
            fixture.State.GetRecentLog().Any(line => line.EndsWith("parity-log", StringComparison.Ordinal)),
            $"v{versionValue}-parity-log-projected");
        AssertFinality(fixture, versionValue);
    }

    [TestMethod]
    [TestCategory("Change47")]
    [DataRow(1)]
    [DataRow(2)]
    public async Task VersionParity_CooperativeStopUsesExactIdentityAndCommittedCancellation(int versionValue)
    {
        InternalWorkerProtocolVersion version = Version(versionValue);
        await using VersionParityFixture fixture = await CreateVersionParityFixtureAsync(
            version,
            9200 + versionValue);
        IReadOnlyList<byte[]> frames = VersionedFrames(
            version,
            fixture.Request,
            ProcessingRunOutcome.Cancelled,
            includeTerminal: true);

        fixture.Process.StandardOutput.Write(frames[0]);
        await fixture.Session.Startup.WaitAsync(VersionParityBound);
        foreach (byte[] frame in frames.Skip(1).SkipLast(1))
        {
            fixture.Process.StandardOutput.Write(frame);
        }

        Task<ChildWorkerCancellationResult> stop = fixture.Session.RequestStop();
        await fixture.Session.WaitForCancellationDeliveryAsync().WaitAsync(VersionParityBound);
        AssertControllerInput(version, fixture.Request, fixture.Process.StandardInput.ToArray());
        fixture.Process.StandardOutput.Write(frames[^1]);
        fixture.Process.StandardOutput.Complete();
        fixture.Process.StandardError.Complete();
        fixture.Process.Exit(130);

        ChildWorkerCancellationResult cancellation = await stop.WaitAsync(VersionParityBound);
        ProcessingRunResult result = await fixture.Result.WaitAsync(VersionParityBound);
        Assert.IsTrue(cancellation.Facts.RequestAccepted, $"v{versionValue}-parity-cancel-accepted");
        Assert.AreEqual(
            ChildWorkerCancelDeliveryPhase.Flushed,
            cancellation.Facts.DeliveryPhase,
            $"v{versionValue}-parity-cancel-flushed");
        Assert.AreEqual(ProcessingRunOutcome.Cancelled, result.Outcome, $"v{versionValue}-parity-cancelled");
        Assert.AreEqual(2, result.ProcessedCount, $"v{versionValue}-parity-cancel-processed");
        Assert.AreEqual(1, result.UpdatedCount, $"v{versionValue}-parity-cancel-updated");
        Assert.AreEqual(1, result.FailedCount, $"v{versionValue}-parity-cancel-handled-failed");
        Assert.AreEqual(
            WorkerRunAuthority.CommittedReceipt,
            fixture.Finalizer.Decision?.Authority,
            $"v{versionValue}-parity-cancel-terminal-authority");
        Assert.IsNull(fixture.State.CurrentActivity, $"v{versionValue}-parity-cancel-activity-closed");
        AssertFinality(fixture, versionValue);
    }

    [TestMethod]
    [TestCategory("Change47")]
    [DataRow(1)]
    [DataRow(2)]
    public async Task VersionParity_CrashWithoutTerminalUsesSameClassifierFinality(int versionValue)
    {
        InternalWorkerProtocolVersion version = Version(versionValue);
        await using VersionParityFixture fixture = await CreateVersionParityFixtureAsync(
            version,
            9300 + versionValue);
        IReadOnlyList<byte[]> frames = VersionedFrames(
            version,
            fixture.Request,
            ProcessingRunOutcome.Completed,
            includeTerminal: false);

        fixture.Process.StandardOutput.Write(frames[0]);
        await fixture.Session.Startup.WaitAsync(VersionParityBound);
        foreach (byte[] frame in frames.Skip(1))
        {
            fixture.Process.StandardOutput.Write(frame);
        }

        fixture.Process.StandardOutput.Complete();
        fixture.Process.StandardError.Complete();
        fixture.Process.Exit(42);

        ProcessingRunResult result = await fixture.Result.WaitAsync(VersionParityBound);
        Assert.AreEqual(ProcessingRunOutcome.Failed, result.Outcome, $"v{versionValue}-parity-crash-failed");
        Assert.AreEqual(
            WorkerRunAuthority.ControlPlane,
            fixture.Finalizer.Decision?.Authority,
            $"v{versionValue}-parity-crash-control-authority");
        Assert.AreEqual(
            WorkerRunFailureCategory.UnmappedExit,
            fixture.Finalizer.Decision?.Category,
            $"v{versionValue}-parity-crash-category");
        Assert.IsNull(
            fixture.Finalizer.Evidence?.Completion?.Terminal,
            $"v{versionValue}-parity-crash-no-synthetic-terminal");
        var protocol = Assert.IsInstanceOfType<ChildWorkerProtocolObservation.ProtocolFailure>(
            fixture.Finalizer.Evidence?.Completion?.FirstProtocolObservation,
            $"v{versionValue}-parity-crash-missing-terminal-observation");
        Assert.AreEqual(
            WorkerProtocolFailureDetail.MissingTerminal,
            protocol.Failure.Detail,
            $"v{versionValue}-parity-crash-missing-terminal-detail");
        AssertFinality(fixture, versionValue);
    }

    [TestMethod]
    [TestCategory("Change47")]
    [DataRow(1)]
    [DataRow(2)]
    public async Task VersionParity_FailedTerminalPreservesProgressAndDomainFailure(int versionValue)
    {
        InternalWorkerProtocolVersion version = Version(versionValue);
        await using VersionParityFixture fixture = await CreateVersionParityFixtureAsync(
            version,
            9400 + versionValue);
        IReadOnlyList<byte[]> frames = VersionedFrames(
            version,
            fixture.Request,
            ProcessingRunOutcome.Failed,
            includeTerminal: true);

        foreach (byte[] frame in frames)
        {
            fixture.Process.StandardOutput.Write(frame);
        }

        fixture.Process.StandardOutput.Complete();
        fixture.Process.StandardError.Complete();
        fixture.Process.Exit(4);

        ProcessingRunResult result = await fixture.Result.WaitAsync(VersionParityBound);
        Assert.AreEqual(ProcessingRunOutcome.Failed, result.Outcome, $"v{versionValue}-parity-failed-outcome");
        Assert.AreEqual(2, result.ProcessedCount, $"v{versionValue}-parity-failed-processed");
        Assert.AreEqual(1, result.UpdatedCount, $"v{versionValue}-parity-failed-updated");
        Assert.AreEqual(1, result.FailedCount, $"v{versionValue}-parity-failed-count");
        Assert.AreEqual(
            WorkerRunAuthority.CommittedReceipt,
            fixture.Finalizer.Decision?.Authority,
            $"v{versionValue}-parity-failed-terminal-authority");
        Assert.AreEqual(
            WorkerRunFailureCategory.Terminal,
            fixture.Finalizer.Decision?.Category,
            $"v{versionValue}-parity-failed-category");
        StringAssert.Contains(fixture.State.LastError, "failed", $"v{versionValue}-parity-failed-safe-error");
        Assert.IsNull(fixture.State.CurrentActivity, $"v{versionValue}-parity-failed-activity-closed");
        AssertFinality(fixture, versionValue);
    }

    [TestMethod]
    [TestCategory("Change47")]
    [DataRow(1)]
    [DataRow(2)]
    public async Task VersionParity_StopBeforeExecuteFlushWaitsThenSendsOneCancel(int versionValue)
    {
        InternalWorkerProtocolVersion version = Version(versionValue);
        await using VersionParityFixture fixture = await CreateVersionParityFixtureAsync(
            version,
            9500 + versionValue);
        var releaseFlush = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Process.StandardInput.FlushGate = releaseFlush.Task;
        IReadOnlyList<byte[]> frames = VersionedFrames(
            version,
            fixture.Request,
            ProcessingRunOutcome.Cancelled,
            includeTerminal: true);

        fixture.Process.StandardOutput.Write(frames[0]);
        await fixture.Process.StandardInput.FlushStarted.Task.WaitAsync(VersionParityBound);
        Task<ChildWorkerCancellationResult> stop = fixture.Session.RequestStop();
        Assert.IsFalse(stop.IsCompleted, $"v{versionValue}-flush-stop-waits-for-execute-flush");
        Assert.AreEqual(1, fixture.Process.StandardInput.WriteCalls, $"v{versionValue}-flush-execute-written-once");
        Assert.AreEqual(1, fixture.Process.StandardInput.FlushCalls, $"v{versionValue}-flush-execute-flush-once");
        AssertExecuteFrame(version, fixture.Request, fixture.Process.StandardInput.ToArray());

        releaseFlush.TrySetResult();
        await fixture.Session.Startup.WaitAsync(VersionParityBound);
        await fixture.Session.WaitForCancellationDeliveryAsync().WaitAsync(VersionParityBound);
        AssertControllerInput(version, fixture.Request, fixture.Process.StandardInput.ToArray());
        foreach (byte[] frame in frames.Skip(1))
        {
            fixture.Process.StandardOutput.Write(frame);
        }

        fixture.Process.StandardOutput.Complete();
        fixture.Process.StandardError.Complete();
        fixture.Process.Exit(130);

        ChildWorkerCancellationResult cancellation = await stop.WaitAsync(VersionParityBound);
        ProcessingRunResult result = await fixture.Result.WaitAsync(VersionParityBound);
        Assert.AreEqual(
            ChildWorkerCancelDeliveryPhase.Flushed,
            cancellation.Facts.DeliveryPhase,
            $"v{versionValue}-flush-cancel-flushed");
        Assert.AreEqual(2, fixture.Process.StandardInput.WriteCalls, $"v{versionValue}-flush-one-execute-one-cancel");
        Assert.AreEqual(2, fixture.Process.StandardInput.FlushCalls, $"v{versionValue}-flush-each-frame-once");
        Assert.AreEqual(ProcessingRunOutcome.Cancelled, result.Outcome, $"v{versionValue}-flush-cancel-outcome");
        AssertFinality(fixture, versionValue);
    }

    [TestMethod]
    [TestCategory("Change47")]
    [DataRow(1)]
    [DataRow(2)]
    public async Task VersionParity_NonCancellableDescriptorSuppressesCancelFrame(int versionValue)
    {
        InternalWorkerProtocolVersion version = Version(versionValue);
        var descriptor = new WorkerJobDescriptor(
            WorkerJobKind.ProcessAssets,
            typeof(ProcessAssetsRequest),
            typeof(ProcessAssetsResult),
            WorkerJobDescriptors.ProcessAssets.Arbitration with
            {
                IsCancellable = false
            });
        await using VersionParityFixture fixture = await CreateVersionParityFixtureAsync(
            version,
            9600 + versionValue,
            descriptor);
        IReadOnlyList<byte[]> frames = VersionedFrames(
            version,
            fixture.Request,
            ProcessingRunOutcome.Completed,
            includeTerminal: true);

        fixture.Process.StandardOutput.Write(frames[0]);
        await fixture.Session.Startup.WaitAsync(VersionParityBound);
        Assert.IsFalse(fixture.Session.IsCancellable, $"v{versionValue}-metadata-noncancellable");
        Task<ChildWorkerCancellationResult> stop = fixture.Session.RequestStop();
        await fixture.Session.WaitForCancellationDeliveryAsync().WaitAsync(VersionParityBound);
        Assert.AreEqual(
            ChildWorkerCancelDeliveryPhase.NotSupported,
            fixture.Session.CancellationFacts?.DeliveryPhase,
            $"v{versionValue}-metadata-no-cancel-delivery");
        Assert.AreEqual(1, fixture.Process.StandardInput.WriteCalls, $"v{versionValue}-metadata-execute-only");
        Assert.AreEqual(1, fixture.Process.StandardInput.FlushCalls, $"v{versionValue}-metadata-no-cancel-flush");
        AssertExecuteFrame(version, fixture.Request, fixture.Process.StandardInput.ToArray());

        foreach (byte[] frame in frames.Skip(1))
        {
            fixture.Process.StandardOutput.Write(frame);
        }

        fixture.Process.StandardOutput.Complete();
        fixture.Process.StandardError.Complete();
        fixture.Process.Exit(0);

        ChildWorkerCancellationResult cancellation = await stop.WaitAsync(VersionParityBound);
        ProcessingRunResult result = await fixture.Result.WaitAsync(VersionParityBound);
        Assert.IsFalse(cancellation.Facts.KillAttempted, $"v{versionValue}-metadata-no-kill-before-exit");
        Assert.AreEqual(ProcessingRunOutcome.Completed, result.Outcome, $"v{versionValue}-metadata-completed");
        AssertFinality(fixture, versionValue);
    }

    private static async Task<VersionParityFixture> CreateVersionParityFixtureAsync(
        InternalWorkerProtocolVersion version,
        int processId,
        WorkerJobDescriptor? descriptor = null)
    {
        var request = new ProcessingRunRequest(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            ProcessingRunTrigger.Manual);
        var state = new ProcessingState();
        state.MarkPending();
        var reporter = new ProcessingStateEventReporter(state);
        Assert.IsTrue(reporter.Arm(request), "version-parity-reporter-armed");
        var bridge = new WorkerEventStateBridgeFactory(reporter).Create(request);
        var process = new ByteProcess(processId);
        var dispatch = descriptor is null
            ? new ProcessAssetsWorkerJobDispatch(request)
            : new ProcessAssetsWorkerJobDispatch(request, descriptor);
        ChildWorkerSession session = await ChildWorkerSession.CreateAsync(
            process,
            dispatch,
            new ProcessAssetsWorkerJobEventSink(request, bridge),
            TestOptions(),
            new ChildWorkerObserverArmingAcknowledgements(),
            version);
        var finalizer = new WorkerRunFinalizer(
            request,
            reporter,
            session.Clock,
            protocolVersion: version);
        Task<ProcessingRunResult> result = finalizer.Start(
            session,
            bridge,
            fault => _ = session.RequestTermination(
                new ChildWorkerTerminationRequest(
                    fault.ObservedAt,
                    ChildWorkerTerminationIntent.FaultContainment,
                    fault.Reason)),
            static () => false);
        return new VersionParityFixture(
            request,
            state,
            process,
            session,
            bridge,
            finalizer,
            result);
    }

    private static IReadOnlyList<byte[]> VersionedFrames(
        InternalWorkerProtocolVersion version,
        ProcessingRunRequest request,
        ProcessingRunOutcome outcome,
        bool includeTerminal)
    {
        DateTimeOffset readyAtUtc = DateTimeOffset.UnixEpoch;
        DateTimeOffset startedAtUtc = readyAtUtc.AddSeconds(1);
        DateTimeOffset progressAtUtc = readyAtUtc.AddSeconds(2);
        DateTimeOffset endedAtUtc = readyAtUtc.AddSeconds(3);
        var result = new ProcessingRunResult(
            request,
            startedAtUtc,
            endedAtUtc,
            2,
            1,
            0,
            1,
            outcome,
            outcome == ProcessingRunOutcome.Failed ? "failed" : null);
        if (version == InternalWorkerProtocolVersion.V1)
        {
            var events = new List<WorkerProtocolEvent>
            {
                WorkerProtocolMapper.Ready(1, readyAtUtc),
                WorkerProtocolMapper.Map(new RunStarted(request, startedAtUtc), 2),
                WorkerProtocolMapper.Map(new EligibilityDetermined(request, 2), 3, progressAtUtc),
                WorkerProtocolMapper.Map(
                    new ActivityStarted(request, request.RunId, "parity-activity"),
                    4,
                    progressAtUtc),
                WorkerProtocolMapper.Map(
                    new LogEmitted(request, ProcessingLogLevel.Information, "parity-log"),
                    5,
                    progressAtUtc),
                WorkerProtocolMapper.Map(
                    new ActivityEnded(request, request.RunId),
                    6,
                    progressAtUtc),
                WorkerProtocolMapper.Map(
                    new ProgressChanged(request, new ProcessingProgress(1, 1, 0, 0)),
                    7,
                    progressAtUtc),
                WorkerProtocolMapper.Map(
                    new ProgressChanged(request, new ProcessingProgress(2, 1, 0, 1)),
                    8,
                    progressAtUtc)
            };
            if (includeTerminal)
            {
                events.Add(WorkerProtocolMapper.Map(new RunFinished(request, result), 9));
            }

            return events.Select(FrameV1).ToArray();
        }

        var context = new WorkerJobContext(
            request.RunId,
            WorkerJobKind.ProcessAssets,
            WorkerJobRequestOrigin.Manual);
        var messages = new List<WorkerJobOutputMessage>
        {
            WorkerJobProtocolMapper.Ready(
                1,
                readyAtUtc,
                new WorkerJobReadyPayload([WorkerJobKind.ProcessAssets])),
            WorkerJobProtocolMapper.JobStarted(context, "manual", startedAtUtc, 2),
            WorkerJobProtocolMapper.Map(
                context,
                new WorkerJobHandlerEvent(
                    progressAtUtc,
                    new ProcessAssetsEligibilityPayload(2)),
                3),
            WorkerJobProtocolMapper.Map(
                context,
                new WorkerJobHandlerEvent(
                    progressAtUtc,
                    new WorkerJobActivityStartedPayload(request.RunId, "parity-activity")),
                4),
            WorkerJobProtocolMapper.Map(
                context,
                new WorkerJobHandlerEvent(
                    progressAtUtc,
                    new WorkerJobLogPayload("information", "parity-log")),
                5),
            WorkerJobProtocolMapper.Map(
                context,
                new WorkerJobHandlerEvent(
                    progressAtUtc,
                    new WorkerJobActivityEndedPayload(request.RunId)),
                6),
            WorkerJobProtocolMapper.Map(
                context,
                new WorkerJobHandlerEvent(
                    progressAtUtc,
                    new ProcessAssetsProgressPayload(1, 1, 0, 0)),
                7),
            WorkerJobProtocolMapper.Map(
                context,
                new WorkerJobHandlerEvent(
                    progressAtUtc,
                    new ProcessAssetsProgressPayload(2, 1, 0, 1)),
                8)
        };
        if (includeTerminal)
        {
            var typedResult = outcome == ProcessingRunOutcome.Completed
                ? new ProcessAssetsResult(
                    "manual",
                    startedAtUtc,
                    endedAtUtc,
                    2,
                    1,
                    0,
                    1)
                : null;
            WorkerJobTerminalOutcome terminalOutcome = outcome switch
            {
                ProcessingRunOutcome.Completed => WorkerJobTerminalOutcome.Completed,
                ProcessingRunOutcome.Cancelled => WorkerJobTerminalOutcome.Cancelled,
                ProcessingRunOutcome.Failed => WorkerJobTerminalOutcome.Failed,
                _ => throw new ArgumentOutOfRangeException(nameof(outcome))
            };
            messages.Add(WorkerJobProtocolMapper.Terminal(
                context,
                new WorkerJobTerminalPayload(
                    terminalOutcome,
                    startedAtUtc,
                    endedAtUtc,
                    typedResult,
                    outcome == ProcessingRunOutcome.Failed
                        ? new WorkerJobSafeError(
                            "executor-failed",
                            WorkerJobFailureCategory.Domain,
                            "failed")
                        : null),
                9));
        }

        return messages.Select(Frame).ToArray();
    }

    private static byte[] FrameV1(WorkerProtocolEvent @event) =>
        WorkerProtocolCodec.Serialize(@event)
            .Concat("\n"u8.ToArray())
            .ToArray();

    private static void AssertExecuteFrame(
        InternalWorkerProtocolVersion version,
        ProcessingRunRequest request,
        byte[] bytes)
    {
        byte[] frame = Encoding.UTF8.GetString(bytes)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(Encoding.UTF8.GetBytes)
            .Single();
        if (version == InternalWorkerProtocolVersion.V1)
        {
            WorkerProtocolControllerParseResult parsed =
                WorkerProtocolCodec.ParseControllerInput(frame);
            Assert.IsTrue(parsed.IsSuccess, "v1-parity-execute-valid");
            Assert.AreEqual(request.RunId, parsed.Message!.RunId, "v1-parity-execute-id");
            return;
        }

        WorkerJobControllerParseResult jobParsed =
            WorkerJobProtocolCodec.ParseControllerInput(frame);
        Assert.IsTrue(jobParsed.IsSuccess, "v2-parity-execute-valid");
        Assert.AreEqual(request.RunId, jobParsed.Message!.JobId, "v2-parity-execute-id");
        Assert.AreEqual(WorkerJobKind.ProcessAssets, jobParsed.Message.JobKind, "v2-parity-execute-kind");
    }

    private static void AssertControllerInput(
        InternalWorkerProtocolVersion version,
        ProcessingRunRequest request,
        byte[] bytes)
    {
        byte[][] frames = Encoding.UTF8.GetString(bytes)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(Encoding.UTF8.GetBytes)
            .ToArray();
        Assert.AreEqual(2, frames.Length, $"{version}-parity-execute-and-cancel-only");
        AssertExecuteFrame(version, request, frames[0].Concat("\n"u8.ToArray()).ToArray());
        if (version == InternalWorkerProtocolVersion.V1)
        {
            WorkerProtocolControllerMessage cancel =
                WorkerProtocolCodec.ParseControllerInput(frames[1]).Message!;
            Assert.AreEqual(WorkerProtocolV1.CancelType, cancel.Type, "v1-parity-cancel-type");
            Assert.AreEqual(request.RunId, cancel.RunId, "v1-parity-cancel-id");
            return;
        }

        WorkerJobControllerMessage jobCancel =
            WorkerJobProtocolCodec.ParseControllerInput(frames[1]).Message!;
        Assert.AreEqual(WorkerJobProtocolV2.CancelType, jobCancel.Type, "v2-parity-cancel-type");
        Assert.AreEqual(request.RunId, jobCancel.JobId, "v2-parity-cancel-id");
        Assert.AreEqual(WorkerJobKind.ProcessAssets, jobCancel.JobKind, "v2-parity-cancel-kind");
    }

    private static InternalWorkerProtocolVersion Version(int value) => value switch
    {
        1 => InternalWorkerProtocolVersion.V1,
        2 => InternalWorkerProtocolVersion.V2,
        _ => throw new AssertFailedException("version-parity-undefined-version")
    };

    private static void AssertFinality(VersionParityFixture fixture, int versionValue)
    {
        ChildWorkerCompletionObservation completion = fixture.Finalizer.Evidence!.Completion!;
        Assert.IsTrue(completion.ExitObserved, $"v{versionValue}-parity-exit-observed");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(
            completion.StandardOutputFinality,
            $"v{versionValue}-parity-stdout-eof");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(
            completion.StandardErrorFinality,
            $"v{versionValue}-parity-stderr-eof");
        Assert.AreEqual(fixture.Process.ProcessId, completion.ProcessId, $"v{versionValue}-parity-process-id");
        Assert.AreEqual(1, fixture.Process.DisposeCalls, $"v{versionValue}-parity-process-disposed-once");
    }

    private sealed record VersionParityFixture(
        ProcessingRunRequest Request,
        ProcessingState State,
        ByteProcess Process,
        ChildWorkerSession Session,
        WorkerEventStateBridge Bridge,
        WorkerRunFinalizer Finalizer,
        Task<ProcessingRunResult> Result) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Process.StandardOutput.ReleaseReads();
            Process.StandardError.ReleaseReads();
            Process.StandardOutput.Complete();
            Process.StandardError.Complete();
            Process.Exit(5);
            await Session.DisposeAsync().AsTask().WaitAsync(VersionParityBound);
            await Bridge.DisposeAsync().AsTask().WaitAsync(VersionParityBound);
        }
    }
}
