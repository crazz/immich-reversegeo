using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerEventStateBridge;

namespace ImmichReverseGeo.Tests.ChildWorkerLaunching;

public sealed partial class ChildWorkerLaunchingTests
{
    [TestMethod]
    [TestCategory("Change47")]
    public async Task ProtocolV2_SharedSessionWritesTypedExecuteAndProjectsTypedLifecycle()
    {
        ProcessingRunRequest request = CreateRequest();
        var process = new ByteProcess(919);
        var sink = new RecordingSink();
        ChildWorkerSession session = await ChildWorkerSession.CreateAsync(
            process,
            request,
            sink,
            TestOptions(),
            new ChildWorkerObserverArmingAcknowledgements(),
            InternalWorkerProtocolVersion.V2);
        var context = new WorkerJobContext(
            request.RunId,
            WorkerJobKind.ProcessAssets,
            WorkerJobRequestOrigin.Manual);
        var startedAtUtc = new DateTimeOffset(2026, 8, 29, 12, 0, 1, TimeSpan.Zero);
        var endedAtUtc = new DateTimeOffset(2026, 8, 29, 12, 0, 3, TimeSpan.Zero);

        process.StandardOutput.Write(Frame(WorkerJobProtocolMapper.Ready(
            1,
            new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero),
            new WorkerJobReadyPayload([WorkerJobKind.ProcessAssets]))));
        Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyAccepted>(
            await session.Startup,
            "v2-session-ready-accepted");

        WorkerJobControllerParseResult execute = WorkerJobProtocolCodec.ParseControllerInput(
            process.StandardInput.ToArray().AsSpan(0, process.StandardInput.ToArray().Length - 1));
        Assert.IsTrue(execute.IsSuccess, "v2-session-controller-execute-parses");
        Assert.AreEqual(WorkerJobProtocolV2.ExecuteType, execute.Message!.Type, "v2-session-execute-type");
        Assert.AreEqual(request.RunId, execute.Message.JobId, "v2-session-execute-job-id");
        Assert.AreEqual(WorkerJobKind.ProcessAssets, execute.Message.JobKind, "v2-session-execute-kind");
        Assert.AreEqual(InternalWorkerProtocolVersion.V2, session.ProtocolVersion, "v2-session-version");
        Assert.AreEqual(request.RunId, session.JobId, "v2-session-job-id-alias");
        Assert.AreEqual(session.JobId, session.RunId, "v2-session-identical-run-id-compatibility");

        process.StandardOutput.Write(Frame(WorkerJobProtocolMapper.JobStarted(
            context,
            "manual",
            startedAtUtc,
            2)));
        process.StandardOutput.Write(Frame(WorkerJobProtocolMapper.Map(
            context,
            new WorkerJobHandlerEvent(
                new DateTimeOffset(2026, 8, 29, 12, 0, 2, TimeSpan.Zero),
                new ProcessAssetsEligibilityPayload(2)),
            3)));
        process.StandardOutput.Write(Frame(WorkerJobProtocolMapper.Map(
            context,
            new WorkerJobHandlerEvent(
                new DateTimeOffset(2026, 8, 29, 12, 0, 2, TimeSpan.Zero),
                new ProcessAssetsProgressPayload(2, 1, 0, 1)),
            4)));
        var typedResult = new ProcessAssetsResult(
            "manual",
            startedAtUtc,
            endedAtUtc,
            2,
            1,
            0,
            1);
        process.StandardOutput.Write(Frame(WorkerJobProtocolMapper.Terminal(
            context,
            new WorkerJobTerminalPayload(
                WorkerJobTerminalOutcome.Completed,
                startedAtUtc,
                endedAtUtc,
                typedResult,
                null),
            5)));
        process.StandardOutput.Complete();
        process.StandardError.Complete();
        process.Exit(19);

        ChildWorkerCompletionObservation completion = await session.Completion;
        CollectionAssert.AreEqual(
            new[]
            {
                WorkerProtocolV1.ReadyType,
                WorkerProtocolV1.RunStartedType,
                WorkerProtocolV1.EligibilityDeterminedType,
                WorkerProtocolV1.ProgressChangedType,
                WorkerProtocolV1.CompletedType
            },
            sink.Events.Select(@event => @event.Type).ToArray(),
            "v2-session-projected-lifecycle");
        var progress = Assert.IsInstanceOfType<ProgressChangedPayload>(sink.Events[3].Payload);
        Assert.AreEqual(1, progress.UpdatedCount, "v2-session-projected-updated");
        Assert.AreEqual(1, progress.FailedCount, "v2-session-projected-handled-failed");
        var terminal = Assert.IsInstanceOfType<CompletedPayload>(sink.Events[^1].Payload);
        Assert.AreEqual(1, terminal.UpdatedCount, "v2-session-terminal-updated");
        Assert.AreEqual(1, terminal.FailedCount, "v2-session-terminal-handled-failed");
        Assert.AreSame(sink.Events[^1], completion.Terminal, "v2-session-committed-projected-terminal");
        Assert.AreEqual(19, completion.ExitCode, "v2-session-contradictory-exit-retained");
        Assert.AreEqual(request.RunId, completion.JobId, "v2-session-completion-job-id");
        Assert.AreEqual(WorkerJobKind.ProcessAssets, completion.JobKind, "v2-session-completion-kind");
        Assert.AreEqual(InternalWorkerProtocolVersion.V2, completion.ProtocolVersion, "v2-session-completion-version");
        Assert.IsNull(completion.FirstProtocolObservation, "v2-session-no-protocol-failure");
        await session.DisposeAsync();
    }

    [TestMethod]
    [TestCategory("Change47")]
    public async Task ProtocolV2_SharedStopWritesOneTypedCancelForExactJobIdentity()
    {
        ProcessingRunRequest request = CreateRequest();
        var process = new ByteProcess(918);
        var sink = new RecordingSink();
        ChildWorkerSession session = await ChildWorkerSession.CreateAsync(
            process,
            request,
            sink,
            TestOptions(),
            new ChildWorkerObserverArmingAcknowledgements(),
            InternalWorkerProtocolVersion.V2);

        process.StandardOutput.Write(Frame(WorkerJobProtocolMapper.Ready(
            1,
            DateTimeOffset.UnixEpoch,
            new WorkerJobReadyPayload([WorkerJobKind.ProcessAssets]))));
        await session.Startup;
        Task<ChildWorkerCancellationResult> stop = session.RequestStop();
        await session.WaitForCancellationDeliveryAsync();

        byte[][] inputs = Encoding.UTF8.GetString(process.StandardInput.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(Encoding.UTF8.GetBytes)
            .ToArray();
        Assert.AreEqual(2, inputs.Length, "v2-session-execute-and-one-cancel");
        WorkerJobControllerMessage cancel =
            WorkerJobProtocolCodec.ParseControllerInput(inputs[1]).Message!;
        Assert.AreEqual(WorkerJobProtocolV2.CancelType, cancel.Type, "v2-session-cancel-type");
        Assert.AreEqual(request.RunId, cancel.JobId, "v2-session-cancel-job-id");
        Assert.AreEqual(WorkerJobKind.ProcessAssets, cancel.JobKind, "v2-session-cancel-kind");
        Assert.AreEqual(2L, cancel.Sequence, "v2-session-cancel-sequence");

        process.StandardOutput.Complete();
        process.StandardError.Complete();
        process.Exit(130);
        await stop;
        Assert.AreEqual(1, process.StandardInput.FlushCalls - 1, "v2-session-one-cancel-flush-after-execute");
        await session.DisposeAsync();
    }

    [TestMethod]
    [TestCategory("Change47")]
    [DataRow("cancelled")]
    [DataRow("failed")]
    public async Task ProtocolV2_NonSuccessTerminalPreservesLastProgressCounts(string outcome)
    {
        ProcessingRunRequest request = CreateRequest();
        var process = new ByteProcess(outcome == "cancelled" ? 917 : 916);
        var sink = new RecordingSink();
        ChildWorkerSession session = await ChildWorkerSession.CreateAsync(
            process,
            request,
            sink,
            TestOptions(),
            new ChildWorkerObserverArmingAcknowledgements(),
            InternalWorkerProtocolVersion.V2);
        var context = new WorkerJobContext(
            request.RunId,
            WorkerJobKind.ProcessAssets,
            WorkerJobRequestOrigin.Manual);
        var startedAtUtc = new DateTimeOffset(2026, 8, 29, 12, 0, 1, TimeSpan.Zero);
        var endedAtUtc = new DateTimeOffset(2026, 8, 29, 12, 0, 3, TimeSpan.Zero);

        process.StandardOutput.Write(Frame(WorkerJobProtocolMapper.Ready(
            1,
            DateTimeOffset.UnixEpoch,
            new WorkerJobReadyPayload([WorkerJobKind.ProcessAssets]))));
        await session.Startup;
        process.StandardOutput.Write(Frame(WorkerJobProtocolMapper.JobStarted(
            context,
            "manual",
            startedAtUtc,
            2)));
        process.StandardOutput.Write(Frame(WorkerJobProtocolMapper.Map(
            context,
            new WorkerJobHandlerEvent(startedAtUtc, new ProcessAssetsEligibilityPayload(2)),
            3)));
        process.StandardOutput.Write(Frame(WorkerJobProtocolMapper.Map(
            context,
            new WorkerJobHandlerEvent(
                endedAtUtc,
                new ProcessAssetsProgressPayload(2, 1, 0, 1)),
            4)));
        WorkerJobTerminalOutcome terminalOutcome = outcome == "cancelled"
            ? WorkerJobTerminalOutcome.Cancelled
            : WorkerJobTerminalOutcome.Failed;
        process.StandardOutput.Write(Frame(WorkerJobProtocolMapper.Terminal(
            context,
            new WorkerJobTerminalPayload(
                terminalOutcome,
                startedAtUtc,
                endedAtUtc,
                null,
                terminalOutcome == WorkerJobTerminalOutcome.Failed
                    ? new WorkerJobSafeError(
                        "process-assets-failed",
                        WorkerJobFailureCategory.Domain,
                        "The processing job failed.")
                    : null),
            5)));
        process.StandardOutput.Complete();
        process.StandardError.Complete();
        process.Exit(outcome == "cancelled" ? 130 : 4);

        ChildWorkerCompletionObservation completion = await session.Completion;
        TerminalPayload projected = Assert.IsInstanceOfType<TerminalPayload>(
            completion.Terminal!.Payload);
        Assert.AreEqual(2, projected.ProcessedCount, outcome + ":projected-processed");
        Assert.AreEqual(1, projected.UpdatedCount, outcome + ":projected-updated");
        Assert.AreEqual(1, projected.FailedCount, outcome + ":projected-handled-failed");
        Assert.AreEqual(
            outcome == "cancelled" ? WorkerProtocolV1.CancelledType : WorkerProtocolV1.FailedType,
            completion.Terminal.Type,
            outcome + ":projected-terminal-type");
        Assert.IsNull(completion.FirstProtocolObservation, outcome + ":valid-v2-stream");
        await session.DisposeAsync();
    }

    [TestMethod]
    [TestCategory("Change47")]
    public async Task ProtocolV2_SharedSessionProjectsThroughExistingProcessingStateBridge()
    {
        ProcessingRunRequest request = CreateRequest();
        var state = new ProcessingState();
        state.MarkPending();
        var reporter = new ProcessingStateEventReporter(state);
        Assert.IsTrue(reporter.Arm(request), "v2-bridge-reporter-armed");
        var bridge = new WorkerEventStateBridgeFactory(reporter).Create(request);
        var process = new ByteProcess(915);
        ChildWorkerSession session = await ChildWorkerSession.CreateAsync(
            process,
            request,
            bridge,
            TestOptions(),
            new ChildWorkerObserverArmingAcknowledgements(),
            InternalWorkerProtocolVersion.V2);
        var context = new WorkerJobContext(
            request.RunId,
            WorkerJobKind.ProcessAssets,
            WorkerJobRequestOrigin.Manual);
        DateTimeOffset startedAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(1);
        DateTimeOffset endedAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(3);
        Guid activityId = Guid.Parse("34343434-3434-3434-3434-343434343434");

        process.StandardOutput.Write(Frame(WorkerJobProtocolMapper.Ready(
            1,
            DateTimeOffset.UnixEpoch,
            new WorkerJobReadyPayload([WorkerJobKind.ProcessAssets]))));
        await session.Startup;
        process.StandardOutput.Write(Frame(WorkerJobProtocolMapper.JobStarted(
            context,
            "manual",
            startedAtUtc,
            2)));
        process.StandardOutput.Write(Frame(WorkerJobProtocolMapper.Map(
            context,
            new WorkerJobHandlerEvent(startedAtUtc, new ProcessAssetsEligibilityPayload(2)),
            3)));
        process.StandardOutput.Write(Frame(WorkerJobProtocolMapper.Map(
            context,
            new WorkerJobHandlerEvent(
                startedAtUtc,
                new WorkerJobActivityStartedPayload(activityId, "processing-assets")),
            4)));
        process.StandardOutput.Write(Frame(WorkerJobProtocolMapper.Map(
            context,
            new WorkerJobHandlerEvent(
                startedAtUtc,
                new WorkerJobLogPayload("information", "processing")),
            5)));
        process.StandardOutput.Write(Frame(WorkerJobProtocolMapper.Map(
            context,
            new WorkerJobHandlerEvent(
                startedAtUtc,
                new WorkerJobActivityEndedPayload(activityId)),
            6)));
        process.StandardOutput.Write(Frame(WorkerJobProtocolMapper.Map(
            context,
            new WorkerJobHandlerEvent(
                endedAtUtc,
                new ProcessAssetsProgressPayload(1, 1, 0, 0)),
            7)));
        process.StandardOutput.Write(Frame(WorkerJobProtocolMapper.Map(
            context,
            new WorkerJobHandlerEvent(
                endedAtUtc,
                new ProcessAssetsProgressPayload(2, 1, 0, 1)),
            8)));
        var result = new ProcessAssetsResult(
            "manual",
            startedAtUtc,
            endedAtUtc,
            2,
            1,
            0,
            1);
        process.StandardOutput.Write(Frame(WorkerJobProtocolMapper.Terminal(
            context,
            new WorkerJobTerminalPayload(
                WorkerJobTerminalOutcome.Completed,
                startedAtUtc,
                endedAtUtc,
                result,
                null),
            9)));
        process.StandardOutput.Complete();
        process.StandardError.Complete();
        process.Exit(0);

        ChildWorkerCompletionObservation completion = await session.Completion;
        Assert.IsNull(completion.FirstProtocolObservation, "v2-bridge-valid-stream");
        Assert.IsTrue(bridge.IsTerminal, "v2-bridge-terminal-committed");
        Assert.AreEqual(1, state.ProcessedThisRun, "v2-bridge-updated-count");
        Assert.AreEqual(1, state.ErrorsThisRun, "v2-bridge-handled-failed-count");
        Assert.IsNull(state.CurrentActivity, "v2-bridge-activity-closed");
        Assert.IsTrue(
            state.GetRecentLog().Any(line => line.Contains("processing", StringComparison.Ordinal)),
            "v2-bridge-log-projected");
        Assert.IsNotNull(state.LastRunStarted, "v2-bridge-start-projected");
        Assert.IsNotNull(state.LastRunCompleted, "v2-bridge-terminal-projected");
        Assert.AreEqual(request.RunId, reporter.GetFinalizationReceipt(request)?.Request.RunId, "v2-bridge-receipt-identity");
        await session.DisposeAsync();
        await bridge.DisposeAsync();
    }

    private static byte[] Frame(WorkerJobOutputMessage message) =>
        WorkerJobProtocolCodec.Serialize(message)
            .Concat("\n"u8.ToArray())
            .ToArray();

    [TestMethod]
    public async Task Protocol_ReadySequenceOneWithNullRunId_IsAcceptedAndDelivered()
    {
        var (process, sink, session) = await LaunchByteSessionAsync(920);

        process.StandardOutput.Write(Encoding.UTF8.GetBytes(ReadyObjectLiteral + "\n"));
        var startup = await session.Startup;
        process.StandardOutput.Complete();
        process.StandardError.Complete();
        process.Exit(0);
        var completion = await session.Completion;

        Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyAccepted>(startup, "ready-null-run-id: exact-startup");
        Assert.AreEqual(1, sink.AcceptCalls, "ready-null-run-id: exact-callback-count");
        Assert.AreEqual(1L, sink.Events[0].Sequence, "ready-null-run-id: exact-sequence");
        Assert.IsNull(sink.Events[0].RunId, "ready-null-run-id: exact-null-run-id");
        Assert.IsNull(completion.FirstProtocolObservation, "ready-null-run-id: no-first-failure");
        await session.DisposeAsync();
    }

    [TestMethod]
    [DataRow("incompatible-protocol", WorkerProtocolFailureCode.UnsupportedProtocol)]
    [DataRow("incompatible-version", WorkerProtocolFailureCode.UnsupportedVersion)]
    [DataRow("incompatible-direction", WorkerProtocolFailureCode.UnsupportedType)]
    [DataRow("incompatible-category", WorkerProtocolFailureCode.UnsupportedType)]
    [DataRow("incompatible-type", WorkerProtocolFailureCode.UnsupportedType)]
    [DataRow("ready-wrong-nonnull-run-id", WorkerProtocolFailureCode.InvalidPayload)]
    [DataRow("not-ready-first", WorkerProtocolFailureCode.InvalidLifecycle)]
    public async Task Protocol_InvalidFirstEventRows_AreNeverSinkDelivered(string label, WorkerProtocolFailureCode expectedCode)
    {
        var request = CreateRequest();
        var literal = label switch
        {
            "incompatible-protocol" => "{\"protocol\":\"other.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"lifecycle\",\"type\":\"ready\",\"sequence\":1,\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\",\"runId\":null,\"payload\":{}}\n",
            "incompatible-version" => "{\"protocol\":\"immich-reversegeo.worker\",\"version\":2,\"direction\":\"worker-to-controller\",\"category\":\"lifecycle\",\"type\":\"ready\",\"sequence\":1,\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\",\"runId\":null,\"payload\":{}}\n",
            "incompatible-direction" => "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"controller-to-worker\",\"category\":\"lifecycle\",\"type\":\"ready\",\"sequence\":1,\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\",\"runId\":null,\"payload\":{}}\n",
            "incompatible-category" => "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"request\",\"type\":\"ready\",\"sequence\":1,\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\",\"runId\":null,\"payload\":{}}\n",
            "incompatible-type" => "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"lifecycle\",\"type\":\"unknown\",\"sequence\":1,\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\",\"runId\":null,\"payload\":{}}\n",
            "ready-wrong-nonnull-run-id" => $"{{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"lifecycle\",\"type\":\"ready\",\"sequence\":1,\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\",\"runId\":\"{request.RunId:D}\",\"payload\":{{}}}}\n",
            "not-ready-first" => $"{{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"lifecycle\",\"type\":\"run-started\",\"sequence\":1,\"timestampUtc\":\"2026-08-29T12:00:01.0000000Z\",\"runId\":\"{request.RunId:D}\",\"payload\":{{\"trigger\":\"manual\",\"startedAtUtc\":\"2026-08-29T12:00:01.0000000Z\"}}}}\n",
            _ => throw new InvalidOperationException(label)
        };
        var (process, sink, session) = await LaunchByteSessionAsync(921, request);

        process.StandardOutput.Write(Encoding.UTF8.GetBytes(literal));
        var startup = await session.Startup;
        process.StandardError.Write(DrainSentinel.ToArray());
        process.StandardOutput.Complete();
        process.StandardError.Complete();
        process.Exit(0);
        var completion = await session.Completion;

        AssertPreReadyProtocolFailure(label, startup, completion, expectedCode);
        Assert.AreEqual(0, sink.AcceptCalls, $"{label}: invalid-event-never-delivered");
        Assert.AreEqual(0, process.StandardInput.WriteCalls, $"{label}: no-execute-write");
        Assert.AreEqual(0, process.StandardInput.FlushCalls, $"{label}: no-execute-flush");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardOutputFinality, $"{label}: stdout-drained");
        CollectionAssert.AreEqual(DrainSentinel, completion.StandardErrorTail.Bytes.ToArray(), $"{label}: stderr-drained");
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Protocol_ControllerExecuteFrameOnStandardOutput_FailsExactDirectionWithoutSideEffects()
    {
        var request = CreateRequest();
        var controllerExecuteFrame = "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"controller-to-worker\",\"category\":\"request\",\"type\":\"execute\",\"sequence\":1,\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\",\"runId\":\"11111111-1111-1111-1111-111111111111\",\"payload\":{\"trigger\":\"manual\"}}\n"u8.ToArray();
        var (process, sink, session) = await LaunchByteSessionAsync(926, request);

        process.StandardOutput.Write(controllerExecuteFrame);
        var startup = await session.Startup;
        process.StandardOutput.Complete();
        process.StandardError.Complete();
        process.Exit(0);
        var completion = await session.Completion;

        var failure = AssertPreReadyProtocolFailure(
            "controller-execute-on-stdout",
            startup,
            completion,
            WorkerProtocolFailureCode.UnsupportedType);
        Assert.AreEqual("Direction is not supported.", failure.Diagnostic, "controller-execute-on-stdout: exact-direction-failure");
        Assert.AreEqual(0, sink.AcceptCalls, "controller-execute-on-stdout: zero-sink-delivery");
        Assert.AreEqual(0, process.StandardInput.WriteCalls, "controller-execute-on-stdout: zero-stdin-write");
        Assert.AreEqual(0, process.StandardInput.FlushCalls, "controller-execute-on-stdout: zero-stdin-flush");
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Protocol_WrongCorrelatedTerminalDoesNotStartNormalInputClose()
    {
        var request = CreateRequest();
        var otherRunId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var (process, sink, session) = await LaunchByteSessionAsync(921, request);

        process.StandardOutput.Write(Encoding.UTF8.GetBytes(CompletedFrame(otherRunId)));
        var startup = await session.Startup;

        Assert.IsInstanceOfType<ChildWorkerStartupObservation.ProtocolFailure>(startup, "wrong-terminal: pre-ready-correlation-rejected");
        Assert.AreEqual(0, sink.AcceptCalls, "wrong-terminal: no-sink-callback");
        Assert.AreEqual(0, process.StandardInput.DisposeCalls, "wrong-terminal: invalid-terminal-cannot-start-normal-input-close");

        process.StandardOutput.Complete();
        process.StandardError.Complete();
        process.Exit(2);
        await session.Completion;
        await session.DisposeAsync();
    }

    [TestMethod]
    [DataRow("sequence-gap", 1, WorkerProtocolFailureCode.InvalidSequence)]
    [DataRow("sequence-replay", 2, WorkerProtocolFailureCode.InvalidSequence)]
    [DataRow("duplicate-ready", 1, WorkerProtocolFailureCode.InvalidLifecycle)]
    [DataRow("wrong-request-run-id", 1, WorkerProtocolFailureCode.InvalidCorrelation)]
    [DataRow("invalid-lifecycle-terminal-before-start", 1, WorkerProtocolFailureCode.InvalidLifecycle)]
    [DataRow("missing-terminal-finalize-stream", 3, WorkerProtocolFailureCode.InvalidLifecycle)]
    [DataRow("post-terminal-event", 4, WorkerProtocolFailureCode.InvalidLifecycle)]
    public async Task Protocol_InvalidLifecycleRows_DeliverOnlyValidPrefix(
        string label,
        int expectedAcceptedPrefix,
        WorkerProtocolFailureCode expectedCode)
    {
        var request = CreateRequest();
        var otherRunId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var literal = label switch
        {
            "sequence-gap" => ReadyFrame() + $"{{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"lifecycle\",\"type\":\"run-started\",\"sequence\":3,\"timestampUtc\":\"2026-08-29T12:00:01.0000000Z\",\"runId\":\"{request.RunId:D}\",\"payload\":{{\"trigger\":\"manual\",\"startedAtUtc\":\"2026-08-29T12:00:01.0000000Z\"}}}}\n",
            "sequence-replay" => ReadyFrame() + RunStartedFrame(request.RunId) + $"{{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"lifecycle\",\"type\":\"eligibility-determined\",\"sequence\":2,\"timestampUtc\":\"2026-08-29T12:00:02.0000000Z\",\"runId\":\"{request.RunId:D}\",\"payload\":{{\"eligibleCount\":0}}}}\n",
            "duplicate-ready" => ReadyFrame() + ReadyObjectLiteral.Replace("\"sequence\":1", "\"sequence\":2", StringComparison.Ordinal) + "\n",
            "wrong-request-run-id" => ReadyFrame() + RunStartedFrame(otherRunId),
            "invalid-lifecycle-terminal-before-start" => ReadyFrame() + CompletedFrame(request.RunId).Replace("\"sequence\":4", "\"sequence\":2", StringComparison.Ordinal),
            "missing-terminal-finalize-stream" => ReadyFrame() + RunStartedFrame(request.RunId) + EligibilityFrame(request.RunId),
            "post-terminal-event" => ReadyFrame() + RunStartedFrame(request.RunId) + EligibilityFrame(request.RunId) + CompletedFrame(request.RunId) + CompletedFrame(request.RunId).Replace("\"sequence\":4", "\"sequence\":5", StringComparison.Ordinal),
            _ => throw new InvalidOperationException(label)
        };
        var (process, sink, session) = await LaunchByteSessionAsync(922, request);

        process.StandardOutput.Write(Encoding.UTF8.GetBytes(literal));
        var startup = await session.Startup;
        Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyAccepted>(startup, $"{label}: ready-prefix-committed");
        process.StandardError.Write(DrainSentinel.ToArray());
        process.StandardOutput.Complete();
        process.StandardError.Complete();
        process.Exit(0);
        var completion = await session.Completion;

        var first = Assert.IsInstanceOfType<ChildWorkerProtocolObservation.ProtocolFailure>(completion.FirstProtocolObservation, $"{label}: exact-first-fact");
        Assert.AreEqual(expectedCode, first.Failure.Code, $"{label}: exact-failure-code");
        Assert.AreEqual(expectedAcceptedPrefix, sink.AcceptCalls, $"{label}: only-valid-prefix-delivered");
        Assert.AreEqual(expectedAcceptedPrefix, sink.Events.Count, $"{label}: invalid-event-not-recorded");
        Assert.AreEqual(1, process.StandardInput.WriteCalls, $"{label}: execute-written-once-after-ready");
        Assert.AreEqual(1, process.StandardInput.FlushCalls, $"{label}: execute-flushed-once-after-ready");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardOutputFinality, $"{label}: stdout-finality");
        CollectionAssert.AreEqual(DrainSentinel, completion.StandardErrorTail.Bytes.ToArray(), $"{label}: stderr-drained");
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Sink_CompleteValidLifecycle_RetainsExactReferencesContentTokensOrderAndRawConflict()
    {
        var request = CreateRequest();
        var sink = new RecordingSink();
        var (process, _, session) = await LaunchByteSessionAsync(923, request, sink);

        process.StandardOutput.Write(Encoding.UTF8.GetBytes(ReadyFrame() + RunStartedFrame(request.RunId) + EligibilityFrame(request.RunId) + CompletedFrame(request.RunId)));
        Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyAccepted>(await session.Startup, "valid-lifecycle: startup-before-exit");
        process.StandardError.Write(Encoding.UTF8.GetBytes("raw-conflicting-exit"));
        process.StandardOutput.Complete();
        process.StandardError.Complete();
        process.Exit(19);
        var completion = await session.Completion;
        var events = sink.Events;

        Assert.AreEqual(4, sink.AcceptCalls, "valid-lifecycle: every-callback-exactly-once");
        Assert.AreEqual(1, sink.MaximumConcurrency, "valid-lifecycle: no-overlap");
        CollectionAssert.AreEqual(
            new[] { WorkerProtocolV1.ReadyType, WorkerProtocolV1.RunStartedType, WorkerProtocolV1.EligibilityDeterminedType, WorkerProtocolV1.CompletedType },
            events.Select(@event => @event.Type).ToArray(),
            "valid-lifecycle: exact-order");
        CollectionAssert.AreEqual(new[] { "lifecycle", "lifecycle", "lifecycle", "terminal" }, events.Select(@event => @event.Category).ToArray(), "valid-lifecycle: exact-categories");
        CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4 }, events.Select(@event => @event.Sequence).ToArray(), "valid-lifecycle: exact-sequences");
        CollectionAssert.AreEqual(
            new[]
            {
                new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 8, 29, 12, 0, 1, TimeSpan.Zero),
                new DateTimeOffset(2026, 8, 29, 12, 0, 2, TimeSpan.Zero),
                new DateTimeOffset(2026, 8, 29, 12, 0, 3, TimeSpan.Zero)
            },
            events.Select(@event => @event.TimestampUtc).ToArray(),
            "valid-lifecycle: exact-timestamps");
        Assert.IsInstanceOfType<ReadyPayload>(events[0].Payload, "valid-lifecycle: exact-ready-content");
        Assert.IsInstanceOfType<RunStartedPayload>(events[1].Payload, "valid-lifecycle: exact-run-started-content");
        Assert.IsInstanceOfType<EligibilityDeterminedPayload>(events[2].Payload, "valid-lifecycle: exact-eligibility-content");
        Assert.IsInstanceOfType<CompletedPayload>(events[3].Payload, "valid-lifecycle: exact-terminal-content");
        Assert.IsNull(events[0].RunId, "valid-lifecycle: ready-null-run-id");
        CollectionAssert.AreEqual(new[] { request.RunId, request.RunId, request.RunId }, events.Skip(1).Select(@event => @event.RunId!.Value).ToArray(), "valid-lifecycle: exact-correlations");
        Assert.IsTrue(sink.Tokens.All(token => token == CancellationToken.None), "valid-lifecycle: exact-none-callback-tokens");
        Assert.AreSame(events[0], sink.Events[0], "valid-lifecycle: ready-reference-retained");
        Assert.AreSame(events[1], sink.Events[1], "valid-lifecycle: run-started-reference-retained");
        Assert.AreSame(events[2], sink.Events[2], "valid-lifecycle: eligibility-reference-retained");
        Assert.AreSame(events[3], sink.Events[3], "valid-lifecycle: terminal-sink-reference-retained");
        Assert.AreSame(events[3], completion.Terminal, "valid-lifecycle: terminal-completion-reference-retained");
        Assert.AreEqual(19, completion.ExitCode, "valid-lifecycle: raw-conflicting-nonzero-exit-retained");
        Assert.IsTrue(completion.ExitObserved, "valid-lifecycle: raw-exit-observed");
        Assert.IsNull(completion.FirstProtocolObservation, "valid-lifecycle: no-classification-added");
        Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyAccepted>(completion.Startup, "valid-lifecycle: startup-retained");
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("raw-conflicting-exit"), completion.StandardErrorTail.Bytes.ToArray(), "valid-lifecycle: exact-stderr");
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Sink_ReadyFailure_IsExactSinkFailedWithZeroTransportAndContinuedFinality()
    {
        var sink = new RecordingSink { FailCall = 1 };
        var request = CreateRequest();
        var (process, _, session) = await LaunchByteSessionAsync(924, request, sink);

        process.StandardOutput.Write(Encoding.UTF8.GetBytes(ReadyFrame() + RunStartedFrame(request.RunId) + EligibilityFrame(request.RunId) + CompletedFrame(request.RunId)));
        Assert.IsInstanceOfType<ChildWorkerStartupObservation.SinkFailed>(await session.Startup, "ready-sink-failure: startup-before-exit");
        await process.StandardInput.DisposeStarted;
        Assert.AreEqual(1, process.StandardInput.DisposeCalls, "ready-sink-failure: accepted-terminal-starts-input-close-without-terminal-callback");
        process.StandardError.Write(DrainSentinel.ToArray());
        process.StandardOutput.Complete();
        process.StandardError.Complete();
        process.Exit(23);
        var completion = await session.Completion;

        Assert.IsInstanceOfType<ChildWorkerStartupObservation.SinkFailed>(completion.Startup, "ready-sink-failure: exact-startup");
        Assert.AreEqual(1, sink.AcceptCalls, "ready-sink-failure: one-callback-only");
        Assert.AreEqual(0, process.StandardInput.WriteCalls, "ready-sink-failure: zero-writes");
        Assert.AreEqual(0, process.StandardInput.FlushCalls, "ready-sink-failure: zero-flushes");
        Assert.IsInstanceOfType<ChildWorkerProtocolObservation.SinkFailure>(completion.FirstProtocolObservation, "ready-sink-failure: exact-first-fact");
        Assert.IsNotNull(completion.Terminal, "ready-sink-failure: accepted-terminal-retained-without-callback");
        Assert.AreEqual(WorkerProtocolV1.CompletedType, completion.Terminal.Type, "ready-sink-failure: terminal-content-retained");
        Assert.AreEqual(23, completion.ExitCode, "ready-sink-failure: exit-retained");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardOutputFinality, "ready-sink-failure: stdout-drained");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardErrorFinality, "ready-sink-failure: stderr-drained");
        CollectionAssert.AreEqual(DrainSentinel, completion.StandardErrorTail.Bytes.ToArray(), "ready-sink-failure: exact-stderr");
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Sink_SynchronousFailure_IsObservedOnceAndSuppressesLaterCallbacks()
    {
        var sink = new ThrowingSink();
        var request = CreateRequest();
        var process = new ByteProcess(926);
        var result = await new ChildWorkerLauncher(new RecordingFactory { Process = process }).LaunchAsync(
            CreateInvocation(), request, sink, TestOptions(), CancellationToken.None);
        var session = Assert.IsInstanceOfType<ChildWorkerLaunchResult.Started>(result, "synchronous-sink-failure: started").Session;

        process.StandardOutput.Write(Encoding.UTF8.GetBytes(ReadyFrame() + RunStartedFrame(request.RunId) + EligibilityFrame(request.RunId) + CompletedFrame(request.RunId)));
        Assert.IsInstanceOfType<ChildWorkerStartupObservation.SinkFailed>(await session.Startup, "synchronous-sink-failure: startup");
        process.StandardOutput.Complete();
        process.StandardError.Complete();
        process.Exit(0);
        var completion = await session.Completion;

        Assert.AreEqual(1, sink.AcceptCalls, "synchronous-sink-failure: one-callback-only");
        Assert.AreEqual(0, process.StandardInput.WriteCalls, "synchronous-sink-failure: zero-writes");
        Assert.AreEqual(0, process.StandardInput.FlushCalls, "synchronous-sink-failure: zero-flushes");
        Assert.IsInstanceOfType<ChildWorkerProtocolObservation.SinkFailure>(completion.FirstProtocolObservation, "synchronous-sink-failure: exact-first-fact");
        Assert.IsNotNull(completion.Terminal, "synchronous-sink-failure: accepted-terminal-retained");
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task FirstFact_SinkFailureIsNotOverwrittenByLaterProtocolFailure()
    {
        var sink = new RecordingSink { FailCall = 1 };
        var (process, _, session) = await LaunchByteSessionAsync(925, sink: sink);

        process.StandardOutput.Write(Encoding.UTF8.GetBytes(ReadyFrame() + "{\n"));
        process.StandardOutput.Complete();
        process.StandardError.Complete();
        process.Exit(0);
        var completion = await session.Completion;

        Assert.IsInstanceOfType<ChildWorkerProtocolObservation.SinkFailure>(completion.FirstProtocolObservation, "sink-first-fact-not-overwritten");
        Assert.AreEqual(1, sink.AcceptCalls, "sink-first-fact: no-later-callback");
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task StandardOutputReadFailure_AfterReadyPreservesAcceptedStartupAndCanonicalExecute()
    {
        var (process, sink, session) = await LaunchByteSessionAsync(927);
        var expectedExecute = Encoding.UTF8.GetBytes("{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"controller-to-worker\",\"category\":\"request\",\"type\":\"execute\",\"sequence\":1,\"timestampUtc\":\"1970-01-01T00:00:00.0000000Z\",\"runId\":\"11111111-1111-1111-1111-111111111111\",\"payload\":{\"trigger\":\"manual\"}}\n");

        process.StandardOutput.Write(Encoding.UTF8.GetBytes(ReadyFrame()));
        Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyAccepted>(await session.Startup, "post-ready-read-failure: startup-ready-accepted");
        process.StandardOutput.Fail();
        process.StandardError.Complete();
        process.Exit(0);
        var completion = await session.Completion;

        Assert.AreEqual(1, sink.AcceptCalls, "post-ready-read-failure: ready-delivered-once");
        CollectionAssert.AreEqual(expectedExecute, process.StandardInput.ToArray(), "post-ready-read-failure: canonical-execute");
        Assert.AreEqual(1, process.StandardInput.WriteCalls, "post-ready-read-failure: execute-written-once");
        Assert.AreEqual(1, process.StandardInput.FlushCalls, "post-ready-read-failure: execute-flushed-once");
        Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyAccepted>(completion.Startup, "post-ready-read-failure: completion-startup-retained");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.ReadFailed>(completion.StandardOutputFinality, "post-ready-read-failure: stdout-finality");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardErrorFinality, "post-ready-read-failure: stderr-finality");
        Assert.IsTrue(completion.ExitObserved, "post-ready-read-failure: exit-observed");
        Assert.AreEqual(0, completion.ExitCode, "post-ready-read-failure: exit-code");
        Assert.IsNull(completion.FirstProtocolObservation, "post-ready-read-failure: no-protocol-or-sink-observation");
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task StandardOutputReadFailure_UsesOnlyStartupAndFinalityChannels()
    {
        var (process, sink, session) = await LaunchByteSessionAsync(926);

        process.StandardOutput.Fail();
        await session.Startup;
        process.StandardError.Complete();
        process.Exit(0);
        var completion = await session.Completion;

        Assert.IsInstanceOfType<ChildWorkerStartupObservation.PreReadyReadFailed>(completion.Startup, "stdout-read-failure: pre-ready-startup");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.ReadFailed>(completion.StandardOutputFinality, "stdout-read-failure: sole-transport-finality");
        Assert.IsNull(completion.FirstProtocolObservation, "stdout-read-failure: no-duplicate-protocol-observation");
        Assert.AreEqual(0, sink.AcceptCalls, "stdout-read-failure: no-sink-callback");
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task FirstFact_ProtocolFailureIsNotOverwrittenByLaterReadFailure()
    {
        var (process, sink, session) = await LaunchByteSessionAsync(926);

        process.StandardOutput.Write(Encoding.UTF8.GetBytes("{\n"));
        var startup = Assert.IsInstanceOfType<ChildWorkerStartupObservation.ProtocolFailure>(
            await session.Startup,
            "protocol-first-fact: startup-exact-failure");
        process.StandardOutput.Fail();
        process.StandardError.Complete();
        process.Exit(0);
        var completion = await session.Completion;

        var first = Assert.IsInstanceOfType<ChildWorkerProtocolObservation.ProtocolFailure>(completion.FirstProtocolObservation, "protocol-first-fact-not-overwritten");
        Assert.AreSame(startup.Failure, first.Failure, "protocol-first-fact: shared-original-failure-reference");
        Assert.AreEqual(
            new WorkerProtocolFailure(WorkerProtocolFailureCode.MalformedJson, "Message is not valid JSON."),
            first.Failure,
            "protocol-first-fact: exact-shared-failure-value");
        Assert.AreEqual("Message is not valid JSON.", first.Failure.Diagnostic, "protocol-first-fact: exact-shared-diagnostic");
        Assert.AreEqual(0, sink.AcceptCalls, "protocol-first-fact: invalid-never-delivered");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.ReadFailed>(completion.StandardOutputFinality, "protocol-first-fact: later-read-failure-finality");
        await session.DisposeAsync();
    }
}
