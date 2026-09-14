using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Web.ChildWorkerLaunching;

namespace ImmichReverseGeo.Tests.WorkerProcessFixture;

[TestClass]
[TestCategory("Change48")]
public sealed class ProductionCoordinateLookupWorkerProcessTests
{
    [TestMethod]
    [DataRow("real-coordinate-success", true, true, true, CoordinateLookupCountryStatus.Matched, CoordinateLookupSourceState.Ready)]
    [DataRow("real-coordinate-success", false, false, false, CoordinateLookupCountryStatus.Matched, CoordinateLookupSourceState.Ready)]
    [DataRow("real-coordinate-no-country", true, true, true, CoordinateLookupCountryStatus.NoMatch, CoordinateLookupSourceState.Skipped)]
    [DataRow("real-coordinate-degraded", true, true, true, CoordinateLookupCountryStatus.Matched, CoordinateLookupSourceState.Unavailable)]
    public async Task ProductionHost_CompletedRowsUseActualHandlerOperationAndTypedTerminal(
        string scenario,
        bool airport,
        bool places,
        bool gadm,
        CoordinateLookupCountryStatus expectedCountry,
        CoordinateLookupSourceState expectedOverture)
    {
        await using var lease = new WorkerProcessFixtureLease();
        var sink = new RecordingJobSink();
        CoordinateLookupWorkerJobDispatch dispatch = Dispatch(
            lease,
            airport,
            places,
            gadm);

        ChildWorkerSession session = await lease.LaunchAsync(
            scenario,
            dispatch,
            sink,
            InternalWorkerProtocolVersion.V2,
            capture: false);
        ChildWorkerCompletionObservation completion = await lease.CompleteAsync();

        Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyAccepted>(await session.Startup);
        Assert.AreEqual(
            WorkerProcessExitCodes.Completed,
            completion.ExitCode,
            completion.StandardErrorTail.Text);
        Assert.IsNull(completion.FirstProtocolObservation);
        AssertTypedSequence(sink.Events, dispatch.Context.JobId);
        Assert.AreEqual(1, sink.Events.Count(IsTerminal));
        AssertBalancedActivities(sink.Events);
        AssertMarker(lease, "source-called.marker");
        AssertNoMarker(lease, "persistence-accessed.marker");

        var terminal = Assert.IsInstanceOfType<WorkerJobTerminalPayload>(
            completion.JobTerminal!.Payload);
        Assert.AreEqual(WorkerJobTerminalOutcome.Completed, terminal.Outcome);
        Assert.IsNull(terminal.ProcessAssetsResult);
        CoordinateLookupResult result = Assert.IsInstanceOfType<CoordinateLookupResult>(
            terminal.CoordinateLookupResult);
        Assert.AreEqual(expectedCountry, result.Country.Status);
        Assert.AreEqual(expectedOverture, result.OvertureDivisions.State);

        if (scenario == "real-coordinate-no-country")
        {
            Assert.AreEqual(CoordinateLookupSourceState.Skipped, result.GadmDivisions.State);
            Assert.AreEqual(CoordinateLookupSourceState.Skipped, result.AirportInfrastructure.State);
            Assert.AreEqual(CoordinateLookupSourceState.Skipped, result.LiveOverturePlaces.State);
            AssertNoOptionalSourceMarkers(lease);
            Assert.AreEqual(0, sink.Events.Count(IsActivity));
        }
        else if (airport || places || gadm)
        {
            AssertMarker(lease, "overture-cache-called.marker");
            AssertMarker(lease, "gadm-cache-called.marker");
            AssertMarker(lease, "airport-called.marker");
            AssertMarker(lease, "places-called.marker");
        }

        if (scenario == "real-coordinate-success")
        {
            AssertMarker(lease, "overture-division-called.marker");
            Assert.AreEqual("United States", result.FinalLocation.Country!.Value);
            if (gadm)
            {
                AssertMarker(lease, "gadm-division-called.marker");
                Assert.AreEqual("Fixture State", result.FinalLocation.State!.Value);
            }
            else
            {
                AssertNoMarker(lease, "gadm-division-called.marker");
                Assert.IsNull(result.FinalLocation.State);
            }

            Assert.AreEqual(
                airport ? "Fixture Airport" : "Fixture City",
                result.FinalLocation.City!.Value);
            if (gadm)
            {
                Assert.AreEqual(
                    CoordinateLookupGadmAttribution.LicenseUrl,
                    result.GadmDivisions.LicenseUrl);
            }
        }
        else if (scenario == "real-coordinate-degraded")
        {
            Assert.IsNotNull(result.OvertureDivisions.Error);
            AssertNoMarker(lease, "overture-division-called.marker");
        }

        if (!airport && !places && !gadm)
        {
            Assert.AreEqual(CoordinateLookupSourceState.Disabled, result.GadmDivisions.State);
            Assert.AreEqual(CoordinateLookupSourceState.Disabled, result.AirportInfrastructure.State);
            Assert.AreEqual(CoordinateLookupSourceState.Disabled, result.LiveOverturePlaces.State);
            AssertNoMarker(lease, "gadm-cache-called.marker");
            AssertNoMarker(lease, "airport-called.marker");
            AssertNoMarker(lease, "places-called.marker");
        }
    }

    [TestMethod]
    public async Task ProductionHost_DomainFailureUsesActualClassifierAndOneFailedTerminal()
    {
        await using var lease = new WorkerProcessFixtureLease();
        var sink = new RecordingJobSink();
        CoordinateLookupWorkerJobDispatch dispatch = Dispatch(lease, true, true, true);

        ChildWorkerSession session = await lease.LaunchAsync(
            "real-coordinate-domain-failure",
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
        AssertMarker(lease, "source-called.marker");
        AssertMarker(lease, "domain-fault-injected.marker");
        AssertNoMarker(lease, "persistence-accessed.marker");
        AssertNoOptionalSourceMarkers(lease);
        Assert.AreEqual(1, sink.Events.Count(IsTerminal));
        var terminal = Assert.IsInstanceOfType<WorkerJobTerminalPayload>(
            completion.JobTerminal!.Payload);
        Assert.AreEqual(WorkerJobTerminalOutcome.Failed, terminal.Outcome);
        Assert.AreEqual("coordinate-lookup-failed", terminal.Error!.Code);
        Assert.AreEqual(WorkerJobFailureCategory.Domain, terminal.Error.Category);
        Assert.IsNull(terminal.CoordinateLookupResult);
    }

    [TestMethod]
    public async Task ProductionHost_ActiveCancellationObservesOwnedCacheAndClosesActivity()
    {
        await using var lease = new WorkerProcessFixtureLease();
        var sink = new RecordingJobSink();
        CoordinateLookupWorkerJobDispatch dispatch = Dispatch(lease, false, false, false);
        ChildWorkerSession session = await lease.LaunchAsync(
            "real-coordinate-cancellation",
            dispatch,
            sink,
            InternalWorkerProtocolVersion.V2,
            capture: false);
        await sink.WaitForAsync(message => message.Payload is WorkerJobActivityStartedPayload);
        AssertMarker(lease, "owned-cache-started.marker");

        ChildWorkerCancellationResult stopped = await session.RequestStop()
            .WaitAsync(WorkerProcessFixtureLease.Watchdog);

        Assert.AreEqual(
            WorkerProcessExitCodes.Cancelled,
            stopped.Completion.ExitCode,
            stopped.Completion.StandardErrorTail.Text);
        Assert.IsTrue(stopped.Facts.RequestAccepted);
        Assert.IsFalse(stopped.Facts.KillAttempted);
        AssertMarker(lease, "owned-cache-cancelled.marker");
        AssertNoMarker(lease, "persistence-accessed.marker");
        AssertNoMarker(lease, "gadm-cache-called.marker");
        AssertNoMarker(lease, "airport-called.marker");
        AssertNoMarker(lease, "places-called.marker");
        Assert.AreEqual(1, sink.Events.Count(IsTerminal));
        AssertBalancedActivities(sink.Events);
        var terminal = Assert.IsInstanceOfType<WorkerJobTerminalPayload>(
            stopped.Completion.JobTerminal!.Payload);
        Assert.AreEqual(WorkerJobTerminalOutcome.Cancelled, terminal.Outcome);
        Assert.IsNull(terminal.CoordinateLookupResult);
    }

    [TestMethod]
    public async Task ProductionHost_ManagedOutputFailureCancelsPendingInputAndSettlesExitSix()
    {
        await using var lease = new WorkerProcessFixtureLease();
        var sink = new RecordingJobSink();
        ChildWorkerSession session = await lease.LaunchAsync(
            "real-coordinate-output-failure",
            Dispatch(lease, true, true, true),
            sink,
            InternalWorkerProtocolVersion.V2,
            capture: false);
        var fallbackCleanup = false;

        try
        {
            Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyAccepted>(await session.Startup);
            await session.ExecuteRequestAccepted.WaitAsync(WorkerProcessFixtureLease.Watchdog);
            await WaitForMarkerAsync(lease.Root, "input-post-execute-read-pending.marker");
            await WaitForMarkerAsync(lease.Root, "output-fault-injected.marker");
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
                "coordinate-job-started",
                File.ReadAllText(Path.Combine(lease.Root, "output-fault-injected.marker")));
            AssertMarker(lease, "input-post-execute-read-cancelled.marker");
            AssertMarker(lease, "input-post-execute-read-finished.marker");
            Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(
                completion.StandardOutputFinality);
            Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(
                completion.StandardErrorFinality);
            Assert.AreSame(completion, await session.EvidenceFinality);
            Assert.AreSame(completion, await session.Settlement);
            Assert.AreEqual(1, sink.Events.Length, "Only the ready frame can be emitted before the managed write fault.");
            Assert.IsInstanceOfType<WorkerJobReadyPayload>(sink.Events[0].Payload);
            AssertNoMarker(lease, "persistence-accessed.marker");
            AssertNoMarker(lease, "source-called.marker");
            AssertNoMarker(lease, "overture-cache-called.marker");
            AssertNoMarker(lease, "overture-division-called.marker");
            AssertNoMarker(lease, "gadm-cache-called.marker");
            AssertNoMarker(lease, "gadm-division-called.marker");
            AssertNoMarker(lease, "airport-called.marker");
            AssertNoMarker(lease, "places-called.marker");
        }
        finally
        {
            if (!session.EvidenceFinality.IsCompleted)
            {
                fallbackCleanup = true;
                await session.RequestStop().WaitAsync(WorkerProcessFixtureLease.Watchdog);
            }
        }

        Assert.IsFalse(fallbackCleanup, "The green path must not require parent-side input closure or termination.");
    }

    [TestMethod]
    public async Task ProductionHost_ReadinessFailureUsesStartupClassifierBeforeDispatch()
    {
        await using var lease = new WorkerProcessFixtureLease();
        var sink = new RecordingJobSink();
        ChildWorkerSession session = await lease.LaunchAsync(
            "real-coordinate-startup-failure",
            Dispatch(lease, true, true, true),
            sink,
            InternalWorkerProtocolVersion.V2,
            capture: false);
        ChildWorkerCompletionObservation completion = await lease.CompleteAsync();

        Assert.IsTrue(await session.Startup is
            ChildWorkerStartupObservation.PreReadyExit or
            ChildWorkerStartupObservation.PreReadyEndOfStream);
        Assert.AreEqual(
            WorkerProcessExitCodes.InfrastructureFailure,
            completion.ExitCode,
            completion.StandardErrorTail.Text);
        AssertMarker(lease, "startup-fault-injected.marker");
        AssertNoMarker(lease, "persistence-accessed.marker");
        AssertNoMarker(lease, "source-called.marker");
        Assert.IsNull(completion.JobTerminal);
        Assert.AreEqual(0, sink.Events.Length);
        Assert.AreEqual(0, lease.WrittenInput.Length);
    }

    [TestMethod]
    public async Task ProductionHost_InvalidCoordinateExits2BeforeAcceptanceOrSourceWork()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "immich-reversegeo-worker-fixture",
            Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(root);
        using var process = StartDirectProcess(root, "real-coordinate-success");
        try
        {
            Assert.IsTrue(process.Start());
            string? ready = await process.StandardOutput.ReadLineAsync()
                .WaitAsync(WorkerProcessFixtureLease.Watchdog);
            Assert.IsNotNull(ready);
            WorkerJobProtocolParseResult parsedReady = WorkerJobProtocolCodec.Parse(
                Encoding.UTF8.GetBytes(ready));
            Assert.IsTrue(parsedReady.IsSuccess, parsedReady.Failure?.Diagnostic);
            Assert.IsInstanceOfType<WorkerJobReadyPayload>(parsedReady.Message!.Payload);

            await process.StandardInput.WriteAsync(InvalidCoordinateFrame);
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(WorkerProcessFixtureLease.Watchdog);
            string remainingOutput = await process.StandardOutput.ReadToEndAsync();

            Assert.AreEqual(WorkerProcessExitCodes.InvalidInput, process.ExitCode);
            Assert.AreEqual(string.Empty, remainingOutput, "Invalid input must not be accepted.");
            AssertNoMarker(root, "source-called.marker");
            AssertNoMarker(root, "persistence-accessed.marker");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }

            Directory.Delete(root, recursive: true);
        }
    }

    private const string InvalidCoordinateFrame =
        "{\"protocol\":\"immich-reversegeo.worker\",\"version\":2,\"direction\":\"controller-to-worker\",\"category\":\"request\",\"type\":\"execute\",\"sequence\":1,\"timestampUtc\":\"2026-09-08T13:31:00.0000000Z\",\"jobId\":\"11111111-2222-3333-4444-555555555555\",\"jobKind\":\"CoordinateLookup\",\"payload\":{\"request\":{\"latitude\":91,\"longitude\":0,\"includeAirportInfrastructure\":false,\"includeLiveOverturePlaces\":false,\"preferGadmAdministrativeAreas\":false,\"cityResolverOverrides\":{\"countryProfiles\":[]}}}}\n";

    private static CoordinateLookupWorkerJobDispatch Dispatch(
        WorkerProcessFixtureLease lease,
        bool airport,
        bool places,
        bool gadm) =>
        new(
            lease.Request.RunId,
            new CoordinateLookupRequest(
                47.6062,
                -122.3321,
                airport,
                places,
                gadm,
                new CoordinateLookupCityResolverOverrides(null, [])));

    private static Process StartDirectProcess(string root, string scenario)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = WorkerProcessFixtureLease.FixtureExecutable,
                WorkingDirectory = WorkerProcessFixtureLease.FixtureDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("--scenario");
        process.StartInfo.ArgumentList.Add(scenario);
        process.StartInfo.ArgumentList.Add("--resource-root");
        process.StartInfo.ArgumentList.Add(root);
        process.StartInfo.Environment[
            InternalWorkerProtocolVersionSelector.EnvironmentVariableName] = "2";
        return process;
    }

    private static bool IsTerminal(WorkerJobOutputMessage message) =>
        message.Payload is WorkerJobTerminalPayload;

    private static bool IsActivity(WorkerJobOutputMessage message) =>
        message.Payload is WorkerJobActivityStartedPayload or WorkerJobActivityEndedPayload;

    private static void AssertTypedSequence(
        IReadOnlyList<WorkerJobOutputMessage> events,
        Guid jobId)
    {
        CollectionAssert.AreEqual(
            Enumerable.Range(1, events.Count).Select(static value => (long)value).ToArray(),
            events.Select(static value => value.Sequence).ToArray());
        Assert.IsInstanceOfType<WorkerJobReadyPayload>(events[0].Payload);
        Assert.IsTrue(events.Skip(1).All(value => value.JobId == jobId));
        Assert.IsTrue(events.Skip(1).All(value => value.JobKind == WorkerJobKind.CoordinateLookup));
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
        CollectionAssert.AreEqual(started, ended);
    }

    private static void AssertMarker(WorkerProcessFixtureLease lease, string name) =>
        Assert.IsTrue(File.Exists(Path.Combine(lease.Root, name)), $"Expected marker {name}.");

    private static void AssertNoMarker(WorkerProcessFixtureLease lease, string name) =>
        AssertNoMarker(lease.Root, name);

    private static void AssertNoMarker(string root, string name) =>
        Assert.IsFalse(File.Exists(Path.Combine(root, name)), $"Unexpected marker {name}.");

    private static void AssertNoOptionalSourceMarkers(WorkerProcessFixtureLease lease)
    {
        AssertNoMarker(lease, "overture-cache-called.marker");
        AssertNoMarker(lease, "overture-division-called.marker");
        AssertNoMarker(lease, "gadm-cache-called.marker");
        AssertNoMarker(lease, "gadm-division-called.marker");
        AssertNoMarker(lease, "airport-called.marker");
        AssertNoMarker(lease, "places-called.marker");
    }

    private static async Task WaitForMarkerAsync(string root, string name)
    {
        string path = Path.Combine(root, name);
        if (File.Exists(path))
        {
            return;
        }

        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(root, name)
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

        await reached.Task.WaitAsync(WorkerProcessFixtureLease.Watchdog);
    }

    private sealed class RecordingJobSink : IWorkerJobEventSink
    {
        private readonly ConcurrentQueue<WorkerJobOutputMessage> _events = new();
        private readonly SemaphoreSlim _changed = new(0);

        internal WorkerJobOutputMessage[] Events => _events.ToArray();

        public ValueTask AcceptAsync(
            WorkerJobOutputMessage message,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _events.Enqueue(message);
            _changed.Release();
            return ValueTask.CompletedTask;
        }

        internal async Task WaitForAsync(Func<WorkerJobOutputMessage, bool> predicate)
        {
            using var timeout = new CancellationTokenSource(WorkerProcessFixtureLease.Watchdog);
            while (!_events.Any(predicate))
            {
                await _changed.WaitAsync(timeout.Token);
            }
        }
    }
}
