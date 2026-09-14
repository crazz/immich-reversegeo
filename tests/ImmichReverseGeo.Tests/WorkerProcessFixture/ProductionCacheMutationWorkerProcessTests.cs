using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Gadm.Services;
using ImmichReverseGeo.Overture.Services;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests.WorkerProcessFixture;

[TestClass]
[TestCategory("Change51")]
public sealed class ProductionCacheMutationWorkerProcessTests
{
    [TestMethod]
    public async Task ProductionHost_GadmEnsureObservesValidCacheWithoutSourceOrRewrite()
    {
        await using var lease = new WorkerProcessFixtureLease();
        PrepareIdentityCatalog(lease.Root);
        string cachePath = await CreateExistingCacheAsync(lease.Root, "observed-version");
        string sourceMarker = Path.Combine(lease.Root, "network-substituted.marker");
        File.Delete(sourceMarker);
        byte[] originalBytes = File.ReadAllBytes(cachePath);
        DateTime originalWriteTime = File.GetLastWriteTimeUtc(cachePath);
        await AssertSpatialCacheAsync(lease.Root);
        var sink = new RecordingJobSink();
        CacheMutationWorkerJobDispatch dispatch = Dispatch(
            lease.Request.RunId,
            CacheMutationOperation.Ensure,
            "CHE");

        ChildWorkerSession session = await lease.LaunchAsync(
            "real-cache-success",
            dispatch,
            sink,
            InternalWorkerProtocolVersion.V2,
            capture: false);
        ChildWorkerCompletionObservation completion = await lease.CompleteAsync();

        Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyAccepted>(await session.Startup);
        await session.ExecuteRequestAccepted.WaitAsync(WorkerProcessFixtureLease.Watchdog);
        Assert.AreEqual(WorkerProcessExitCodes.Completed, completion.ExitCode);
        Assert.AreEqual(1, sink.Events.Count(value => value.Payload is WorkerJobTerminalPayload));
        var terminal = Assert.IsInstanceOfType<WorkerJobTerminalPayload>(
            completion.JobTerminal!.Payload);
        Assert.AreEqual(WorkerJobTerminalOutcome.Completed, terminal.Outcome);
        CacheMutationResult result = Assert.IsInstanceOfType<CacheMutationResult>(
            terminal.CacheMutationResult);
        Assert.AreEqual(CacheMutationDisposition.AlreadyReady, result.Cache.Disposition);
        Assert.AreEqual(CacheMutationSource.Gadm, result.Cache.Source);
        Assert.AreEqual(CacheMutationOperation.Ensure, result.Cache.Operation);
        Assert.AreEqual("CHE", result.Cache.Iso3);
        Assert.AreEqual("observed-version", result.Cache.Version);
        Assert.AreEqual(
            result.Cache.Version,
            result.Cache.GadmAttribution!.DatasetVersion);
        Assert.AreEqual(
            CacheMutationGadmAttribution.OfficialDatasetName,
            result.Cache.GadmAttribution.DatasetName);
        Assert.AreEqual(
            CacheMutationGadmAttribution.OfficialLicenseUrl,
            result.Cache.GadmAttribution.LicenseUrl);
        Assert.AreEqual(
            CacheMutationGadmAttribution.NonCommercialUseNotice,
            result.Cache.GadmAttribution.UsageNotice);
        CollectionAssert.AreEqual(
            new[]
            {
                CacheMutationProgressStep.CheckingExisting,
                CacheMutationProgressStep.Completed
            },
            sink.Events
                .Select(static value => value.Payload)
                .OfType<CacheMutationProgressPayload>()
                .Select(static value => value.Step)
                .ToArray());
        Assert.AreEqual(0, sink.Events.Count(value =>
            value.Payload is WorkerJobActivityStartedPayload or WorkerJobActivityEndedPayload));
        Assert.IsFalse(File.Exists(sourceMarker));
        CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(cachePath));
        Assert.AreEqual(originalWriteTime, File.GetLastWriteTimeUtc(cachePath));
        await AssertSpatialCacheAsync(lease.Root);
        AssertNoMutationArtifacts(lease.Root);
    }

    [TestMethod]
    public async Task ProductionHost_OvertureEnsureUsesCheckedInSourceAndPublishesReadableCache()
    {
        await using var lease = new WorkerProcessFixtureLease();
        PrepareIdentityCatalog(lease.Root);
        var sink = new RecordingJobSink();
        CacheMutationWorkerJobDispatch dispatch = Dispatch(
            lease.Request.RunId,
            CacheMutationOperation.Ensure,
            "CHE",
            CacheMutationSource.Overture);

        ChildWorkerSession session = await lease.LaunchAsync(
            "real-cache-overture-success",
            dispatch,
            sink,
            InternalWorkerProtocolVersion.V2,
            capture: false);
        ChildWorkerCompletionObservation completion = await lease.CompleteAsync();

        Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyAccepted>(await session.Startup);
        await session.ExecuteRequestAccepted.WaitAsync(WorkerProcessFixtureLease.Watchdog);
        string terminalDiagnostic = sink.Events
            .Select(static value => value.Payload)
            .OfType<WorkerJobTerminalPayload>()
            .Select(static value => value.Error?.Message ?? value.Outcome.ToString())
            .LastOrDefault() ?? "no terminal";
        string progressDiagnostic = string.Join(
            ",",
            sink.Events
                .Select(static value => value.Payload)
                .OfType<CacheMutationProgressPayload>()
                .Select(static value => value.Step));
        string fileDiagnostic = string.Join(
            ",",
            Directory.GetFiles(lease.Root, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(lease.Root, path)));
        Assert.AreEqual(
            WorkerProcessExitCodes.Completed,
            completion.ExitCode,
            $"{completion.StandardErrorTail.Text} Terminal: {terminalDiagnostic}; "
            + $"Progress: {progressDiagnostic}; Files: {fileDiagnostic}");
        Assert.IsNull(completion.FirstProtocolObservation);
        Assert.IsTrue(File.Exists(
            Path.Combine(lease.Root, "remote-export-substituted.marker")));
        Assert.IsFalse(File.Exists(
            Path.Combine(lease.Root, "network-substituted.marker")));
        CollectionAssert.AreEqual(
            new[]
            {
                CacheMutationProgressStep.CheckingExisting,
                CacheMutationProgressStep.PreparingSource,
                CacheMutationProgressStep.Downloading,
                CacheMutationProgressStep.Exporting,
                CacheMutationProgressStep.ValidatingCandidate,
                CacheMutationProgressStep.Publishing,
                CacheMutationProgressStep.Completed
            },
            sink.Events
                .Select(static value => value.Payload)
                .OfType<CacheMutationProgressPayload>()
                .Select(static value => value.Step)
                .ToArray());
        Assert.IsTrue(sink.Events
            .Select(static value => value.Payload)
            .OfType<CacheMutationProgressPayload>()
            .All(static value =>
                value.Source == CacheMutationSource.Overture
                && value.Operation == CacheMutationOperation.Ensure
                && value.Iso3 == "CHE"
                && value.GadmAttribution is null));
        AssertBalancedActivities(sink.Events);
        Assert.AreEqual(1, sink.Events.Count(value => value.Payload is WorkerJobTerminalPayload));
        var terminal = Assert.IsInstanceOfType<WorkerJobTerminalPayload>(
            completion.JobTerminal!.Payload);
        Assert.AreEqual(WorkerJobTerminalOutcome.Completed, terminal.Outcome);
        CacheMutationResult result = Assert.IsInstanceOfType<CacheMutationResult>(
            terminal.CacheMutationResult);
        Assert.AreEqual(CacheMutationDisposition.Published, result.Cache.Disposition);
        Assert.AreEqual(CacheMutationSource.Overture, result.Cache.Source);
        Assert.AreEqual(CacheMutationOperation.Ensure, result.Cache.Operation);
        Assert.AreEqual("CHE", result.Cache.Iso3);
        Assert.AreEqual("2026-09-09.0", result.Cache.Version);
        Assert.AreEqual(1L, result.Cache.RowCount);

        await AssertReadableOvertureCacheAsync(lease.Root, result.Cache.Version);
        AssertNoOvertureMutationArtifacts(lease.Root);
    }

    [TestMethod]
    public async Task ProductionHost_GadmRefreshUsesCheckedInSourceAndPublishesReadableCache()
    {
        await using var lease = new WorkerProcessFixtureLease();
        PrepareIdentityCatalog(lease.Root);
        var sink = new RecordingJobSink();
        var dispatch = Dispatch(lease.Request.RunId, CacheMutationOperation.Refresh, "CHE");

        ChildWorkerSession session = await lease.LaunchAsync(
            "real-cache-success",
            dispatch,
            sink,
            InternalWorkerProtocolVersion.V2,
            capture: false);
        ChildWorkerCompletionObservation completion;
        try
        {
            completion = await lease.CompleteAsync();
        }
        catch (TimeoutException exception)
        {
            string eventSummary = string.Join(
                ",",
                sink.Events.Select(value => value.Payload.GetType().Name));
            string fileSummary = string.Join(
                ",",
                Directory.GetFiles(lease.Root, "*", SearchOption.AllDirectories)
                    .Select(path => Path.GetRelativePath(lease.Root, path)));
            throw new AssertFailedException(
                $"Cache worker did not finalize. Events=[{eventSummary}] Files=[{fileSummary}]",
                exception);
        }

        Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyAccepted>(
            await session.Startup);
        Assert.AreEqual(
            WorkerProcessExitCodes.Completed,
            completion.ExitCode,
            completion.StandardErrorTail.Text);
        Assert.IsNull(completion.FirstProtocolObservation);
        Assert.IsTrue(File.Exists(Path.Combine(lease.Root, "network-substituted.marker")));

        WorkerJobOutputMessage[] events = sink.Events;
        CollectionAssert.AreEqual(
            Enumerable.Range(1, events.Length).Select(static value => (long)value).ToArray(),
            events.Select(static value => value.Sequence).ToArray());
        Assert.IsTrue(events.Skip(1).All(value => value.JobId == dispatch.Context.JobId));
        Assert.IsTrue(events.Skip(1).All(value => value.JobKind == WorkerJobKind.CacheMutation));
        Assert.AreEqual(1, events.Count(value => value.Payload is WorkerJobActivityStartedPayload));
        Assert.AreEqual(1, events.Count(value => value.Payload is WorkerJobActivityEndedPayload));
        Assert.IsTrue(events.Any(value =>
            value.Payload is WorkerJobLogPayload log
            && log.Message.Contains("Starting Gadm cache Refresh for CHE", StringComparison.Ordinal)));
        Assert.IsTrue(events.Any(value =>
            value.Payload is WorkerJobLogPayload log
            && log.Message.Contains("Completed Gadm cache Refresh for CHE", StringComparison.Ordinal)));

        CacheMutationProgressPayload[] progress = events
            .Select(static value => value.Payload)
            .OfType<CacheMutationProgressPayload>()
            .ToArray();
        CollectionAssert.AreEqual(
            new[]
            {
                CacheMutationProgressStep.CheckingExisting,
                CacheMutationProgressStep.PreparingSource,
                CacheMutationProgressStep.Downloading,
                CacheMutationProgressStep.Exporting,
                CacheMutationProgressStep.ValidatingCandidate,
                CacheMutationProgressStep.Publishing,
                CacheMutationProgressStep.Completed
            },
            progress.Select(static value => value.Step).ToArray());
        Assert.IsTrue(progress.All(value =>
            value.Source == CacheMutationSource.Gadm
            && value.Operation == CacheMutationOperation.Refresh
            && value.Iso3 == "CHE"
            && value.GadmAttribution is
            {
                DatasetName: CacheMutationGadmAttribution.OfficialDatasetName,
                LicenseUrl: CacheMutationGadmAttribution.OfficialLicenseUrl,
                UsageNotice: CacheMutationGadmAttribution.NonCommercialUseNotice
            }));

        var terminal = Assert.IsInstanceOfType<WorkerJobTerminalPayload>(
            completion.JobTerminal!.Payload);
        Assert.AreEqual(WorkerJobTerminalOutcome.Completed, terminal.Outcome);
        CacheMutationResult result = Assert.IsInstanceOfType<CacheMutationResult>(
            terminal.CacheMutationResult);
        Assert.AreEqual(CacheMutationDisposition.Published, result.Cache.Disposition);
        Assert.AreEqual(CacheMutationSource.Gadm, result.Cache.Source);
        Assert.AreEqual(CacheMutationOperation.Refresh, result.Cache.Operation);
        Assert.AreEqual("CHE", result.Cache.Iso3);
        Assert.AreEqual(2L, result.Cache.RowCount);
        Assert.AreEqual(result.Cache.Version, result.Cache.GadmAttribution!.DatasetVersion);

        var reader = new GadmDivisionsService(
            NullLogger<GadmDivisionsService>.Instance,
            lease.Root);
        var diagnostics = await reader.FindContainingDivisionAreasAsync(47, 8, "CHE");
        Assert.IsNull(diagnostics.Error);
        Assert.AreEqual(2, diagnostics.Candidates.Count);
        Assert.AreEqual("CHE.1_1", diagnostics.BestMatch!.Id);
        Assert.AreEqual("Region", diagnostics.BestMatch.Name);
        AssertNoMutationArtifacts(lease.Root);
    }

    [TestMethod]
    public async Task ProductionHost_UnknownCatalogCountryIsRejectedBeforeSourceOrCacheSideEffects()
    {
        string root = CreateRoot();
        PrepareIdentityCatalog(root);
        using Process process = CreateProcess(root, "real-cache-success");
        Task<string?>? readyOutput = null;
        Task<string>? remainingOutput = null;
        Task<string>? stderr = null;
        try
        {
            Assert.IsTrue(process.Start());
            stderr = process.StandardError.ReadToEndAsync();
            readyOutput = process.StandardOutput.ReadLineAsync();
            string? ready = await readyOutput.WaitAsync(WorkerProcessFixtureLease.Watchdog);
            AssertReadySupportsCache(ready);
            await WriteExecuteAsync(process, Dispatch(Guid.NewGuid(), CacheMutationOperation.Ensure, "ZZZ"));
            process.StandardInput.Close();
            remainingOutput = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(WorkerProcessFixtureLease.Watchdog);

            Assert.AreEqual(
                WorkerProcessExitCodes.InvalidInput,
                process.ExitCode,
                await stderr.WaitAsync(WorkerProcessFixtureLease.Watchdog));
            Assert.AreEqual(
                string.Empty,
                await remainingOutput.WaitAsync(WorkerProcessFixtureLease.Watchdog),
                "semantic-rejection-has-no-owned-output");
            Assert.IsFalse(File.Exists(Path.Combine(root, "network-substituted.marker")));
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "gadm-divisions")));
        }
        finally
        {
            await DrainProcessAsync(process, readyOutput, remainingOutput, stderr);
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ProductionHost_SourceFailureEmitsOneSafeFailedTerminalAndPreservesCleanup()
    {
        await using var lease = new WorkerProcessFixtureLease();
        PrepareIdentityCatalog(lease.Root);
        var sink = new RecordingJobSink();
        CacheMutationWorkerJobDispatch dispatch = Dispatch(
            lease.Request.RunId,
            CacheMutationOperation.Refresh,
            "CHE");

        ChildWorkerSession session = await lease.LaunchAsync(
            "real-cache-failure",
            dispatch,
            sink,
            InternalWorkerProtocolVersion.V2,
            capture: false);
        ChildWorkerCompletionObservation completion = await lease.CompleteAsync();

        Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyAccepted>(await session.Startup);
        Assert.AreEqual(
            WorkerProcessExitCodes.ExecutorFailure,
            completion.ExitCode,
            completion.StandardErrorTail.Text);
        Assert.IsTrue(File.Exists(Path.Combine(lease.Root, "cache-source-failure.marker")));
        Assert.AreEqual(1, sink.Events.Count(value => value.Payload is WorkerJobTerminalPayload));
        var terminal = Assert.IsInstanceOfType<WorkerJobTerminalPayload>(
            completion.JobTerminal!.Payload);
        Assert.AreEqual(WorkerJobTerminalOutcome.Failed, terminal.Outcome);
        Assert.AreEqual("cache-mutation-failed", terminal.Error!.Code);
        Assert.AreEqual(WorkerJobFailureCategory.Domain, terminal.Error.Category);
        Assert.IsNull(terminal.CacheMutationResult);
        AssertBalancedActivities(sink.Events);
        AssertNoMutationArtifacts(lease.Root);
        Assert.IsFalse(File.Exists(Path.Combine(lease.Root, "gadm-divisions", "CHE.db")));
    }

    [TestMethod]
    public async Task ProductionHost_AcceptedInfrastructureFaultHasNoTerminalAndSettlesExitFive()
    {
        await using var lease = new WorkerProcessFixtureLease();
        PrepareIdentityCatalog(lease.Root);
        var sink = new RecordingJobSink();
        CacheMutationWorkerJobDispatch dispatch = Dispatch(
            lease.Request.RunId,
            CacheMutationOperation.Refresh,
            "CHE");
        ChildWorkerSession session = await lease.LaunchAsync(
            "real-cache-infrastructure-failure",
            dispatch,
            sink,
            InternalWorkerProtocolVersion.V2,
            capture: false);

        Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyAccepted>(await session.Startup);
        await session.ExecuteRequestAccepted.WaitAsync(WorkerProcessFixtureLease.Watchdog);
        ChildWorkerCompletionObservation completion = await lease.CompleteAsync();
        ChildWorkerTerminalPreventingObservation observation = await session
            .FirstTerminalPreventingObservation
            .WaitAsync(WorkerProcessFixtureLease.Watchdog);

        Assert.AreEqual(
            WorkerProcessExitCodes.InfrastructureFailure,
            completion.ExitCode,
            completion.StandardErrorTail.Text);
        Assert.IsNull(completion.JobTerminal);
        var protocolFailure = Assert.IsInstanceOfType<ChildWorkerFaultContainmentReason.ProtocolFailure>(
            observation.Reason);
        Assert.AreEqual(WorkerProtocolFailureCode.InvalidLifecycle, protocolFailure.Failure.Code);
        Assert.AreEqual(WorkerProtocolFailureDetail.MissingTerminal, protocolFailure.Failure.Detail);
        Assert.IsTrue(File.Exists(
            Path.Combine(lease.Root, "cache-infrastructure-fault.marker")));
        Assert.IsFalse(File.Exists(
            Path.Combine(lease.Root, "network-substituted.marker")));
        Assert.IsTrue(sink.Events.Any(value =>
            value.Payload is CacheMutationProgressPayload
            {
                Step: CacheMutationProgressStep.Downloading
            }));
        Assert.AreEqual(0, sink.Events.Count(value => value.Payload is WorkerJobTerminalPayload));
        AssertBalancedActivities(sink.Events);
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(
            completion.StandardOutputFinality);
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(
            completion.StandardErrorFinality);
        Assert.IsFalse(completion.StandardErrorTail.Text.Contains(
            lease.Root,
            StringComparison.Ordinal));
        AssertNoMutationArtifacts(lease.Root);
        Assert.IsFalse(File.Exists(Path.Combine(lease.Root, "gadm-divisions", "CHE.db")));
        Assert.AreSame(completion, await session.EvidenceFinality);
        Assert.AreSame(completion, await session.Settlement);
    }

    [TestMethod]
    public async Task ProductionHost_AcceptedManagedOutputFaultCancelsInputAndSettlesExitSix()
    {
        await using var lease = new WorkerProcessFixtureLease();
        PrepareIdentityCatalog(lease.Root);
        var sink = new RecordingJobSink();
        CacheMutationWorkerJobDispatch dispatch = Dispatch(
            lease.Request.RunId,
            CacheMutationOperation.Refresh,
            "CHE");
        ChildWorkerSession session = await lease.LaunchAsync(
            "real-cache-output-failure",
            dispatch,
            sink,
            InternalWorkerProtocolVersion.V2,
            capture: false);
        var fallbackCleanup = false;

        try
        {
            Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyAccepted>(await session.Startup);
            await session.ExecuteRequestAccepted.WaitAsync(WorkerProcessFixtureLease.Watchdog);
            await ObserveExistingOrCreatedFileAsync(
                Path.Combine(lease.Root, "input-post-execute-read-pending.marker"));
            await ObserveExistingOrCreatedFileAsync(
                Path.Combine(lease.Root, "output-fault-injected.marker"));
            ChildWorkerCompletionObservation completion = await session.Settlement
                .WaitAsync(WorkerProcessFixtureLease.Watchdog);
            ChildWorkerTerminalPreventingObservation observation = await session
                .FirstTerminalPreventingObservation
                .WaitAsync(WorkerProcessFixtureLease.Watchdog);

            Assert.AreEqual(WorkerProcessExitCodes.OutputTransportFailure, completion.ExitCode);
            Assert.IsNull(completion.JobTerminal);
            var protocolFailure = Assert.IsInstanceOfType<ChildWorkerFaultContainmentReason.ProtocolFailure>(
                observation.Reason);
            Assert.AreEqual(WorkerProtocolFailureCode.InvalidLifecycle, protocolFailure.Failure.Code);
            Assert.AreEqual(WorkerProtocolFailureDetail.MissingTerminal, protocolFailure.Failure.Detail);
            Assert.AreEqual(
                "cache-mutation-job-started",
                File.ReadAllText(Path.Combine(lease.Root, "output-fault-injected.marker")));
            Assert.IsTrue(File.Exists(
                Path.Combine(lease.Root, "input-post-execute-read-cancelled.marker")));
            Assert.IsTrue(File.Exists(
                Path.Combine(lease.Root, "input-post-execute-read-finished.marker")));
            Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(
                completion.StandardOutputFinality);
            Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(
                completion.StandardErrorFinality);
            Assert.AreSame(completion, await session.EvidenceFinality);
            Assert.AreSame(completion, await session.Settlement);
            Assert.AreEqual(1, sink.Events.Length);
            Assert.IsInstanceOfType<WorkerJobReadyPayload>(sink.Events[0].Payload);
            Assert.IsFalse(File.Exists(
                Path.Combine(lease.Root, "network-substituted.marker")));
            Assert.IsFalse(Directory.Exists(
                Path.Combine(lease.Root, "gadm-divisions")));
        }
        finally
        {
            if (!session.EvidenceFinality.IsCompleted)
            {
                fallbackCleanup = true;
                await session.RequestStop().WaitAsync(WorkerProcessFixtureLease.Watchdog);
            }
        }

        Assert.IsFalse(fallbackCleanup);
        Assert.AreEqual(0, lease.TreeKillCalls);
    }

    [TestMethod]
    public async Task ProductionHost_UnwritableCacheDirectoryFailsBeforeSourceAndPreservesNoPartialCache()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Assert.Inconclusive("Unix permission bits are required for this fixture.");
            return;
        }

        await using var lease = new WorkerProcessFixtureLease();
        PrepareIdentityCatalog(lease.Root);
        string cacheDirectory = Path.Combine(lease.Root, "gadm-divisions");
        Directory.CreateDirectory(cacheDirectory);
        UnixFileMode originalMode = File.GetUnixFileMode(cacheDirectory);
        File.SetUnixFileMode(
            cacheDirectory,
            UnixFileMode.UserRead
            | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead
            | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead
            | UnixFileMode.OtherExecute);

        try
        {
            string probePath = Path.Combine(cacheDirectory, "write-probe");
            try
            {
                await File.WriteAllTextAsync(probePath, "probe");
                File.Delete(probePath);
                Assert.Inconclusive("The current filesystem did not enforce Unix directory write denial.");
            }
            catch (UnauthorizedAccessException)
            {
            }

            var sink = new RecordingJobSink();
            CacheMutationWorkerJobDispatch dispatch = Dispatch(
                lease.Request.RunId,
                CacheMutationOperation.Refresh,
                "CHE");
            ChildWorkerSession session = await lease.LaunchAsync(
                "real-cache-success",
                dispatch,
                sink,
                InternalWorkerProtocolVersion.V2,
                capture: false);
            ChildWorkerCompletionObservation completion = await lease.CompleteAsync();

            Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyAccepted>(await session.Startup);
            Assert.AreEqual(
                WorkerProcessExitCodes.ExecutorFailure,
                completion.ExitCode,
                completion.StandardErrorTail.Text);
            Assert.AreEqual(1, sink.Events.Count(value => value.Payload is WorkerJobTerminalPayload));
            var terminal = Assert.IsInstanceOfType<WorkerJobTerminalPayload>(
                completion.JobTerminal!.Payload);
            Assert.AreEqual(WorkerJobTerminalOutcome.Failed, terminal.Outcome);
            Assert.AreEqual("cache-mutation-failed", terminal.Error!.Code);
            Assert.IsFalse(
                terminal.Error.Message.Contains(lease.Root, StringComparison.Ordinal),
                "safe error must not reveal the storage path");
            Assert.IsNull(terminal.CacheMutationResult);
            Assert.IsFalse(File.Exists(Path.Combine(lease.Root, "network-substituted.marker")));
            Assert.IsFalse(File.Exists(Path.Combine(cacheDirectory, "CHE.db")));
            AssertNoMutationArtifacts(lease.Root);
        }
        finally
        {
            File.SetUnixFileMode(cacheDirectory, originalMode);
        }
    }

    [TestMethod]
    public async Task ProductionHost_CancellationBeforePublicationEmitsOneCancelledTerminalAndCleansCandidates()
    {
        await using var lease = new WorkerProcessFixtureLease();
        PrepareIdentityCatalog(lease.Root);
        string marker = Path.Combine(lease.Root, "cache-cancellation-ready.marker");
        using var observationCancellation = new CancellationTokenSource();
        Task readyToCancel = ObserveCreatedFileAsync(
            marker,
            observationCancellation.Token);
        var sink = new RecordingJobSink();
        CacheMutationWorkerJobDispatch dispatch = Dispatch(
            lease.Request.RunId,
            CacheMutationOperation.Refresh,
            "CHE");
        try
        {
            ChildWorkerSession session = await lease.LaunchAsync(
                "real-cache-cancellation",
                dispatch,
                sink,
                InternalWorkerProtocolVersion.V2,
                capture: false);
            await readyToCancel.WaitAsync(WorkerProcessFixtureLease.Watchdog);
            ChildWorkerCancellationResult stopped = await session.RequestStop()
                .WaitAsync(WorkerProcessFixtureLease.Watchdog);

            Assert.AreEqual(
                WorkerProcessExitCodes.Cancelled,
                stopped.Completion.ExitCode,
                stopped.Completion.StandardErrorTail.Text);
            Assert.IsTrue(stopped.Facts.RequestAccepted);
            Assert.IsFalse(stopped.Facts.KillAttempted);
            Assert.AreEqual(1, sink.Events.Count(value => value.Payload is WorkerJobTerminalPayload));
            var terminal = Assert.IsInstanceOfType<WorkerJobTerminalPayload>(
                stopped.Completion.JobTerminal!.Payload);
            Assert.AreEqual(WorkerJobTerminalOutcome.Cancelled, terminal.Outcome);
            Assert.IsNull(terminal.CacheMutationResult);
            AssertBalancedActivities(sink.Events);
            AssertNoMutationArtifacts(lease.Root);
            Assert.IsFalse(File.Exists(Path.Combine(lease.Root, "gadm-divisions", "CHE.db")));
        }
        finally
        {
            observationCancellation.Cancel();
            try
            {
                await readyToCancel;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [TestMethod]
    public async Task ProductionHost_ForcedExitLeavesOldCacheAndOwnershipCleanerRemovesOnlyAbandonedCandidates()
    {
        string root = CreateRoot();
        PrepareIdentityCatalog(root);
        string cachePath = await CreateExistingCacheAsync(root, "old-version");
        await AssertSpatialCacheAsync(root);
        byte[] oldBytes = File.ReadAllBytes(cachePath);
        string publicationMarker = Path.Combine(root, "cache-before-publication.marker");
        using var observationCancellation = new CancellationTokenSource();
        Task publicationReached = ObserveCreatedFileAsync(
            publicationMarker,
            observationCancellation.Token);
        using Process process = CreateProcess(root, "real-cache-unresponsive");
        Task<string>? remainingOutput = null;
        Task<string>? stderr = null;
        try
        {
            Assert.IsTrue(process.Start());
            stderr = process.StandardError.ReadToEndAsync();
            string? ready = await process.StandardOutput.ReadLineAsync()
                .WaitAsync(WorkerProcessFixtureLease.Watchdog);
            remainingOutput = process.StandardOutput.ReadToEndAsync();
            AssertReadySupportsCache(ready);
            await WriteExecuteAsync(process, Dispatch(Guid.NewGuid(), CacheMutationOperation.Refresh, "CHE"));
            await publicationReached.WaitAsync(WorkerProcessFixtureLease.Watchdog);

            Assert.AreEqual("old-version", ReadMeta(cachePath, "version"));
            CollectionAssert.AreEqual(oldBytes, File.ReadAllBytes(cachePath));
            await AssertSpatialCacheAsync(root);
            string candidateDirectory = Path.Combine(root, "gadm-divisions");
            Assert.AreEqual(2, Directory.GetFiles(candidateDirectory, "CHE.*.owner").Length);
            Assert.IsTrue(Directory.GetFiles(candidateDirectory, "CHE.*.tmp").Length == 1);
            Assert.IsTrue(Directory.GetFiles(candidateDirectory, "CHE.*.gpkg.download").Length == 1);

            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(WorkerProcessFixtureLease.Watchdog);
            _ = await remainingOutput.WaitAsync(WorkerProcessFixtureLease.Watchdog);
            _ = await stderr.WaitAsync(WorkerProcessFixtureLease.Watchdog);

            var ownership = new CacheCandidateOwnership();
            foreach (string ownerPath in Directory.GetFiles(candidateDirectory, "CHE.*.owner"))
            {
                Assert.IsTrue(
                    ownership.TryCleanupAbandoned(ownerPath[..^".owner".Length]),
                    "post-exit-recognized-candidate-cleaned");
            }

            Assert.AreEqual("old-version", ReadMeta(cachePath, "version"));
            CollectionAssert.AreEqual(oldBytes, File.ReadAllBytes(cachePath));
            await AssertSpatialCacheAsync(root);
            AssertNoMutationArtifacts(root);

            string recoveredPath = await CreateExistingCacheAsync(root, "recovered-version");
            Assert.AreEqual(cachePath, recoveredPath);
            Assert.AreEqual("recovered-version", ReadMeta(recoveredPath, "version"));
            await AssertSpatialCacheAsync(root);
            AssertNoMutationArtifacts(root);
        }
        finally
        {
            observationCancellation.Cancel();
            await TryDrainAsync(publicationReached);
            await DrainProcessAsync(process, null, remainingOutput, stderr);
            Directory.Delete(root, recursive: true);
        }
    }

    private static CacheMutationWorkerJobDispatch Dispatch(
        Guid jobId,
        CacheMutationOperation operation,
        string iso3,
        CacheMutationSource source = CacheMutationSource.Gadm) =>
        new(jobId, new CacheMutationRequest(source, operation, iso3));

    private static async Task WriteExecuteAsync(
        Process process,
        CacheMutationWorkerJobDispatch dispatch)
    {
        var message = new WorkerJobControllerMessage(
            WorkerJobProtocolV2.RequestCategory,
            WorkerJobProtocolV2.ExecuteType,
            1,
            DateTimeOffset.UtcNow,
            dispatch.Context.JobId,
            WorkerJobKind.CacheMutation,
            new CacheMutationExecutePayload(dispatch.Request));
        string frame = Encoding.UTF8.GetString(WorkerJobProtocolCodec.SerializeControllerInput(message));
        await process.StandardInput.WriteAsync(frame + "\n");
        await process.StandardInput.FlushAsync();
    }

    private static void AssertReadySupportsCache(string? line)
    {
        Assert.IsNotNull(line);
        WorkerJobProtocolParseResult parsed = WorkerJobProtocolCodec.Parse(
            Encoding.UTF8.GetBytes(line));
        Assert.IsTrue(parsed.IsSuccess, parsed.Failure?.Diagnostic);
        var ready = Assert.IsInstanceOfType<WorkerJobReadyPayload>(parsed.Message!.Payload);
        CollectionAssert.Contains(ready.SupportedJobKinds.ToArray(), WorkerJobKind.CacheMutation);
    }

    private static Process CreateProcess(string root, string scenario)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = WorkerProcessFixtureLease.FixtureExecutable,
            WorkingDirectory = WorkerProcessFixtureLease.FixtureDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--scenario");
        startInfo.ArgumentList.Add(scenario);
        startInfo.ArgumentList.Add("--resource-root");
        startInfo.ArgumentList.Add(root);
        startInfo.Environment[
            InternalWorkerProtocolVersionSelector.EnvironmentVariableName] = "2";
        return new Process { StartInfo = startInfo };
    }

    private static string CreateRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "immich-reversegeo-worker-fixture",
            Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void PrepareIdentityCatalog(string root)
    {
        string destination = Path.Combine(root, "bundled-data");
        Directory.CreateDirectory(destination);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "data", "iso3166.json"),
            Path.Combine(destination, "iso3166.json"));
    }

    private static async Task<string> CreateExistingCacheAsync(string root, string version)
    {
        using Process process = CreateProcess(root, "real-cache-success");
        Task<string?>? readyOutput = null;
        Task<string>? remainingOutput = null;
        Task<string>? stderr = null;
        try
        {
            Assert.IsTrue(process.Start());
            stderr = process.StandardError.ReadToEndAsync();
            readyOutput = process.StandardOutput.ReadLineAsync();
            string? ready = await readyOutput.WaitAsync(WorkerProcessFixtureLease.Watchdog);
            remainingOutput = process.StandardOutput.ReadToEndAsync();
            AssertReadySupportsCache(ready);
            await WriteExecuteAsync(
                process,
                Dispatch(Guid.NewGuid(), CacheMutationOperation.Refresh, "CHE"));
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(WorkerProcessFixtureLease.Watchdog);
            Assert.AreEqual(
                WorkerProcessExitCodes.Completed,
                process.ExitCode,
                await stderr.WaitAsync(WorkerProcessFixtureLease.Watchdog));
            _ = await remainingOutput.WaitAsync(WorkerProcessFixtureLease.Watchdog);

            string path = Path.Combine(root, "gadm-divisions", "CHE.db");
            using var connection = new SqliteConnection($"Data Source={path};Pooling=false");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE _meta SET value = $version WHERE key = 'version'";
            command.Parameters.AddWithValue("$version", version);
            command.ExecuteNonQuery();
            return path;
        }
        finally
        {
            await DrainProcessAsync(process, readyOutput, remainingOutput, stderr);
        }
    }

    private static async Task DrainProcessAsync(
        Process process,
        Task<string?>? readyOutput,
        Task<string>? remainingOutput,
        Task<string>? stderr)
    {
        try
        {
            process.StandardInput.Close();
        }
        catch (InvalidOperationException)
        {
        }

        KillIfAlive(process);
        await TryDrainAsync(readyOutput);
        if (remainingOutput is not null)
        {
            await TryDrainAsync(remainingOutput);
        }
        else
        {
            try
            {
                await process.StandardOutput.ReadToEndAsync()
                    .WaitAsync(WorkerProcessFixtureLease.Watchdog);
            }
            catch (Exception)
            {
            }
        }

        await TryDrainAsync(stderr);
    }

    private static async Task TryDrainAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.WaitAsync(WorkerProcessFixtureLease.Watchdog);
        }
        catch (Exception)
        {
        }
    }

    private static string ReadMeta(string path, string key)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM _meta WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return (string)command.ExecuteScalar()!;
    }

    private static async Task ObserveCreatedFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            return;
        }

        string directory = Path.GetDirectoryName(path)!;
        string name = Path.GetFileName(path);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(directory, name)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };
        watcher.Created += (_, _) => reached.TrySetResult();
        watcher.Changed += (_, _) => reached.TrySetResult();
        if (File.Exists(path))
        {
            return;
        }

        await reached.Task.WaitAsync(cancellationToken);
    }

    private static async Task ObserveExistingOrCreatedFileAsync(string path)
    {
        using var cancellation = new CancellationTokenSource(
            WorkerProcessFixtureLease.Watchdog);
        await ObserveCreatedFileAsync(path, cancellation.Token);
    }

    private static async Task AssertSpatialCacheAsync(string root)
    {
        var reader = new GadmDivisionsService(
            NullLogger<GadmDivisionsService>.Instance,
            root);
        var diagnostics = await reader.FindContainingDivisionAreasAsync(47, 8, "CHE");
        Assert.IsNull(diagnostics.Error);
        Assert.AreEqual(2, diagnostics.Candidates.Count);
        Assert.AreEqual("CHE.1_1", diagnostics.BestMatch!.Id);
        Assert.AreEqual("Region", diagnostics.BestMatch.Name);
    }

    private static async Task AssertReadableOvertureCacheAsync(
        string root,
        string expectedRelease)
    {
        var places = new OverturePlacesService(
            NullLogger<OverturePlacesService>.Instance,
            root,
            root);
        var divisions = new OvertureDivisionsService(
            NullLogger<OvertureDivisionsService>.Instance,
            places,
            root,
            root,
            static alpha2 => alpha2 == "CH" ? "CHE" : null);
        var diagnostics = await divisions.FindContainingDivisionAreasAsync(
            47,
            8,
            "CH",
            "CHE");

        Assert.IsNull(diagnostics.Error);
        Assert.AreEqual(expectedRelease, diagnostics.Release);
        Assert.AreEqual(1, diagnostics.Candidates.Count);
        Assert.AreEqual("overture:division-area:fixture-che", diagnostics.BestMatch!.Id);
        Assert.AreEqual("Fixture Region", diagnostics.BestMatch.Name);
        Assert.IsTrue(diagnostics.BestMatch.GeometryContainsPoint);
    }

    private static void AssertNoMutationArtifacts(string root)
    {
        string directory = Path.Combine(root, "gadm-divisions");
        if (!Directory.Exists(directory))
        {
            return;
        }

        Assert.AreEqual(0, Directory.GetFiles(directory, "CHE.*.tmp").Length);
        Assert.AreEqual(0, Directory.GetFiles(directory, "CHE.*.gpkg.download").Length);
        Assert.AreEqual(0, Directory.GetFiles(directory, "CHE.*.owner").Length);
    }

    private static void AssertNoOvertureMutationArtifacts(string root)
    {
        string directory = Path.Combine(root, "overture-divisions");
        Assert.IsTrue(File.Exists(Path.Combine(directory, "CHE.db")));
        Assert.AreEqual(0, Directory.GetFiles(directory, "CHE.*.tmp").Length);
        Assert.AreEqual(0, Directory.GetFiles(directory, "CHE.*.owner").Length);
    }

    private static void AssertBalancedActivities(IReadOnlyList<WorkerJobOutputMessage> events)
    {
        Guid[] started = events
            .Select(static value => value.Payload)
            .OfType<WorkerJobActivityStartedPayload>()
            .Select(static value => value.ActivityId)
            .ToArray();
        Guid[] ended = events
            .Select(static value => value.Payload)
            .OfType<WorkerJobActivityEndedPayload>()
            .Select(static value => value.ActivityId)
            .ToArray();
        CollectionAssert.AreEquivalent(started, ended);
    }

    private static void KillIfAlive(Process process)
    {
        try
        {
            if (process.Id != 0 && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private sealed class RecordingJobSink : IWorkerJobEventSink
    {
        private readonly ConcurrentQueue<WorkerJobOutputMessage> _events = new();

        internal WorkerJobOutputMessage[] Events => _events.ToArray();

        public ValueTask AcceptAsync(
            WorkerJobOutputMessage message,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _events.Enqueue(message);
            return ValueTask.CompletedTask;
        }
    }
}
