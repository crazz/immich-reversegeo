using System.Text;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProtocol;

namespace ImmichReverseGeo.Tests.WorkerJobs;

[TestClass]
public sealed class CacheMutationWorkerJobProtocolTests
{
    private static readonly Guid JobId = Guid.Parse("51515151-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset Started =
        new DateTimeOffset(2026, 9, 9, 10, 11, 12, TimeSpan.Zero).AddTicks(3456789);
    private static readonly DateTimeOffset Ended = Started.AddSeconds(1);

    [TestMethod]
    [TestCategory("Change51")]
    public void ExecuteGoldens_CoverEverySourceAndOperationWithEnvelopeOnlyIdentity()
    {
        var rows = new[]
        {
            (CacheMutationSource.Overture, CacheMutationOperation.Ensure, "overture", "ensure"),
            (CacheMutationSource.Overture, CacheMutationOperation.Refresh, "overture", "refresh"),
            (CacheMutationSource.Gadm, CacheMutationOperation.Ensure, "gadm", "ensure"),
            (CacheMutationSource.Gadm, CacheMutationOperation.Refresh, "gadm", "refresh")
        };

        foreach (var (source, operation, sourceToken, operationToken) in rows)
        {
            WorkerJobControllerMessage message = Execute(source, operation, "CHE");
            string expected =
                "{\"protocol\":\"immich-reversegeo.worker\",\"version\":2,\"direction\":\"controller-to-worker\",\"category\":\"request\",\"type\":\"execute\",\"sequence\":1,\"timestampUtc\":\"2026-09-09T10:11:12.3456789Z\",\"jobId\":\"51515151-2222-3333-4444-555555555555\",\"jobKind\":\"CacheMutation\",\"payload\":{\"source\":\""
                + sourceToken
                + "\",\"operation\":\""
                + operationToken
                + "\",\"iso3\":\"CHE\"}}";

            byte[] encoded = WorkerJobProtocolCodec.SerializeControllerInput(message);
            Assert.AreEqual(expected, Encoding.UTF8.GetString(encoded), $"{source}-{operation}");
            WorkerJobControllerParseResult parsed = WorkerJobProtocolCodec.ParseControllerInput(encoded);
            Assert.IsTrue(parsed.IsSuccess, $"{source}-{operation}");
            CacheMutationRequest request =
                Assert.IsInstanceOfType<CacheMutationExecutePayload>(parsed.Message!.Payload).Request;
            Assert.AreEqual(source, request.Source);
            Assert.AreEqual(operation, request.Operation);
            Assert.AreEqual("CHE", request.Iso3);
            Assert.DoesNotContain("jobId", expected[(expected.IndexOf("\"payload\"", StringComparison.Ordinal))..]);
        }
    }

    [TestMethod]
    [TestCategory("Change51")]
    public void ProgressGoldens_CarryStableGadmAttributionAndClosedSteps()
    {
        CacheMutationGadmAttribution attribution = GadmAttribution();
        WorkerJobOutputMessage overture = Progress(
            3,
            new CacheMutationProgressPayload(
                CacheMutationProgressStep.ValidatingCandidate,
                CacheMutationSource.Overture,
                CacheMutationOperation.Refresh,
                "CHE",
                "Validating cache candidate."));
        WorkerJobOutputMessage gadm = Progress(
            4,
            new CacheMutationProgressPayload(
                CacheMutationProgressStep.Exporting,
                CacheMutationSource.Gadm,
                CacheMutationOperation.Ensure,
                "CHE",
                "Exporting GADM areas.",
                attribution));
        const string overtureGolden = "{\"protocol\":\"immich-reversegeo.worker\",\"version\":2,\"direction\":\"worker-to-controller\",\"category\":\"progress\",\"type\":\"progress-changed\",\"sequence\":3,\"timestampUtc\":\"2026-09-09T10:11:12.3456789Z\",\"jobId\":\"51515151-2222-3333-4444-555555555555\",\"jobKind\":\"CacheMutation\",\"payload\":{\"step\":\"validating-candidate\",\"source\":\"overture\",\"operation\":\"refresh\",\"iso3\":\"CHE\",\"message\":\"Validating cache candidate.\",\"gadmAttribution\":null}}";
        const string gadmGolden = "{\"protocol\":\"immich-reversegeo.worker\",\"version\":2,\"direction\":\"worker-to-controller\",\"category\":\"progress\",\"type\":\"progress-changed\",\"sequence\":4,\"timestampUtc\":\"2026-09-09T10:11:12.3456789Z\",\"jobId\":\"51515151-2222-3333-4444-555555555555\",\"jobKind\":\"CacheMutation\",\"payload\":{\"step\":\"exporting\",\"source\":\"gadm\",\"operation\":\"ensure\",\"iso3\":\"CHE\",\"message\":\"Exporting GADM areas.\",\"gadmAttribution\":{\"datasetName\":\"GADM\",\"datasetVersion\":\"4.1\",\"licenseUrl\":\"https://gadm.org/license.html\",\"usageNotice\":\"GADM data is available for academic and other non-commercial use.\"}}}";

        Assert.AreEqual(overtureGolden, Encoding.UTF8.GetString(WorkerJobProtocolCodec.Serialize(overture)));
        Assert.AreEqual(gadmGolden, Encoding.UTF8.GetString(WorkerJobProtocolCodec.Serialize(gadm)));
        Assert.IsTrue(WorkerJobProtocolCodec.Parse(Encoding.UTF8.GetBytes(overtureGolden)).IsSuccess);
        Assert.IsTrue(WorkerJobProtocolCodec.Parse(Encoding.UTF8.GetBytes(gadmGolden)).IsSuccess);
    }

    [TestMethod]
    [TestCategory("Change51")]
    [DataRow(CacheMutationProgressStep.CheckingExisting, "checking-existing")]
    [DataRow(CacheMutationProgressStep.PreparingSource, "preparing-source")]
    [DataRow(CacheMutationProgressStep.Downloading, "downloading")]
    [DataRow(CacheMutationProgressStep.Exporting, "exporting")]
    [DataRow(CacheMutationProgressStep.ValidatingCandidate, "validating-candidate")]
    [DataRow(CacheMutationProgressStep.Publishing, "publishing")]
    [DataRow(CacheMutationProgressStep.Completed, "completed")]
    public void ProgressStepMatrix_UsesOnlyCanonicalTokens(
        CacheMutationProgressStep step,
        string token)
    {
        WorkerJobOutputMessage message = Progress(
            3,
            new CacheMutationProgressPayload(
                step,
                CacheMutationSource.Overture,
                CacheMutationOperation.Refresh,
                "CHE",
                "Bounded progress."));
        byte[] encoded = WorkerJobProtocolCodec.Serialize(message);
        string expected =
            "{\"protocol\":\"immich-reversegeo.worker\",\"version\":2,\"direction\":\"worker-to-controller\",\"category\":\"progress\",\"type\":\"progress-changed\",\"sequence\":3,\"timestampUtc\":\"2026-09-09T10:11:12.3456789Z\",\"jobId\":\"51515151-2222-3333-4444-555555555555\",\"jobKind\":\"CacheMutation\",\"payload\":{\"step\":\""
            + token
            + "\",\"source\":\"overture\",\"operation\":\"refresh\",\"iso3\":\"CHE\",\"message\":\"Bounded progress.\",\"gadmAttribution\":null}}";

        Assert.AreEqual(expected, Encoding.UTF8.GetString(encoded));
        WorkerJobProtocolParseResult parsed =
            WorkerJobProtocolCodec.Parse(Encoding.UTF8.GetBytes(expected));
        Assert.IsTrue(parsed.IsSuccess);
        Assert.AreEqual(
            step,
            Assert.IsInstanceOfType<CacheMutationProgressPayload>(parsed.Message!.Payload).Step);
    }

    [TestMethod]
    [TestCategory("Change51")]
    [DataRow(CacheMutationSource.Overture, CacheMutationOperation.Ensure, CacheMutationDisposition.AlreadyReady)]
    [DataRow(CacheMutationSource.Overture, CacheMutationOperation.Ensure, CacheMutationDisposition.Published)]
    [DataRow(CacheMutationSource.Overture, CacheMutationOperation.Refresh, CacheMutationDisposition.Published)]
    [DataRow(CacheMutationSource.Gadm, CacheMutationOperation.Ensure, CacheMutationDisposition.AlreadyReady)]
    [DataRow(CacheMutationSource.Gadm, CacheMutationOperation.Ensure, CacheMutationDisposition.Published)]
    [DataRow(CacheMutationSource.Gadm, CacheMutationOperation.Refresh, CacheMutationDisposition.Published)]
    public void CompletedResultMatrix_RoundTripsEveryLegitimateSourceOperationDisposition(
        CacheMutationSource source,
        CacheMutationOperation operation,
        CacheMutationDisposition disposition)
    {
        CacheMutationGadmAttribution? attribution = source == CacheMutationSource.Gadm
            ? GadmAttribution()
            : null;
        var result = new CacheMutationResult(
            Started,
            Ended,
            SourceResult(source, operation, disposition, "4.1", attribution));
        WorkerJobOutputMessage message = WorkerJobProtocolMapper.Terminal(
            new WorkerJobContext(JobId, WorkerJobKind.CacheMutation, WorkerJobRequestOrigin.Manual),
            new WorkerJobTerminalPayload(
                WorkerJobTerminalOutcome.Completed,
                Started,
                Ended,
                null,
                null,
                result,
                null),
            3);

        byte[] encoded = WorkerJobProtocolCodec.Serialize(message);
        string sourceToken = source == CacheMutationSource.Overture ? "overture" : "gadm";
        string operationToken = operation == CacheMutationOperation.Ensure ? "ensure" : "refresh";
        string dispositionToken = disposition == CacheMutationDisposition.AlreadyReady
            ? "already-ready"
            : "published";
        string attributionJson = source == CacheMutationSource.Gadm
            ? "{\"datasetName\":\"GADM\",\"datasetVersion\":\"4.1\",\"licenseUrl\":\"https://gadm.org/license.html\",\"usageNotice\":\"GADM data is available for academic and other non-commercial use.\"}"
            : "null";
        string expected =
            "{\"protocol\":\"immich-reversegeo.worker\",\"version\":2,\"direction\":\"worker-to-controller\",\"category\":\"terminal\",\"type\":\"terminal\",\"sequence\":3,\"timestampUtc\":\"2026-09-09T10:11:13.3456789Z\",\"jobId\":\"51515151-2222-3333-4444-555555555555\",\"jobKind\":\"CacheMutation\",\"payload\":{\"outcome\":\"completed\",\"startedAtUtc\":\"2026-09-09T10:11:12.3456789Z\",\"endedAtUtc\":\"2026-09-09T10:11:13.3456789Z\",\"result\":{\"startedAtUtc\":\"2026-09-09T10:11:12.3456789Z\",\"endedAtUtc\":\"2026-09-09T10:11:13.3456789Z\",\"source\":\""
            + sourceToken
            + "\",\"operation\":\""
            + operationToken
            + "\",\"iso3\":\"CHE\",\"disposition\":\""
            + dispositionToken
            + "\",\"rowCount\":1,\"downloadedAtUtc\":\"2026-09-09T10:11:12.3456789Z\",\"fileSizeBytes\":1024,\"version\":\"4.1\",\"gadmAttribution\":"
            + attributionJson
            + "},\"error\":null}}";

        Assert.AreEqual(expected, Encoding.UTF8.GetString(encoded));
        WorkerJobProtocolParseResult parsed =
            WorkerJobProtocolCodec.Parse(Encoding.UTF8.GetBytes(expected));
        Assert.IsTrue(parsed.IsSuccess);
        Assert.AreEqual(
            result,
            Assert.IsInstanceOfType<WorkerJobTerminalPayload>(parsed.Message!.Payload)
                .CacheMutationResult);
    }

    [TestMethod]
    [TestCategory("Change51")]
    public void NonSuccessTerminalGoldens_HaveNoCacheResult()
    {
        var context = new WorkerJobContext(
            JobId,
            WorkerJobKind.CacheMutation,
            WorkerJobRequestOrigin.Manual);
        WorkerJobOutputMessage cancelled = WorkerJobProtocolMapper.Terminal(
            context,
            new WorkerJobTerminalPayload(
                WorkerJobTerminalOutcome.Cancelled,
                Started,
                Ended,
                null,
                null,
                null,
                null),
            3);
        WorkerJobOutputMessage failed = WorkerJobProtocolMapper.Terminal(
            context,
            new WorkerJobTerminalPayload(
                WorkerJobTerminalOutcome.Failed,
                Started,
                Ended,
                null,
                null,
                null,
                new WorkerJobSafeError(
                    "cache-source",
                    WorkerJobFailureCategory.Dependency,
                    "Cache source failed.")),
            3);
        const string cancelledGolden = "{\"protocol\":\"immich-reversegeo.worker\",\"version\":2,\"direction\":\"worker-to-controller\",\"category\":\"terminal\",\"type\":\"terminal\",\"sequence\":3,\"timestampUtc\":\"2026-09-09T10:11:13.3456789Z\",\"jobId\":\"51515151-2222-3333-4444-555555555555\",\"jobKind\":\"CacheMutation\",\"payload\":{\"outcome\":\"cancelled\",\"startedAtUtc\":\"2026-09-09T10:11:12.3456789Z\",\"endedAtUtc\":\"2026-09-09T10:11:13.3456789Z\",\"result\":null,\"error\":null}}";
        const string failedGolden = "{\"protocol\":\"immich-reversegeo.worker\",\"version\":2,\"direction\":\"worker-to-controller\",\"category\":\"terminal\",\"type\":\"terminal\",\"sequence\":3,\"timestampUtc\":\"2026-09-09T10:11:13.3456789Z\",\"jobId\":\"51515151-2222-3333-4444-555555555555\",\"jobKind\":\"CacheMutation\",\"payload\":{\"outcome\":\"failed\",\"startedAtUtc\":\"2026-09-09T10:11:12.3456789Z\",\"endedAtUtc\":\"2026-09-09T10:11:13.3456789Z\",\"result\":null,\"error\":{\"code\":\"cache-source\",\"category\":\"dependency\",\"message\":\"Cache source failed.\"}}}";

        Assert.AreEqual(cancelledGolden, Encoding.UTF8.GetString(WorkerJobProtocolCodec.Serialize(cancelled)));
        Assert.AreEqual(failedGolden, Encoding.UTF8.GetString(WorkerJobProtocolCodec.Serialize(failed)));
        Assert.IsTrue(WorkerJobProtocolCodec.Parse(Encoding.UTF8.GetBytes(cancelledGolden)).IsSuccess);
        Assert.IsTrue(WorkerJobProtocolCodec.Parse(Encoding.UTF8.GetBytes(failedGolden)).IsSuccess);
    }

    [TestMethod]
    [TestCategory("Change51")]
    public void CompletedResultGolden_UsesAuthoritativeMetadataAndMatchingAttribution()
    {
        var result = new CacheMutationResult(
            Started,
            Ended,
            new CacheMutationSourceResult(
                CacheMutationSource.Gadm,
                CacheMutationOperation.Refresh,
                "CHE",
                CacheMutationDisposition.Published,
                7,
                Started,
                4096,
                "4.1",
                GadmAttribution()));
        var message = new WorkerJobOutputMessage(
            WorkerJobProtocolV2.TerminalCategory,
            WorkerJobProtocolV2.TerminalType,
            5,
            Ended,
            JobId,
            WorkerJobKind.CacheMutation,
            new WorkerJobTerminalPayload(
                WorkerJobTerminalOutcome.Completed,
                Started,
                Ended,
                null,
                null,
                result,
                null));
        const string expected = "{\"protocol\":\"immich-reversegeo.worker\",\"version\":2,\"direction\":\"worker-to-controller\",\"category\":\"terminal\",\"type\":\"terminal\",\"sequence\":5,\"timestampUtc\":\"2026-09-09T10:11:13.3456789Z\",\"jobId\":\"51515151-2222-3333-4444-555555555555\",\"jobKind\":\"CacheMutation\",\"payload\":{\"outcome\":\"completed\",\"startedAtUtc\":\"2026-09-09T10:11:12.3456789Z\",\"endedAtUtc\":\"2026-09-09T10:11:13.3456789Z\",\"result\":{\"startedAtUtc\":\"2026-09-09T10:11:12.3456789Z\",\"endedAtUtc\":\"2026-09-09T10:11:13.3456789Z\",\"source\":\"gadm\",\"operation\":\"refresh\",\"iso3\":\"CHE\",\"disposition\":\"published\",\"rowCount\":7,\"downloadedAtUtc\":\"2026-09-09T10:11:12.3456789Z\",\"fileSizeBytes\":4096,\"version\":\"4.1\",\"gadmAttribution\":{\"datasetName\":\"GADM\",\"datasetVersion\":\"4.1\",\"licenseUrl\":\"https://gadm.org/license.html\",\"usageNotice\":\"GADM data is available for academic and other non-commercial use.\"}},\"error\":null}}";

        byte[] encoded = WorkerJobProtocolCodec.Serialize(message);
        Assert.AreEqual(expected, Encoding.UTF8.GetString(encoded));
        WorkerJobProtocolParseResult parsed = WorkerJobProtocolCodec.Parse(encoded);
        Assert.IsTrue(parsed.IsSuccess);
        Assert.AreEqual(result, Assert.IsInstanceOfType<WorkerJobTerminalPayload>(parsed.Message!.Payload).CacheMutationResult);
    }

    [TestMethod]
    [TestCategory("Change51")]
    public void Contracts_RejectImpossibleDispositionAndAttributionCombinations()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new CacheMutationGadmAttribution(
            "not-gadm",
            "4.1",
            CacheMutationGadmAttribution.OfficialLicenseUrl,
            CacheMutationGadmAttribution.NonCommercialUseNotice));
        Assert.ThrowsExactly<ArgumentException>(() => SourceResult(
            CacheMutationSource.Overture,
            CacheMutationOperation.Refresh,
            CacheMutationDisposition.AlreadyReady,
            "2026-09-09",
            null));
        Assert.ThrowsExactly<ArgumentException>(() => SourceResult(
            CacheMutationSource.Gadm,
            CacheMutationOperation.Ensure,
            CacheMutationDisposition.Published,
            "4.2",
            GadmAttribution()));
        Assert.ThrowsExactly<ArgumentException>(() => new CacheMutationProgressPayload(
            CacheMutationProgressStep.Downloading,
            CacheMutationSource.Gadm,
            CacheMutationOperation.Ensure,
            "CHE",
            "Downloading."));
    }

    [TestMethod]
    [TestCategory("Change51")]
    public void Codec_RejectsMalformedIso3DiscriminatorsPropertiesAndBounds()
    {
        string valid = Encoding.UTF8.GetString(WorkerJobProtocolCodec.SerializeControllerInput(
            Execute(CacheMutationSource.Overture, CacheMutationOperation.Ensure, "CHE")));
        var rows = new[]
        {
            valid.Replace("\"CHE\"", "\"che\"", StringComparison.Ordinal),
            valid.Replace("\"CHE\"", "\" CHE\"", StringComparison.Ordinal),
            valid.Replace("\"CHE\"", "\"CH\"", StringComparison.Ordinal),
            valid.Replace("\"CHE\"", "\"CHEE\"", StringComparison.Ordinal),
            valid.Replace("\"CHE\"", "\"CHÉ\"", StringComparison.Ordinal),
            valid.Replace("\"overture\"", "\"future\"", StringComparison.Ordinal),
            valid.Replace("\"ensure\"", "\"delete\"", StringComparison.Ordinal),
            valid.Replace("\"iso3\":\"CHE\"", "\"iso3\":\"CHE\",\"path\":\"/tmp/x\"", StringComparison.Ordinal),
            valid.Replace("\"iso3\":\"CHE\"", "\"iso3\":\"CHE\",\"url\":\"https://invalid\"", StringComparison.Ordinal),
            valid.Replace("\"iso3\":\"CHE\"", "\"iso3\":\"CHE\",\"release\":\"future\"", StringComparison.Ordinal),
            valid.Replace("\"iso3\":\"CHE\"", "\"iso3\":\"CHE\",\"options\":{}", StringComparison.Ordinal),
            valid.Replace("\"jobKind\":\"CacheMutation\"", "\"jobKind\":\"CoordinateLookup\"", StringComparison.Ordinal)
        };

        foreach (string row in rows)
        {
            WorkerJobControllerParseResult parsed =
                WorkerJobProtocolCodec.ParseControllerInput(Encoding.UTF8.GetBytes(row));
            Assert.IsFalse(parsed.IsSuccess, row);
        }

        var result = new CacheMutationResult(
            Started,
            Ended,
            SourceResult(
                CacheMutationSource.Overture,
                CacheMutationOperation.Ensure,
                CacheMutationDisposition.AlreadyReady,
                "4.1",
                null));
        string completed = Encoding.UTF8.GetString(WorkerJobProtocolCodec.Serialize(
            WorkerJobProtocolMapper.Terminal(
                new WorkerJobContext(
                    JobId,
                    WorkerJobKind.CacheMutation,
                    WorkerJobRequestOrigin.Manual),
                new WorkerJobTerminalPayload(
                    WorkerJobTerminalOutcome.Completed,
                    Started,
                    Ended,
                    null,
                    null,
                    result,
                    null),
                3)));
        string[] invalidResults =
        [
            completed.Replace("\"rowCount\":1", "\"rowCount\":0", StringComparison.Ordinal),
            completed.Replace("\"fileSizeBytes\":1024", "\"fileSizeBytes\":0", StringComparison.Ordinal)
        ];
        foreach (string invalidResult in invalidResults)
        {
            Assert.IsFalse(
                WorkerJobProtocolCodec.Parse(Encoding.UTF8.GetBytes(invalidResult)).IsSuccess,
                invalidResult);
        }
    }

    [TestMethod]
    [TestCategory("Change51")]
    public void OutputValidator_RejectsProgressAndResultThatDifferFromAcceptedRequest()
    {
        var expected = new CacheMutationRequest(
            CacheMutationSource.Overture,
            CacheMutationOperation.Refresh,
            "CHE");
        var stream = new WorkerJobOutputStreamValidator(
            JobId,
            WorkerJobKind.CacheMutation,
            expected);
        Assert.IsTrue(stream.Validate(new WorkerJobOutputMessage(
            WorkerJobProtocolV2.LifecycleCategory,
            WorkerJobProtocolV2.ReadyType,
            1,
            Started,
            null,
            null,
            new WorkerJobReadyPayload(WorkerJobDescriptors.Registered.Select(static descriptor => descriptor.Kind)))).IsSuccess);
        Assert.IsTrue(stream.Validate(new WorkerJobOutputMessage(
            WorkerJobProtocolV2.LifecycleCategory,
            WorkerJobProtocolV2.JobStartedType,
            2,
            Started,
            JobId,
            WorkerJobKind.CacheMutation,
            new WorkerJobStartedPayload("manual", Started))).IsSuccess);

        WorkerJobOutputValidationResult wrongProgress = stream.Validate(Progress(
            3,
            new CacheMutationProgressPayload(
                CacheMutationProgressStep.CheckingExisting,
                CacheMutationSource.Overture,
                CacheMutationOperation.Ensure,
                "CHE",
                "Checking.")));

        Assert.IsFalse(wrongProgress.IsSuccess);
        Assert.AreEqual(WorkerProtocolFailureCode.InvalidCorrelation, wrongProgress.Failure!.Code);
    }

    [TestMethod]
    [TestCategory("Change51")]
    public void RegisteredDescriptor_IsCanonicalManualHeavyCacheMaintenance()
    {
        WorkerJobDescriptor descriptor = WorkerJobDescriptors.CacheMutation;
        Assert.AreEqual(WorkerJobKind.CacheMutation, descriptor.Kind);
        Assert.AreEqual(typeof(CacheMutationRequest), descriptor.RequestType);
        Assert.AreEqual(typeof(CacheMutationResult), descriptor.ResultType);
        Assert.AreEqual(WorkerJobCapabilityFamily.CacheMaintenance, descriptor.Arbitration.CapabilityFamily);
        Assert.AreEqual(WorkerJobResourceClass.ExclusiveHeavyWorker, descriptor.Arbitration.ResourceClass);
        Assert.IsTrue(descriptor.Arbitration.IsHeavy);
        Assert.IsTrue(descriptor.Arbitration.IsCancellable);
        Assert.IsTrue(descriptor.Arbitration.IsGeodataBearing);
        CollectionAssert.Contains(WorkerJobDescriptors.Registered.ToArray(), descriptor);
        Assert.AreEqual(WorkerJobRequestOrigin.Manual,
            new CacheMutationWorkerJobDispatch(JobId, new CacheMutationRequest(
                CacheMutationSource.Overture,
                CacheMutationOperation.Ensure,
                "CHE")).Context.Origin);
    }

    private static WorkerJobControllerMessage Execute(
        CacheMutationSource source,
        CacheMutationOperation operation,
        string iso3) =>
        new(
            WorkerJobProtocolV2.RequestCategory,
            WorkerJobProtocolV2.ExecuteType,
            1,
            Started,
            JobId,
            WorkerJobKind.CacheMutation,
            new CacheMutationExecutePayload(new CacheMutationRequest(source, operation, iso3)));

    private static WorkerJobOutputMessage Progress(long sequence, CacheMutationProgressPayload payload) =>
        new(
            WorkerJobProtocolV2.ProgressCategory,
            WorkerJobProtocolV2.ProgressChangedType,
            sequence,
            Started,
            JobId,
            WorkerJobKind.CacheMutation,
            payload);

    private static CacheMutationGadmAttribution GadmAttribution() =>
        new(
            CacheMutationGadmAttribution.OfficialDatasetName,
            "4.1",
            CacheMutationGadmAttribution.OfficialLicenseUrl,
            CacheMutationGadmAttribution.NonCommercialUseNotice);

    private static CacheMutationSourceResult SourceResult(
        CacheMutationSource source,
        CacheMutationOperation operation,
        CacheMutationDisposition disposition,
        string version,
        CacheMutationGadmAttribution? attribution) =>
        new(
            source,
            operation,
            "CHE",
            disposition,
            1,
            Started,
            1024,
            version,
            attribution);
}
