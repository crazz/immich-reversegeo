using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class ProcessingBackgroundServiceRoutingCoverageTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);
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
            async (_, _, _) => { writeAccepted.TrySetResult(); await releaseCancellation.Task.WaitAsync(TestTimeout); });
        var pass = ProcessingBackgroundService.RunOnceAsync(NullLogger<ProcessingBackgroundService>.Instance, state, reporter, request, operations, cancellation.Token);
        await writeAccepted.Task.WaitAsync(TestTimeout);
        releaseCancellation.SetResult();
        cancellation.Cancel();
        await pass.WaitAsync(TestTimeout);

        Assert.AreEqual(1L, state.ProcessedThisRun);
        AssertLogSuffixes(state, "Run cancelled.", "Run complete. Processed=1 Skipped=0 Errors=0");
    }


    [TestMethod]
    public void NoCityLoggerOnlyDecision_IsUnreachableAfterMandatoryFallbackForEveryMatchedShape()
    {
        var matchedShapes = new[]
        {
            new GeoResult(string.Empty, null, null),
            new GeoResult("United States", null, null),
            new GeoResult("United States", "State", null),
            new GeoResult("United States", null, "City"),
            new GeoResult("United States", "State", "City")
        };
        var compatibilityGuardDispositions = 0;

        foreach (var matched in matchedShapes)
        {
            Assert.IsTrue(matched.HasMatch);
            var afterMandatoryFallback = matched.WithFallbackCity();
            Assert.IsNotNull(afterMandatoryFallback.City);
            if (ProcessingBackgroundService.IsLoggerOnlyNoCitySkip(afterMandatoryFallback))
            {
                compatibilityGuardDispositions++;
            }
        }

        Assert.AreEqual(0, compatibilityGuardDispositions);
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
    public async Task ResolverProgress_ReusesAdmittedSessionWithoutDirectStateBridge()
    {
        var state = new ProcessingState();
        var sink = new RecordingProcessingEventReporter();
        var reporter = new CapturingEventReporter(sink);
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual);
        var asset = new AssetRecord(Guid.NewGuid(), 1, 2, DateTime.UtcNow);
        var batches = new Queue<List<AssetRecord>>([[asset], []]);
        IProcessingRunEventSession? resolverSession = null;
        var operations = new ProcessingBackgroundService.ProcessingOperations(
            _ => Task.FromResult(1L),
            () => Task.FromResult(new AppConfig { Processing = new ProcessingConfig { BatchDelayMs = 0, MaxDegreeOfParallelism = 1 } }),
            () => Task.FromResult(new HashSet<Guid>()),
            (_, _, _) => Task.FromResult(batches.Dequeue()),
            async (_, _, _, session, _) =>
            {
                resolverSession = session;
                await session.ReportLogAsync(ProcessingLogLevel.Information, "resolver event progress");
                return null;
            },
            (_, _, _, _) => throw new AssertFailedException("airport lookup was not expected"),
            _ => Task.CompletedTask,
            (_, _, _) => throw new AssertFailedException("write was not expected"));

        await ProcessingBackgroundService.RunOnceAsync(NullLogger<ProcessingBackgroundService>.Instance, state, reporter, request, operations, CancellationToken.None);

        Assert.IsNotNull(reporter.OpenedSession);
        Assert.IsNotNull(resolverSession);
        Assert.AreNotSame(reporter.OpenedSession, resolverSession, "The executor supplies one guarded session decorator so every nested reporter entry shares the atomic first-failure boundary.");
        Assert.AreSame(request, reporter.OpenedSession.Request);
        Assert.AreSame(request, resolverSession.Request);
        var resolverLogs = sink.EventsFor(request).OfType<LogEmitted>().ToArray();
        Assert.AreEqual(3, resolverLogs.Length);
        Assert.AreEqual(ProcessingLogLevel.Information, resolverLogs[0].Level);
        Assert.AreEqual("Batch 1: fetched 1 assets (total processed so far: 0).", resolverLogs[0].Message);
        Assert.AreEqual(ProcessingLogLevel.Information, resolverLogs[1].Level);
        Assert.AreEqual("resolver event progress", resolverLogs[1].Message);
        Assert.AreEqual(ProcessingLogLevel.Warning, resolverLogs[2].Level);
        Assert.AreEqual($"Asset {asset.Id}: no country found at (1.0000, 2.0000), skipping.", resolverLogs[2].Message);
        Assert.AreEqual(0, state.GetRecentLog().Count(line => line.EndsWith("resolver event progress", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ResolverReportingBoundary_AbandonsOriginalSinkFailureWithoutRecursiveEventsAndRecovers()
    {
        foreach (var stage in new[] { "activity-begin", "information", "activity-end", "gadm-information" })
        {
            var gadm = stage == "gadm-information";
            await using var fixture = await AdministrativeAreaResolverEventReportingTests.ResolverFixture.CreateAsync(gadm ? AdministrativeAreaResolverEventReportingTests.Source.Gadm : AdministrativeAreaResolverEventReportingTests.Source.Overture, ready: gadm);
            var state = new ProcessingState();
            var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual);
            var attempts = new List<ProcessingEvent>();
            var sinkFailure = new InvalidOperationException($"{stage} sink failed");
            var faulted = false;
            var reporter = new ProcessingStateEventReporter(state, e =>
            {
                attempts.Add(e);
                var fault = stage switch { "activity-begin" => e is ActivityStarted, "information" => e is LogEmitted { Message: "Starting Overture administrative cache download for USA." }, "activity-end" => e is ActivityEnded, _ => e is LogEmitted { Message: "GADM administrative cache already ready for USA." } };
                if (fault && !faulted) { faulted = true; throw sinkFailure; }
            });
            state.MarkPending(); Assert.IsTrue(reporter.Arm(request));
            var batches = new Queue<List<AssetRecord>>([[new(Guid.NewGuid(), 38.9, -77.0, DateTime.UtcNow)], []]);
            var operations = new ProcessingBackgroundService.ProcessingOperations(_ => Task.FromResult(1L), () => Task.FromResult(new AppConfig { Processing = new ProcessingConfig { BatchDelayMs = 0, MaxDegreeOfParallelism = 1, UseAirportInfrastructure = false, UseGadmAdministrativeAreas = gadm, PreferGadmAdministrativeAreas = gadm } }), () => Task.FromResult(new HashSet<Guid>()), (_, _, _) => Task.FromResult(batches.Dequeue()), (_, _, c, s, ct) => fixture.ResolveAsync(c.UseGadmAdministrativeAreas, s, ct), (_, _, _, _) => throw new AssertFailedException("airport lookup was not expected"), _ => Task.CompletedTask, (_, _, _) => Task.CompletedTask);
            var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ProcessingBackgroundService.RunOnceAsync(NullLogger<ProcessingBackgroundService>.Instance, state, reporter, request, operations, CancellationToken.None));
            Assert.AreSame(sinkFailure, failure); Assert.IsTrue(faulted);
            var common = new[]
            {
                "RunStarted", "EligibilityDetermined:1",
                "LogEmitted:Information:Batch 1: fetched 1 assets (total processed so far: 0).",
                "LogEmitted:Information:Checking bundled Overture country coverage...",
                "LogEmitted:Information:Country resolved as United States (USA)."
            };
            string[] expectedAttempts = stage switch
            {
                "activity-begin" => [.. common, "ActivityStarted:Downloading Overture administrative cache for USA..."],
                "information" => [.. common,
                    "ActivityStarted:Downloading Overture administrative cache for USA...",
                    "LogEmitted:Information:Starting Overture administrative cache download for USA."],
                "activity-end" => [.. common,
                    "ActivityStarted:Downloading Overture administrative cache for USA...",
                    "LogEmitted:Information:Starting Overture administrative cache download for USA.",
                    "ActivityEnded:Downloading Overture administrative cache for USA..."],
                _ =>
                [
                    "RunStarted", "EligibilityDetermined:1",
                    "LogEmitted:Information:Batch 1: fetched 1 assets (total processed so far: 0).",
                    "LogEmitted:Information:Checking bundled Overture country coverage...",
                    "LogEmitted:Information:Country resolved as United States (USA).",
                    "LogEmitted:Information:Preparing GADM administrative caches for USA...",
                    "LogEmitted:Information:GADM administrative cache already ready for USA."
                ]
            };
            AssertExactEventSequence(attempts, expectedAttempts);
            if (stage == "activity-end")
            {
                var acceptedStart = attempts.OfType<ActivityStarted>().Single();
                var attemptedEnd = attempts.OfType<ActivityEnded>().Single();
                Assert.AreEqual(acceptedStart.ActivityId, attemptedEnd.ActivityId);
                Assert.IsNull(state.CurrentActivity, "Abandon must locally close the accepted start after the matching end projection faults; the end is only an attempted event, not a completed pair.");
            }
            Assert.AreEqual(0L, state.ProcessedThisRun); Assert.AreEqual(0L, state.SkippedThisRun); Assert.AreEqual(1L, state.ErrorsThisRun); Assert.IsFalse(state.IsRunning);
            var recovery = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Scheduled); state.MarkPending(); Assert.IsTrue(reporter.Arm(recovery));
            await ProcessingBackgroundService.RunOnceAsync(NullLogger<ProcessingBackgroundService>.Instance, state, reporter, recovery, Operations(_ => Task.FromResult(0L)), CancellationToken.None);

            var recoveryEvents = attempts.Where(processingEvent => ReferenceEquals(processingEvent.Request, recovery)).ToArray();
            CollectionAssert.AreEqual(new[] { typeof(RunStarted), typeof(EligibilityDetermined), typeof(RunFinished) }, recoveryEvents.Select(processingEvent => processingEvent.GetType()).ToArray());
            var terminal = recoveryEvents.OfType<RunFinished>().Single();
            Assert.AreSame(recovery, terminal.Request);
            Assert.AreSame(recovery, terminal.Result.Request);
            Assert.AreEqual(ProcessingRunOutcome.Completed, terminal.Result.Outcome);
            Assert.AreEqual(0L, terminal.Result.ProcessedCount);
            Assert.AreEqual(0L, terminal.Result.UpdatedCount);
            Assert.AreEqual(0L, terminal.Result.SkippedCount);
            Assert.AreEqual(0L, terminal.Result.FailedCount);
            Assert.AreEqual(0L, state.ProcessedThisRun);
            Assert.AreEqual(0L, state.SkippedThisRun);
            Assert.AreEqual(0L, state.ErrorsThisRun);
            Assert.IsFalse(state.IsRunning);
            Assert.IsNull(state.CurrentActivity);
            Assert.IsNull(state.LastError);
        }
    }

    [TestMethod]
    [DataRow("information")]
    [DataRow("activity-begin")]
    public async Task ResolverReportingAdmissionCancellation_CompletesCancelledWithoutReporterAbandonment(string admission)
    {
        await using var fixture = await AdministrativeAreaResolverEventReportingTests.ResolverFixture.CreateAsync(AdministrativeAreaResolverEventReportingTests.Source.Overture);
        using var cancellation = new CancellationTokenSource();
        var reporter = fixture.Reporter;
        var cancelledAtAdmission = false;
        reporter.BeforeAcceptAsync = (processingEvent, _) =>
        {
            var matches = admission == "information"
                ? processingEvent is LogEmitted { Level: ProcessingLogLevel.Information, Message: "Starting Overture administrative cache download for USA." }
                : processingEvent is ActivityStarted { Label: "Downloading Overture administrative cache for USA..." };
            if (matches && !cancelledAtAdmission)
            {
                cancelledAtAdmission = true;
                cancellation.Cancel();
            }

            return ValueTask.CompletedTask;
        };

        var state = new ProcessingState();
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual);
        var asset = new AssetRecord(Guid.NewGuid(), 38.9, -77.0, DateTime.UtcNow);
        var batches = new Queue<List<AssetRecord>>([[asset], []]);
        var operations = new ProcessingBackgroundService.ProcessingOperations(
            _ => Task.FromResult(1L),
            () => Task.FromResult(new AppConfig { Processing = new ProcessingConfig { BatchDelayMs = 0, MaxDegreeOfParallelism = 1, UseAirportInfrastructure = false } }),
            () => Task.FromResult(new HashSet<Guid>()),
            (_, _, _) => Task.FromResult(batches.Dequeue()),
            (lat, lon, config, session, ct) => fixture.ResolveAsync(false, session, ct),
            (_, _, _, _) => throw new AssertFailedException("Airport lookup was not expected."),
            _ => Task.CompletedTask,
            (_, _, _) => throw new AssertFailedException("Write must not be reached after admission cancellation."));

        await ProcessingBackgroundService.RunOnceAsync(NullLogger<ProcessingBackgroundService>.Instance, state, reporter, request, operations, cancellation.Token);

        Assert.IsTrue(cancelledAtAdmission);
        Assert.IsTrue(cancellation.IsCancellationRequested);
        var terminal = reporter.EventsFor(request).OfType<RunFinished>().Single();
        Assert.AreEqual(ProcessingRunOutcome.Cancelled, terminal.Result.Outcome);
        Assert.IsNull(terminal.Result.FailureMessage);
        Assert.AreEqual(0, reporter.Attempts.OfType<ProgressChanged>().Count());
        Assert.AreEqual(0, reporter.Attempts.OfType<LogEmitted>().Count(log => log.Level == ProcessingLogLevel.Error));
        Assert.AreEqual(0, reporter.Attempts.OfType<LogEmitted>().Count(log => log.Message.Contains($"Asset {asset.Id} [", StringComparison.Ordinal)));

        var starts = reporter.EventsFor(request).OfType<ActivityStarted>().ToArray();
        var ends = reporter.EventsFor(request).OfType<ActivityEnded>().ToArray();
        if (admission == "information")
        {
            Assert.AreEqual(1, starts.Length, "The activity start was accepted before Information admission cancelled.");
            Assert.AreEqual(1, ends.Length, "Accepted activities must use non-cancelled cleanup.");
            Assert.AreEqual(starts[0].ActivityId, ends[0].ActivityId);
        }
        else
        {
            Assert.AreEqual(0, starts.Length, "The activity begin was cancelled before acceptance.");
            Assert.AreEqual(0, ends.Length, "No cleanup is needed when activity begin was not accepted.");
        }
    }

    private sealed class CapturingEventReporter(IProcessingEventReporter inner) : IProcessingEventReporter
    {
        public IProcessingRunEventSession? OpenedSession { get; private set; }

        public async ValueTask<IProcessingRunEventSession> OpenRunAsync(ProcessingRunRequest request, DateTimeOffset startedAtUtc, CancellationToken cancellationToken = default)
        {
            OpenedSession = await inner.OpenRunAsync(request, startedAtUtc, cancellationToken);
            return OpenedSession;
        }
    }

    private static void AssertExactEventSequence(IEnumerable<ProcessingEvent> events, params string[] expected)
    {
        var labelsById = new Dictionary<Guid, string>();
        var actual = events.Select(processingEvent => processingEvent switch
        {
            RunStarted => "RunStarted",
            EligibilityDetermined eligibility => $"EligibilityDetermined:{eligibility.EligibleCount}",
            LogEmitted log => $"LogEmitted:{log.Level}:{log.Message}",
            ActivityStarted started => DescribeStart(started, labelsById),
            ActivityEnded ended => $"ActivityEnded:{labelsById[ended.ActivityId]}",
            ProgressChanged progress => $"ProgressChanged:{progress.Progress.UpdatedCount}:{progress.Progress.SkippedCount}:{progress.Progress.FailedCount}",
            RunFinished finished => $"RunFinished:{finished.Result.Outcome}:{finished.Result.UpdatedCount}:{finished.Result.SkippedCount}:{finished.Result.FailedCount}",
            _ => processingEvent.GetType().Name
        }).ToArray();
        CollectionAssert.AreEqual(expected, actual, $"Expected: {string.Join(" | ", expected)}. Actual: {string.Join(" | ", actual)}");
    }

    private static string DescribeStart(ActivityStarted started, IDictionary<Guid, string> labelsById)
    {
        labelsById.Add(started.ActivityId, started.Label);
        return $"ActivityStarted:{started.Label}";
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
