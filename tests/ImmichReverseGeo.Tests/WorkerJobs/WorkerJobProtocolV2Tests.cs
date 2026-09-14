using System.Text;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProtocol;

namespace ImmichReverseGeo.Tests.WorkerJobs;

[TestClass]
public sealed class WorkerJobProtocolV2Tests
{
    private static readonly Guid JobId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset Started =
        new DateTimeOffset(2026, 9, 8, 10, 11, 12, TimeSpan.Zero).AddTicks(3456789);
    private static readonly DateTimeOffset Ended = Started.AddSeconds(1);

    [TestMethod]
    [TestCategory("Change47")]
    public void ExecuteRequest_HasDeterministicTypedGoldenAndOneIdentity()
    {
        var request = new ProcessingRunRequest(JobId, ProcessingRunTrigger.Manual);
        var message = new WorkerJobControllerMessage(
            WorkerJobProtocolV2.RequestCategory,
            WorkerJobProtocolV2.ExecuteType,
            1,
            Started,
            JobId,
            WorkerJobKind.ProcessAssets,
            new ProcessAssetsExecutePayload(new ProcessAssetsRequest(request)));
        const string expected = "{\"protocol\":\"immich-reversegeo.worker\",\"version\":2,\"direction\":\"controller-to-worker\",\"category\":\"request\",\"type\":\"execute\",\"sequence\":1,\"timestampUtc\":\"2026-09-08T10:11:12.3456789Z\",\"jobId\":\"11111111-2222-3333-4444-555555555555\",\"jobKind\":\"ProcessAssets\",\"payload\":{\"trigger\":\"manual\"}}";

        byte[] bytes = WorkerJobProtocolCodec.SerializeControllerInput(message);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(expected), bytes, "v2-execute-golden");
        WorkerJobControllerParseResult parsed = WorkerJobProtocolCodec.ParseControllerInput(bytes);
        Assert.IsTrue(parsed.IsSuccess);
        Assert.AreEqual(JobId, parsed.Message!.JobId);
        Assert.AreEqual(
            JobId,
            ((ProcessAssetsExecutePayload)parsed.Message.Payload).Request.ProcessingRequest.RunId,
            "envelope-job-id-is-processing-run-id");
    }

    [TestMethod]
    [TestCategory("Change47")]
    public void Ready_AdvertisesOnlyRegisteredKindWithNullJobScopeGolden()
    {
        var message = new WorkerJobOutputMessage(
            WorkerJobProtocolV2.LifecycleCategory,
            WorkerJobProtocolV2.ReadyType,
            1,
            Started,
            null,
            null,
            new WorkerJobReadyPayload([WorkerJobKind.ProcessAssets]));
        const string expected = "{\"protocol\":\"immich-reversegeo.worker\",\"version\":2,\"direction\":\"worker-to-controller\",\"category\":\"lifecycle\",\"type\":\"ready\",\"sequence\":1,\"timestampUtc\":\"2026-09-08T10:11:12.3456789Z\",\"jobId\":null,\"jobKind\":null,\"payload\":{\"supportedJobKinds\":[\"ProcessAssets\"]}}";

        byte[] bytes = WorkerJobProtocolCodec.Serialize(message);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(expected), bytes, "v2-ready-golden");
        WorkerJobProtocolParseResult parsed = WorkerJobProtocolCodec.Parse(bytes);
        Assert.IsTrue(parsed.IsSuccess);
        CollectionAssert.AreEqual(
            new[] { WorkerJobKind.ProcessAssets },
            ((WorkerJobReadyPayload)parsed.Message!.Payload).SupportedJobKinds.ToArray());
    }

    [TestMethod]
    [TestCategory("Change47")]
    public void CompletedTerminal_HasExactlyOneConcreteResultGolden()
    {
        var result = new ProcessAssetsResult("manual", Started, Ended, 3, 2, 1, 0);
        var message = new WorkerJobOutputMessage(
            WorkerJobProtocolV2.TerminalCategory,
            WorkerJobProtocolV2.TerminalType,
            3,
            Ended,
            JobId,
            WorkerJobKind.ProcessAssets,
            new WorkerJobTerminalPayload(
                WorkerJobTerminalOutcome.Completed,
                Started,
                Ended,
                result,
                null));
        const string expected = "{\"protocol\":\"immich-reversegeo.worker\",\"version\":2,\"direction\":\"worker-to-controller\",\"category\":\"terminal\",\"type\":\"terminal\",\"sequence\":3,\"timestampUtc\":\"2026-09-08T10:11:13.3456789Z\",\"jobId\":\"11111111-2222-3333-4444-555555555555\",\"jobKind\":\"ProcessAssets\",\"payload\":{\"outcome\":\"completed\",\"startedAtUtc\":\"2026-09-08T10:11:12.3456789Z\",\"endedAtUtc\":\"2026-09-08T10:11:13.3456789Z\",\"result\":{\"trigger\":\"manual\",\"startedAtUtc\":\"2026-09-08T10:11:12.3456789Z\",\"endedAtUtc\":\"2026-09-08T10:11:13.3456789Z\",\"processedCount\":3,\"updatedCount\":2,\"skippedCount\":1,\"failedCount\":0},\"error\":null}}";

        byte[] bytes = WorkerJobProtocolCodec.Serialize(message);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(expected), bytes, "v2-completed-golden");
        WorkerJobProtocolParseResult parsed = WorkerJobProtocolCodec.Parse(bytes);
        Assert.IsTrue(parsed.IsSuccess);
        Assert.AreEqual(result, ((WorkerJobTerminalPayload)parsed.Message!.Payload).ProcessAssetsResult);
    }

    [TestMethod]
    [TestCategory("Change47")]
    public void Codec_RejectsUnknownFieldsVersionsKindsAndKindPayloadMismatch()
    {
        const string baseline = "{\"protocol\":\"immich-reversegeo.worker\",\"version\":2,\"direction\":\"controller-to-worker\",\"category\":\"request\",\"type\":\"execute\",\"sequence\":1,\"timestampUtc\":\"2026-09-08T10:11:12.3456789Z\",\"jobId\":\"11111111-2222-3333-4444-555555555555\",\"jobKind\":\"ProcessAssets\",\"payload\":{\"trigger\":\"manual\"}}";
        var rows = new[]
        {
            ("wrong-version", baseline.Replace("\"version\":2", "\"version\":1", StringComparison.Ordinal), WorkerProtocolFailureCode.UnsupportedVersion),
            ("unknown-kind", baseline.Replace("ProcessAssets", "Other", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidEnvelope),
            ("reserved-kind-no-schema", baseline.Replace("ProcessAssets", "CacheMutation", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidPayload),
            ("unknown-payload-field", baseline.Replace("\"trigger\":\"manual\"", "\"trigger\":\"manual\",\"extra\":1", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidPayload),
            ("duplicate-payload-field", baseline.Replace("\"trigger\":\"manual\"", "\"trigger\":\"manual\",\"trigger\":\"manual\"", StringComparison.Ordinal), WorkerProtocolFailureCode.InvalidEnvelope)
        };

        foreach (var row in rows)
        {
            WorkerJobControllerParseResult parsed =
                WorkerJobProtocolCodec.ParseControllerInput(Encoding.UTF8.GetBytes(row.Item2));
            Assert.IsFalse(parsed.IsSuccess, row.Item1);
            Assert.AreEqual(row.Item3, parsed.Failure!.Code, row.Item1);
        }
    }

    [TestMethod]
    [TestCategory("Change47")]
    public void HandlerReporterBoundary_RejectsReadyStartedAndTerminalPayloads()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            new WorkerJobHandlerEvent(
                Started,
                new WorkerJobReadyPayload([WorkerJobKind.ProcessAssets])));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new WorkerJobHandlerEvent(
                Started,
                new WorkerJobStartedPayload("manual", Started)));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new WorkerJobHandlerEvent(
                Ended,
                new WorkerJobTerminalPayload(
                    WorkerJobTerminalOutcome.Cancelled,
                    Started,
                    Ended,
                    null,
                    null)));
    }

    [TestMethod]
    [TestCategory("Change47")]
    public void DiagnosticText_Preserves257WhileStructuredSafeErrorRemainsBounded()
    {
        string atLegacyBoundary = new('x', WorkerJobProtocolV2.MaxSafeTextLength);
        string beyondLegacyBoundary = new('x', WorkerJobProtocolV2.MaxSafeTextLength + 1);

        Assert.AreEqual(
            atLegacyBoundary,
            new WorkerJobLogPayload("information", atLegacyBoundary).Message,
            "diagnostic-256-log-exact");
        Assert.AreEqual(
            beyondLegacyBoundary,
            new WorkerJobLogPayload("information", beyondLegacyBoundary).Message,
            "diagnostic-257-log-exact");
        Assert.AreEqual(
            beyondLegacyBoundary,
            new WorkerJobActivityStartedPayload(JobId, beyondLegacyBoundary).Label,
            "diagnostic-257-activity-exact");
        Assert.AreEqual(
            atLegacyBoundary,
            new WorkerJobSafeError(
                "safe-code",
                WorkerJobFailureCategory.Domain,
                atLegacyBoundary).Message,
            "safe-error-256-accepted");
        Assert.ThrowsExactly<ArgumentException>(() =>
            new WorkerJobSafeError(
                "safe-code",
                WorkerJobFailureCategory.Domain,
                beyondLegacyBoundary));
    }

    [TestMethod]
    [TestCategory("Change47")]
    public void ControllerValidator_NonCancellableDescriptorConsumesControlsWithoutCancelling()
    {
        var request = new ProcessingRunRequest(JobId, ProcessingRunTrigger.Manual);
        var descriptor = new WorkerJobDescriptor(
            WorkerJobKind.ProcessAssets,
            typeof(ProcessAssetsRequest),
            typeof(ProcessAssetsResult),
            WorkerJobDescriptors.ProcessAssets.Arbitration with
            {
                IsCancellable = false
            });
        var validator = new WorkerJobControllerInputValidator([descriptor]);
        var execute = new WorkerJobControllerMessage(
            WorkerJobProtocolV2.RequestCategory,
            WorkerJobProtocolV2.ExecuteType,
            1,
            Started,
            JobId,
            WorkerJobKind.ProcessAssets,
            new ProcessAssetsExecutePayload(new ProcessAssetsRequest(request)));
        var firstCancel = new WorkerJobControllerMessage(
            WorkerJobProtocolV2.ControlCategory,
            WorkerJobProtocolV2.CancelType,
            2,
            Started,
            JobId,
            WorkerJobKind.ProcessAssets,
            new WorkerJobCancelPayload());
        var secondCancel = new WorkerJobControllerMessage(
            WorkerJobProtocolV2.ControlCategory,
            WorkerJobProtocolV2.CancelType,
            3,
            Started,
            JobId,
            WorkerJobKind.ProcessAssets,
            new WorkerJobCancelPayload());

        Assert.IsTrue(validator.Validate(execute, true, WorkerJobExecutionPhase.BeforeInvocation).IsSuccess);
        WorkerJobControllerValidationResult first = validator.Validate(
            firstCancel,
            true,
            WorkerJobExecutionPhase.Executing);
        WorkerJobControllerValidationResult second = validator.Validate(
            secondCancel,
            true,
            WorkerJobExecutionPhase.Executing);

        Assert.AreEqual(WorkerJobCancelDisposition.NotCancellableNoOp, first.CancelDisposition);
        Assert.AreEqual(WorkerJobCancelDisposition.NotCancellableNoOp, second.CancelDisposition);
        Assert.AreEqual(3, validator.Snapshot.LastSequence, "noncancellable-controls-advance-sequence");
        Assert.AreSame(descriptor, validator.Snapshot.Descriptor, "noncancellable-accepted-descriptor-retained");
        Assert.IsFalse(validator.Snapshot.CancellationRequested, "noncancellable-cancel-state-remains-false");
    }

    [TestMethod]
    [TestCategory("Change47")]
    public void Validators_RejectUnregisteredBeforeAcceptanceAndEnforceTerminalLifecycle()
    {
        var reserved = new WorkerJobControllerMessage(
            WorkerJobProtocolV2.ControlCategory,
            WorkerJobProtocolV2.CancelType,
            1,
            Started,
            JobId,
            WorkerJobKind.CoordinateLookup,
            new WorkerJobCancelPayload());
        var input = new WorkerJobControllerInputValidator([WorkerJobDescriptors.ProcessAssets]);
        WorkerJobControllerValidationResult rejected = input.Validate(
            reserved,
            isReady: true,
            WorkerJobExecutionPhase.BeforeInvocation);
        Assert.IsFalse(rejected.IsSuccess);
        Assert.AreEqual(WorkerProtocolFailureCode.InvalidLifecycle, rejected.Failure!.Code);
        Assert.IsNull(input.Snapshot.JobId, "rejection-does-not-accept-identity");

        var stream = new WorkerJobOutputStreamValidator(JobId, WorkerJobKind.ProcessAssets);
        Assert.IsTrue(stream.Validate(new WorkerJobOutputMessage(
            WorkerJobProtocolV2.LifecycleCategory,
            WorkerJobProtocolV2.ReadyType,
            1,
            Started,
            null,
            null,
            new WorkerJobReadyPayload([WorkerJobKind.ProcessAssets]))).IsSuccess);
        Assert.IsTrue(stream.Validate(new WorkerJobOutputMessage(
            WorkerJobProtocolV2.LifecycleCategory,
            WorkerJobProtocolV2.JobStartedType,
            2,
            Started,
            JobId,
            WorkerJobKind.ProcessAssets,
            new WorkerJobStartedPayload("manual", Started))).IsSuccess);
        Assert.IsTrue(stream.Validate(new WorkerJobOutputMessage(
            WorkerJobProtocolV2.TerminalCategory,
            WorkerJobProtocolV2.TerminalType,
            3,
            Ended,
            JobId,
            WorkerJobKind.ProcessAssets,
            new WorkerJobTerminalPayload(
                WorkerJobTerminalOutcome.Cancelled,
                Started,
                Ended,
                null,
                null))).IsSuccess);
        Assert.IsNull(stream.FinalizeOutput(hasPartialFrame: false));
        Assert.IsFalse(stream.Validate(new WorkerJobOutputMessage(
            WorkerJobProtocolV2.DiagnosticCategory,
            WorkerJobProtocolV2.LogEmittedType,
            4,
            Ended,
            JobId,
            WorkerJobKind.ProcessAssets,
            new WorkerJobLogPayload("information", "late"))).IsSuccess);
    }
}
