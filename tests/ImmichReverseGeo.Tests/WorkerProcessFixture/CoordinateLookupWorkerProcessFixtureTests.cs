using System.Collections.Concurrent;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using ImmichReverseGeo.Web.ChildWorkerLaunching;

namespace ImmichReverseGeo.Tests.WorkerProcessFixture;

[TestClass]
[TestCategory("Change48")]
public sealed class CoordinateLookupWorkerProcessFixtureTests
{
    [TestMethod]
    [DataRow("success", true, true, true, CoordinateLookupCountryStatus.Matched, CoordinateLookupSourceState.Ready)]
    [DataRow("success", false, false, false, CoordinateLookupCountryStatus.Matched, CoordinateLookupSourceState.Ready)]
    [DataRow("no-work", true, true, true, CoordinateLookupCountryStatus.NoMatch, CoordinateLookupSourceState.Skipped)]
    [DataRow("source-degraded", true, true, true, CoordinateLookupCountryStatus.Matched, CoordinateLookupSourceState.Unavailable)]
    public async Task ActualProcess_CoordinateLookupCompletedRowsPreserveTypedResultAndFinality(
        string scenario,
        bool airport,
        bool places,
        bool gadm,
        CoordinateLookupCountryStatus expectedCountry,
        CoordinateLookupSourceState expectedOverture)
    {
        await using var lease = new WorkerProcessFixtureLease();
        var sink = new RecordingJobSink();
        var dispatch = Dispatch(lease, airport, places, gadm);

        var session = await lease.LaunchAsync(
            scenario,
            dispatch,
            sink,
            InternalWorkerProtocolVersion.V2);
        var completion = await lease.CompleteAsync();

        Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyAccepted>(await session.Startup);
        Assert.AreEqual(WorkerProcessExitCodes.Completed, completion.ExitCode, completion.StandardErrorTail.Text);
        Assert.IsNull(completion.FirstProtocolObservation);
        WorkerJobOutputMessage[] events = sink.Events;
        CollectionAssert.AreEqual(
            Enumerable.Range(1, events.Length).Select(static value => (long)value).ToArray(),
            events.Select(static value => value.Sequence).ToArray());
        Assert.IsTrue(events.Skip(1).All(value => value.JobId == dispatch.Context.JobId));
        Assert.IsTrue(events.Skip(1).All(value => value.JobKind == WorkerJobKind.CoordinateLookup));
        Assert.AreEqual(1, events.Count(value => value.Type == WorkerJobProtocolV2.TerminalType));
        AssertBalancedActivities(events);

        var terminal = Assert.IsInstanceOfType<WorkerJobTerminalPayload>(completion.JobTerminal!.Payload);
        Assert.AreEqual(WorkerJobTerminalOutcome.Completed, terminal.Outcome);
        Assert.IsNull(terminal.ProcessAssetsResult);
        Assert.AreEqual(expectedCountry, terminal.CoordinateLookupResult!.Country.Status);
        Assert.AreEqual(expectedOverture, terminal.CoordinateLookupResult.OvertureDivisions.State);
        Assert.AreEqual(CoordinateLookupGadmAttribution.LicenseUrl,
            terminal.CoordinateLookupResult.GadmDivisions.LicenseUrl);
        if (scenario == "source-degraded")
        {
            Assert.AreEqual("United States", terminal.CoordinateLookupResult.FinalLocation.Country!.Value);
            Assert.IsNull(terminal.CoordinateLookupResult.FinalLocation.State);
            Assert.IsNotNull(terminal.CoordinateLookupResult.OvertureDivisions.Error);
        }

        lease.AssertExactCapture();
    }

    [TestMethod]
    public async Task ActualProcess_CoordinateLookupCooperativeCancellationClosesActivityAndExits130()
    {
        await using var lease = new WorkerProcessFixtureLease();
        var sink = new RecordingJobSink();
        var dispatch = Dispatch(lease, airport: true, places: true, gadm: true);
        var session = await lease.LaunchAsync(
            "cooperative-cancel",
            dispatch,
            sink,
            InternalWorkerProtocolVersion.V2);
        await sink.WaitForAsync(message =>
            message.Payload is WorkerJobLogPayload log
            && log.Message.Contains("cooperative-cancel", StringComparison.Ordinal));

        ChildWorkerCancellationResult stopped = await session.RequestStop()
            .WaitAsync(WorkerProcessFixtureLease.Watchdog);

        Assert.AreEqual(WorkerProcessExitCodes.Cancelled, stopped.Completion.ExitCode);
        WorkerJobOutputMessage[] events = sink.Events;
        Assert.AreEqual(1, events.Count(value => value.Type == WorkerJobProtocolV2.TerminalType));
        AssertBalancedActivities(events);
        var terminal = Assert.IsInstanceOfType<WorkerJobTerminalPayload>(stopped.Completion.JobTerminal!.Payload);
        Assert.AreEqual(WorkerJobTerminalOutcome.Cancelled, terminal.Outcome);
        Assert.IsNull(terminal.CoordinateLookupResult);
        Assert.IsTrue(stopped.Facts.RequestAccepted);
        Assert.IsFalse(stopped.Facts.KillAttempted);
    }

    [TestMethod]
    public async Task ActualProcess_CoordinateLookupDomainFailureHasOneFailedTerminalAndExit4()
    {
        await using var lease = new WorkerProcessFixtureLease();
        var sink = new RecordingJobSink();
        var dispatch = Dispatch(lease, airport: true, places: true, gadm: true);

        await lease.LaunchAsync(
            "domain-failure",
            dispatch,
            sink,
            InternalWorkerProtocolVersion.V2);
        var completion = await lease.CompleteAsync();

        Assert.AreEqual(WorkerProcessExitCodes.ExecutorFailure, completion.ExitCode);
        Assert.IsNull(completion.FirstProtocolObservation);
        Assert.AreEqual(1, sink.Events.Count(value => value.Type == WorkerJobProtocolV2.TerminalType));
        var terminal = Assert.IsInstanceOfType<WorkerJobTerminalPayload>(completion.JobTerminal!.Payload);
        Assert.AreEqual(WorkerJobTerminalOutcome.Failed, terminal.Outcome);
        Assert.AreEqual("fixture-domain-failure", terminal.Error!.Code);
        Assert.IsNull(terminal.CoordinateLookupResult);
    }

    [TestMethod]
    public async Task ActualProcess_ManagedFailureMatrixUses2_4_5_6_130AndNever3()
    {
        var exits = new List<int>();

        await using (var domain = new WorkerProcessFixtureLease())
        {
            await domain.LaunchAsync(
                "domain-failure",
                Dispatch(domain, true, true, true),
                new RecordingJobSink(),
                InternalWorkerProtocolVersion.V2);
            exits.Add((await domain.CompleteAsync()).ExitCode!.Value);
        }

        await using (var startup = new WorkerProcessFixtureLease())
        {
            await startup.LaunchAsync(
                "pre-ready-crash",
                Dispatch(startup, true, true, true),
                new RecordingJobSink(),
                InternalWorkerProtocolVersion.V2,
                false,
                "--exit-code",
                WorkerProcessExitCodes.InfrastructureFailure.ToString());
            exits.Add((await startup.CompleteAsync()).ExitCode!.Value);
        }

        await using (var output = new WorkerProcessFixtureLease())
        {
            await output.LaunchAsync(
                "post-ready-crash",
                Dispatch(output, true, true, true),
                new RecordingJobSink(),
                InternalWorkerProtocolVersion.V2,
                true,
                "--exit-code",
                WorkerProcessExitCodes.OutputTransportFailure.ToString());
            ChildWorkerCompletionObservation completion = await output.CompleteAsync();
            exits.Add(completion.ExitCode!.Value);
            Assert.IsNull(completion.JobTerminal, "output-failure-stream-has-no-synthetic-terminal");
        }

        await using (var cancelled = new WorkerProcessFixtureLease())
        {
            var sink = new RecordingJobSink();
            ChildWorkerSession session = await cancelled.LaunchAsync(
                "cooperative-cancel",
                Dispatch(cancelled, true, true, true),
                sink,
                InternalWorkerProtocolVersion.V2);
            await sink.WaitForAsync(message => message.Payload is WorkerJobActivityStartedPayload);
            exits.Add((await session.RequestStop().WaitAsync(WorkerProcessFixtureLease.Watchdog))
                .Completion.ExitCode!.Value);
        }

        exits.Add(await RunInvalidCoordinateInputAsync());

        CollectionAssert.AreEquivalent(
            new[]
            {
                WorkerProcessExitCodes.InvalidInput,
                WorkerProcessExitCodes.ExecutorFailure,
                WorkerProcessExitCodes.InfrastructureFailure,
                WorkerProcessExitCodes.OutputTransportFailure,
                WorkerProcessExitCodes.Cancelled
            },
            exits);
        Assert.IsFalse(exits.Contains(WorkerProcessExitCodes.Busy));
    }

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

    private static async Task<int> RunInvalidCoordinateInputAsync()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "immich-reversegeo-worker-fixture",
            Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(root);
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
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
        process.StartInfo.ArgumentList.Add("success");
        process.StartInfo.ArgumentList.Add("--resource-root");
        process.StartInfo.ArgumentList.Add(root);
        process.StartInfo.Environment[InternalWorkerProtocolVersionSelector.EnvironmentVariableName] = "2";

        try
        {
            Assert.IsTrue(process.Start());
            string? ready = await process.StandardOutput.ReadLineAsync()
                .WaitAsync(WorkerProcessFixtureLease.Watchdog);
            Assert.IsNotNull(ready);
            await process.StandardInput.WriteAsync(
                "{\"protocol\":\"immich-reversegeo.worker\",\"version\":2,\"direction\":\"controller-to-worker\",\"category\":\"request\",\"type\":\"execute\",\"sequence\":1,\"timestampUtc\":\"2026-09-08T13:31:00.0000000Z\",\"jobId\":\"11111111-2222-3333-4444-555555555555\",\"jobKind\":\"CoordinateLookup\",\"payload\":{\"request\":{\"latitude\":91,\"longitude\":0,\"includeAirportInfrastructure\":false,\"includeLiveOverturePlaces\":false,\"preferGadmAdministrativeAreas\":false,\"cityResolverOverrides\":{\"countryProfiles\":[]}}}}\n");
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(WorkerProcessFixtureLease.Watchdog);
            string remaining = await process.StandardOutput.ReadToEndAsync();
            string error = await process.StandardError.ReadToEndAsync();
            Assert.AreEqual(string.Empty, remaining, "invalid-coordinate-no-accepted-output");
            StringAssert.StartsWith(error, "fixture-input:");
            return process.ExitCode;
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
