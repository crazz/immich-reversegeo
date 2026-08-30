using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class ProcessingBackgroundServiceRoutingCoverageTests
{
    [TestMethod]
    public async Task CountCancellationAndFailure_PreservePendingSnapshotWithoutEligibilityProjection()
    {
        foreach (var failure in new Exception[] { new OperationCanceledException(), new InvalidOperationException("count failed") })
        {
            var state = PriorState();
            var priorStart = state.LastRunStarted;
            var priorTotal = state.TotalUnprocessed;
            var reporter = new ProcessingStateEventReporter(state);
            var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual);
            state.MarkPending();
            Assert.IsTrue(reporter.Arm(request));
            using var cancellation = new CancellationTokenSource();
            if (failure is OperationCanceledException)
            {
                cancellation.Cancel();
            }

            var operations = Operations(_ => Task.FromException<long>(failure));
            await ProcessingBackgroundService.RunOnceAsync(NullLogger<ProcessingBackgroundService>.Instance, state, reporter, request, operations, cancellation.Token);

            Assert.IsFalse(state.IsRunning);
            Assert.AreEqual(priorStart, state.LastRunStarted);
            Assert.AreEqual(priorTotal, state.TotalUnprocessed);
            Assert.IsTrue(state.GetRecentLog().Any(line => line.EndsWith(failure is OperationCanceledException ? "Run cancelled." : "[ERROR] Fatal: count failed", StringComparison.Ordinal)));
        }
    }

    [TestMethod]
    public async Task RoutedDispositions_ExcludeSuppressedAndRetainCommittedWriteAfterCancellation()
    {
        var state = new ProcessingState();
        var reporter = new ProcessingStateEventReporter(state);
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Scheduled);
        Assert.IsTrue(reporter.Arm(request));
        using var cancellation = new CancellationTokenSource();
        var suppressed = Guid.NewGuid();
        var written = Guid.NewGuid();
        var noCountry = Guid.NewGuid();
        var failing = Guid.NewGuid();
        var batches = new Queue<List<AssetRecord>>([
            [new(suppressed, 1, 1, DateTime.UtcNow), new(written, 2, 2, DateTime.UtcNow), new(noCountry, 3, 3, DateTime.UtcNow), new(failing, 4, 4, DateTime.UtcNow)],
            []]);
        var writes = 0;
        var operations = new ProcessingBackgroundService.ProcessingOperations(
            _ => Task.FromResult(4L),
            () => Task.FromResult(new AppConfig { Processing = new ProcessingConfig { BatchDelayMs = 0, MaxDegreeOfParallelism = 1, VerboseLogging = true, UseAirportInfrastructure = false } }),
            () => Task.FromResult(new HashSet<Guid> { suppressed }),
            (_, _, _) => Task.FromResult(batches.Dequeue()),
            (lat, _, _, _, _) => lat switch
            {
                2 => Task.FromResult<AdministrativeAreaResolution?>(new("USA", "US", "United States", new GeoResult("United States", "State", "City"), null, null)),
                3 => Task.FromResult<AdministrativeAreaResolution?>(null),
                _ => throw new InvalidOperationException("resolver failed")
            },
            (_, _, _, _) => throw new AssertFailedException("airport lookup was not expected"),
            _ => Task.CompletedTask,
            (id, _, _) =>
            {
                writes++;
                if (id == written)
                {
                    return Task.CompletedTask;
                }

                throw new InvalidOperationException("write failed");
            });

        await ProcessingBackgroundService.RunOnceAsync(NullLogger<ProcessingBackgroundService>.Instance, state, reporter, request, operations, cancellation.Token);

        Assert.AreEqual(1, writes);
        Assert.AreEqual(1L, state.ProcessedThisRun);
        Assert.AreEqual(1L, state.SkippedThisRun);
        Assert.AreEqual(1L, state.ErrorsThisRun);
        Assert.AreEqual($"Asset {failing} [FindCountry]: resolver failed", state.LastError);
        AssertLogSuffixes(state, "Skipping 1 previously unresolvable assets.", "Asset " + written + ": City, State, United States", "[WARN] Asset " + noCountry, "[ERROR] Asset " + failing + " [FindCountry]: resolver failed", "Run complete. Processed=1 Skipped=1 Errors=1");

        // The second run gates cancellation after an irreversible successful write, with no sleep.
        state = new ProcessingState();
        reporter = new ProcessingStateEventReporter(state);
        request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual);
        Assert.IsTrue(reporter.Arm(request));
        var firstBatch = true;
        var writeAccepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        operations = new ProcessingBackgroundService.ProcessingOperations(
            _ => Task.FromResult(1L),
            () => Task.FromResult(new AppConfig { Processing = new ProcessingConfig { BatchDelayMs = 0, MaxDegreeOfParallelism = 1, UseAirportInfrastructure = false } }),
            () => Task.FromResult(new HashSet<Guid>()),
            (_, _, token) =>
            {
                if (firstBatch)
                {
                    firstBatch = false;
                    return Task.FromResult(new List<AssetRecord> { new(written, 2, 2, DateTime.UtcNow) });
                }

                token.ThrowIfCancellationRequested();
                return Task.FromResult(new List<AssetRecord>());
            },
            (_, _, _, _, _) => Task.FromResult<AdministrativeAreaResolution?>(new("USA", "US", "United States", new GeoResult("United States", "State", "City"), null, null)),
            (_, _, _, _) => throw new AssertFailedException("airport lookup was not expected"),
            _ => Task.CompletedTask,
            async (_, _, _) => { writeAccepted.TrySetResult(); await releaseCancellation.Task; });
        var pass = ProcessingBackgroundService.RunOnceAsync(NullLogger<ProcessingBackgroundService>.Instance, state, reporter, request, operations, cancellation.Token);
        await writeAccepted.Task;
        releaseCancellation.SetResult();
        cancellation.Cancel();
        await pass;

        Assert.AreEqual(1L, state.ProcessedThisRun);
        AssertLogSuffixes(state, "Run cancelled.", "Run complete. Processed=1 Skipped=0 Errors=0");
    }


    [TestMethod]
    public async Task NoCityLoggerOnlyDecision_EmitsExactlyOneSkippedDispositionWithoutProcessingLog()
    {
        var noCity = new GeoResult("United States", null, null);
        Assert.IsTrue(ProcessingBackgroundService.IsLoggerOnlyNoCitySkip(noCity));
        Assert.IsFalse(ProcessingBackgroundService.IsLoggerOnlyNoCitySkip(noCity.WithFallbackCity()));

        var reporter = new RecordingProcessingEventReporter();
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual);
        var session = await reporter.OpenRunAsync(request, DateTimeOffset.UtcNow);
        await session.DetermineEligibilityAsync(1);
        if (ProcessingBackgroundService.IsLoggerOnlyNoCitySkip(noCity))
        {
            await session.ReportSkippedAsync();
        }

        Assert.AreEqual(1, reporter.Events.OfType<ProgressChanged>().Count());
        var progress = reporter.Events.OfType<ProgressChanged>().Single().Progress;
        Assert.AreEqual(1L, progress.SkippedCount);
        Assert.AreEqual(0L, progress.UpdatedCount);
        Assert.AreEqual(0L, progress.FailedCount);
        Assert.AreEqual(0, reporter.Events.OfType<LogEmitted>().Count());
    }

    [TestMethod]
    public async Task ReportingFault_PropagatesWithoutRecursiveDispositionLogOrTerminal()
    {
        var reporter = new RecordingProcessingEventReporter
        {
            FailureFactory = processingEvent => processingEvent is EligibilityDetermined
                ? new InvalidOperationException("injected reporting fault")
                : null
        };
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ProcessingBackgroundService.RunOnceAsync(
            NullLogger<ProcessingBackgroundService>.Instance,
            new ProcessingState(),
            reporter,
            request,
            Operations(_ => Task.FromResult(1L)),
            CancellationToken.None));

        CollectionAssert.AreEqual(new[] { typeof(RunStarted), typeof(EligibilityDetermined) }, reporter.Attempts.Select(x => x.GetType()).ToArray());
        Assert.AreEqual(0, reporter.Attempts.Count(x => x is LogEmitted or ProgressChanged or RunFinished));
    }

    [TestMethod]
    [DataRow("start")]
    [DataRow("eligibility")]
    [DataRow("log")]
    [DataRow("progress-updated")]
    [DataRow("progress-skipped")]
    [DataRow("progress-failed")]
    [DataRow("terminal")]
    public async Task RealAdapterProjectionFault_RecoversWithoutRecursiveReporting(string faultStage)
    {
        var state = PriorState();
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual);
        var faulted = false;
        var reporter = new ProcessingStateEventReporter(state, processingEvent =>
        {
            var matches = faultStage switch
            {
                "start" => processingEvent is RunStarted,
                "eligibility" => processingEvent is EligibilityDetermined,
                "log" => processingEvent is LogEmitted,
                "terminal" => processingEvent is RunFinished,
                _ => processingEvent is ProgressChanged
            };
            if (matches && !faulted)
            {
                faulted = true;
                throw new InvalidOperationException($"{faultStage} projection failed");
            }
        });
        state.MarkPending();
        Assert.IsTrue(reporter.Arm(request));
        var asset = new AssetRecord(Guid.NewGuid(), 1, 2, DateTime.UtcNow);
        var batches = new Queue<List<AssetRecord>>([[asset], []]);
        var sideEffects = 0;
        var requiresAsset = faultStage is "log" or "progress-updated" or "progress-skipped" or "progress-failed";
        var operations = new ProcessingBackgroundService.ProcessingOperations(
            _ => Task.FromResult(requiresAsset ? 1L : 0L),
            () => Task.FromResult(new AppConfig { Processing = new ProcessingConfig { BatchDelayMs = 0, MaxDegreeOfParallelism = 1, UseAirportInfrastructure = false } }),
            () => Task.FromResult(faultStage == "log" ? new HashSet<Guid> { asset.Id } : new HashSet<Guid>()),
            (_, _, _) => Task.FromResult(batches.Dequeue()),
            (_, _, _, _, _) => faultStage switch
            {
                "progress-skipped" => Task.FromResult<AdministrativeAreaResolution?>(null),
                "progress-failed" => throw new InvalidOperationException("handled asset failure"),
                _ => Task.FromResult<AdministrativeAreaResolution?>(new("USA", "US", "United States", new GeoResult("United States", "State", "City"), null, null))
            },
            (_, _, _, _) => throw new AssertFailedException("airport lookup was not expected"),
            _ => { sideEffects++; return Task.CompletedTask; },
            (_, _, _) => { sideEffects++; return Task.CompletedTask; });

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ProcessingBackgroundService.RunOnceAsync(
            NullLogger<ProcessingBackgroundService>.Instance, state, reporter, request, operations, CancellationToken.None));

        var expectedProcessed = faultStage is "start" or "eligibility" ? 1L : faultStage == "progress-updated" ? 1L : 0L;
        var expectedSkipped = faultStage == "progress-skipped" ? 1L : 0L;
        var expectedErrors = faultStage == "progress-failed" ? 2L : 1L;
        if (faultStage.StartsWith("progress-", StringComparison.Ordinal))
        {
            Assert.AreEqual(faultStage == "progress-failed" ? 0 : 1, sideEffects);
        }

        Assert.IsTrue(faulted);
        Assert.IsFalse(state.IsRunning);
        Assert.IsNull(state.CurrentActivity);
        Assert.AreEqual(expectedProcessed, state.ProcessedThisRun);
        Assert.AreEqual(expectedSkipped, state.SkippedThisRun);
        Assert.AreEqual(expectedErrors, state.ErrorsThisRun);
        Assert.AreEqual($"Fatal: {faultStage} projection failed", state.LastError);
        Assert.AreEqual(1, state.GetRecentLog().Count(line => line.EndsWith($"[ERROR] Fatal: {faultStage} projection failed", StringComparison.Ordinal)));
        Assert.IsTrue(state.GetRecentLog().Last().EndsWith($"Run complete. Processed={expectedProcessed} Skipped={expectedSkipped} Errors={expectedErrors}", StringComparison.Ordinal));
        Assert.IsTrue(reporter.Arm(new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Scheduled)));
    }

    [TestMethod]
    public async Task SynchronousStateNotificationFault_RecoversAndAllowsLaterScheduledAndManualAdmission()
    {
        var state = new ProcessingState();
        var throwNotification = false;
        state.OnChanged += () =>
        {
            if (throwNotification)
            {
                throwNotification = false;
                throw new InvalidOperationException("notification failed");
            }
        };
        var reporter = new ProcessingStateEventReporter(state);
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual);
        state.MarkPending();
        Assert.IsTrue(reporter.Arm(request));
        throwNotification = true;

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ProcessingBackgroundService.RunOnceAsync(
            NullLogger<ProcessingBackgroundService>.Instance, state, reporter, request, Operations(_ => Task.FromResult(0L)), CancellationToken.None));

        Assert.IsFalse(state.IsRunning);
        Assert.AreEqual("Fatal: notification failed", state.LastError);
        var service = new ProcessingBackgroundService(NullLogger<ProcessingBackgroundService>.Instance, state, reporter, Operations(_ => Task.FromResult(0L)));
        await service.TryRunScheduledAsync(CancellationToken.None);
        Assert.IsFalse(state.IsRunning);
        await service.TriggerRunAsync();
        await service.WaitForManualAdmissionAsync();
        Assert.IsFalse(state.IsRunning);
    }

    [TestMethod]
    [DataRow("manual")]
    [DataRow("scheduled")]
    public async Task PendingNotificationFault_RollsBackAndReleasesAdmissionLock(string trigger)
    {
        var state = new ProcessingState();
        var throwNotification = true;
        state.OnChanged += () =>
        {
            if (throwNotification)
            {
                throwNotification = false;
                throw new InvalidOperationException("pending notification failed");
            }
        };
        var reporter = new ProcessingStateEventReporter(state);
        var service = new ProcessingBackgroundService(NullLogger<ProcessingBackgroundService>.Instance, state, reporter, Operations(_ => Task.FromResult(0L)));

        if (trigger == "manual")
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.TriggerRunAsync());
            Assert.IsFalse(state.IsRunning);
            await service.TriggerRunAsync();
            await service.WaitForManualAdmissionAsync();
        }
        else
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.TryRunScheduledAsync(CancellationToken.None));
            Assert.IsFalse(state.IsRunning);
            await service.TryRunScheduledAsync(CancellationToken.None);
        }

        Assert.IsFalse(state.IsRunning);
    }

    [TestMethod]
    public async Task ScheduledArmFailure_RollsBackPendingAndReleasesAdmissionLock()
    {
        var state = new ProcessingState();
        var reporter = new ProcessingStateEventReporter(state);
        Assert.IsTrue(reporter.Arm(new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual)));
        var service = new ProcessingBackgroundService(NullLogger<ProcessingBackgroundService>.Instance, state, reporter, Operations(_ => Task.FromResult(0L)));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.TryRunScheduledAsync(CancellationToken.None));
        Assert.IsFalse(state.IsRunning);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.TryRunScheduledAsync(CancellationToken.None));
        Assert.IsFalse(state.IsRunning);
    }

    [TestMethod]
    public async Task ManualArmFailure_RollsBackPendingAndReleasesAdmissionLock()
    {
        var state = new ProcessingState();
        var reporter = new ProcessingStateEventReporter(state);
        Assert.IsTrue(reporter.Arm(new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Scheduled)));
        var service = new ProcessingBackgroundService(NullLogger<ProcessingBackgroundService>.Instance, state, reporter, Operations(_ => Task.FromResult(0L)));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.TriggerRunAsync());
        Assert.IsFalse(state.IsRunning);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.TriggerRunAsync());
        Assert.IsFalse(state.IsRunning);
    }

    [TestMethod]
    public async Task ResolverProgress_UsesDirectStateBridgeExactlyOnceWithoutReporterDuplicate()
    {
        var state = new ProcessingState();
        var reporter = new RecordingProcessingEventReporter();
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual);
        var asset = new AssetRecord(Guid.NewGuid(), 1, 2, DateTime.UtcNow);
        var batches = new Queue<List<AssetRecord>>([[asset], []]);
        var operations = new ProcessingBackgroundService.ProcessingOperations(
            _ => Task.FromResult(1L),
            () => Task.FromResult(new AppConfig { Processing = new ProcessingConfig { BatchDelayMs = 0, MaxDegreeOfParallelism = 1 } }),
            () => Task.FromResult(new HashSet<Guid>()),
            (_, _, _) => Task.FromResult(batches.Dequeue()),
            (_, _, _, progress, _) =>
            {
                progress!.Report("resolver direct progress");
                return Task.FromResult<AdministrativeAreaResolution?>(null);
            },
            (_, _, _, _) => throw new AssertFailedException("airport lookup was not expected"),
            _ => Task.CompletedTask,
            (_, _, _) => throw new AssertFailedException("write was not expected"));

        await ProcessingBackgroundService.RunOnceAsync(NullLogger<ProcessingBackgroundService>.Instance, state, reporter, request, operations, CancellationToken.None);

        Assert.AreEqual(1, state.GetRecentLog().Count(line => line.EndsWith("resolver direct progress", StringComparison.Ordinal)));
        Assert.AreEqual(0, reporter.Events.OfType<LogEmitted>().Count(log => log.Message == "resolver direct progress"));
    }

    private static ProcessingState PriorState()
    {
        var state = new ProcessingState();
        state.StartRun(7);
        state.IncrementProcessed();
        state.CompleteRun();
        return state;
    }

    private static ProcessingBackgroundService.ProcessingOperations Operations(Func<CancellationToken, Task<long>> count) => new(
        count,
        () => throw new AssertFailedException("configuration read was not expected"),
        () => throw new AssertFailedException("skipped records were not expected"),
        (_, _, _) => throw new AssertFailedException("batch retrieval was not expected"),
        (_, _, _, _, _) => throw new AssertFailedException("resolution was not expected"),
        (_, _, _, _) => throw new AssertFailedException("airport lookup was not expected"),
        _ => throw new AssertFailedException("skipped write was not expected"),
        (_, _, _) => throw new AssertFailedException("location write was not expected"));

    private static void AssertLogSuffixes(ProcessingState state, params string[] suffixes)
    {
        var log = state.GetRecentLog();
        var prior = -1;
        foreach (var suffix in suffixes)
        {
            var index = log.ToList().FindIndex(line => line.Contains(suffix, StringComparison.Ordinal));
            Assert.IsTrue(index > prior, $"Expected '{suffix}' after prior log entry.");
            prior = index;
        }
    }
}
