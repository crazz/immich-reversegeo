using System.Collections.Concurrent;
using System.Reflection;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Overture.Models;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class ProcessingRunExecutorChange11Tests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset Epoch = new(2026, 8, 30, 20, 30, 0, TimeSpan.Zero);

    [TestMethod]
    [DataRow("eligibility")]
    [DataRow("skipped-information")]
    [DataRow("batch-information")]
    [DataRow("warning")]
    [DataRow("trace")]
    [DataRow("error")]
    public async Task ExecuteAsync_ActiveCancellationAtTokenBearingReporterAdmission_ReturnsOneHealthyCancelledTerminal(string admission)
    {
        using var cancellation = new CancellationTokenSource();
        var asset = Asset(1);
        var fixture = ConfigureAdmissionScenario(asset, admission);
        var targetEntered = Signal<ProcessingEvent>();
        var targetRelease = Signal();
        var reporter = new Change11Reporter(async (processingEvent, token) =>
        {
            if (IsTargetAdmission(processingEvent, admission))
            {
                targetEntered.TrySetResult(processingEvent);
                await targetRelease.Task.WaitAsync(Bound, token).ConfigureAwait(false);
            }
        });
        var request = Request();

        var execution = fixture.Executor.ExecuteAsync(request, reporter, cancellation.Token);
        var attemptedTarget = await targetEntered.Task.WaitAsync(Bound);
        Assert.AreSame(request, attemptedTarget.Request);
        cancellation.Cancel();
        targetRelease.TrySetResult();
        var result = await execution.WaitAsync(Bound);

        Assert.AreSame(request, result.Request);
        Assert.AreEqual(ProcessingRunOutcome.Cancelled, result.Outcome);
        AssertCounts(result, 0, 0, 0, 0);
        Assert.AreEqual(1, reporter.Attempts.Count(IsTarget));
        Assert.AreEqual(0, reporter.Events.Count(IsTarget));
        Assert.AreEqual(0, reporter.Events.OfType<ProgressChanged>().Count());
        Assert.AreEqual(1, reporter.Events.OfType<RunFinished>().Count());
        Assert.AreSame(result, reporter.Events.OfType<RunFinished>().Single().Result);
        Assert.AreEqual(ProcessingRunOutcome.Cancelled, reporter.Events.OfType<RunFinished>().Single().Result.Outcome);
        Assert.IsInstanceOfType<RunFinished>(reporter.Events.Last());
        Assert.IsTrue(reporter.Events.All(processingEvent => ReferenceEquals(request, processingEvent.Request)));
        Assert.IsFalse(fixture.Logger.Entries.Any(entry => entry.Message == "Fatal error during processing run"));

        bool IsTarget(ProcessingEvent processingEvent) => IsTargetAdmission(processingEvent, admission);
    }

    [TestMethod]
    public async Task ExecuteAsync_MatchedLocationFallsBackToState_WritesExactStateCityAndOnlyUpdated()
    {
        await AssertMatchedFallbackWriteAsync(
            new GeoResult("Country", "FallbackState", null),
            new GeoResult("Country", "FallbackState", "FallbackState"));
    }

    [TestMethod]
    public async Task ExecuteAsync_MatchedLocationFallsBackToCountry_WritesExactCountryCityAndOnlyUpdated()
    {
        await AssertMatchedFallbackWriteAsync(
            new GeoResult("FallbackCountry", null, null),
            new GeoResult("FallbackCountry", null, "FallbackCountry"));
    }

    [TestMethod]
    public async Task ExecuteAsync_ParallelReporterFailure_CapturesFirstExactlyAndRefusesEveryLaterSessionCall()
    {
        var first = Asset(1);
        var second = Asset(2);
        var firstEntered = Signal();
        var secondEntered = Signal();
        var releaseResolvers = Signal();
        var firstResolved = Signal();
        var secondResolved = Signal();
        var fixture = TwoAssetFixture(
            first,
            second,
            maxParallelism: 2,
            verbose: true,
            async (asset, session, token) =>
            {
                (asset.Id == first.Id ? firstEntered : secondEntered).TrySetResult();
                await releaseResolvers.Task.WaitAsync(Bound, token).ConfigureAwait(false);
                (asset.Id == first.Id ? firstResolved : secondResolved).TrySetResult();
                return Resolution(new GeoResult("Country", "State", asset.Id == first.Id ? "First" : "Second"));
            });
        var failure = new TestSinkException("first trace sink failure");
        var failedSinkEntered = Signal();
        var releaseFailure = Signal();
        var traceSinkAttempts = 0;
        var reporter = new Change11Reporter(async (processingEvent, token) =>
        {
            if (processingEvent is LogEmitted { Level: ProcessingLogLevel.Trace })
            {
                Interlocked.Increment(ref traceSinkAttempts);
                failedSinkEntered.TrySetResult();
                await releaseFailure.Task.WaitAsync(Bound, token).ConfigureAwait(false);
                throw failure;
            }
        });
        var request = Request();

        var execution = fixture.Executor.ExecuteAsync(request, reporter, CancellationToken.None);
        await firstEntered.Task.WaitAsync(Bound);
        await secondEntered.Task.WaitAsync(Bound);
        releaseResolvers.TrySetResult();
        await firstResolved.Task.WaitAsync(Bound);
        await secondResolved.Task.WaitAsync(Bound);
        await failedSinkEntered.Task.WaitAsync(Bound);
        releaseFailure.TrySetResult();

        var observed = await Assert.ThrowsExactlyAsync<TestSinkException>(() => execution.WaitAsync(Bound));
        Assert.AreSame(failure, observed);
        Assert.AreEqual(1, traceSinkAttempts);
        Assert.AreEqual(1, reporter.Attempts.OfType<LogEmitted>().Count(log => log.Level == ProcessingLogLevel.Trace));
        Assert.AreEqual(0, reporter.Events.OfType<LogEmitted>().Count(log => log.Level == ProcessingLogLevel.Trace));
        Assert.AreEqual(0, reporter.Attempts.OfType<ProgressChanged>().Count());
        Assert.AreEqual(0, reporter.Attempts.OfType<RunFinished>().Count());
        Assert.IsTrue(reporter.Attempts.All(processingEvent => ReferenceEquals(request, processingEvent.Request)));
        Assert.AreEqual(0, fixture.Writes.Count);
    }

    [TestMethod]
    public async Task ExecuteAsync_MixedBranches_ProduceExactCombinedCausalEventsWritesAndAccounting()
    {
        var suppressed = Asset(1);
        var containing = Asset(2);
        var preserveAdmin = Asset(3);
        var airportFallback = Asset(4);
        var stateFallback = Asset(5);
        var countryFallback = Asset(6);
        var noCountry = Asset(7);
        var noAdmin = Asset(8);
        var handledFailure = Asset(9);
        var ordered = new[] { suppressed, containing, preserveAdmin, airportFallback, stateFallback, countryFallback, noCountry, noAdmin, handledFailure };
        var config = DefaultConfig();
        config.Processing.BatchSize = ordered.Length;
        config.Processing.BatchDelayMs = 3;
        config.Processing.MaxDegreeOfParallelism = 1;
        config.Processing.UseAirportInfrastructure = true;
        config.Processing.VerboseLogging = true;
        var fixture = new Change11ExecutorProbe(new Change11Scenario
        {
            Eligible = ordered.Length,
            Config = config,
            SkippedIds = new HashSet<Guid> { suppressed.Id },
            Pages = [ordered, Array.Empty<AssetRecord>()],
            Resolve = (asset, session, token) =>
            {
                if (asset.Id == handledFailure.Id)
                {
                    throw new InvalidOperationException("resolver exploded");
                }

                if (asset.Id == noCountry.Id)
                {
                    return Task.FromResult<AdministrativeAreaResolution?>(null);
                }

                var geo = asset.Id == noAdmin.Id
                    ? new GeoResult(null, null, null)
                    : asset.Id == containing.Id
                        ? new GeoResult("Country", "State", "Administrative")
                        : asset.Id == preserveAdmin.Id
                            ? new GeoResult("Country", "State", "PreservedAdmin")
                            : asset.Id == airportFallback.Id
                                ? new GeoResult("Country", "State", null)
                                : asset.Id == stateFallback.Id
                                    ? new GeoResult("Country", "FallbackState", null)
                                    : new GeoResult("Country", null, null);
                return Task.FromResult<AdministrativeAreaResolution?>(Resolution(geo));
            },
            Infrastructure = (asset, token) => asset.Id switch
            {
                var id when id == containing.Id => Task.FromResult(Diagnostics("ContainingAirport", contains: true)),
                var id when id == preserveAdmin.Id => Task.FromResult(Diagnostics("NearbyIgnored", contains: false)),
                var id when id == airportFallback.Id => Task.FromResult(Diagnostics("NearbyFallback", contains: false)),
                _ => Task.FromResult(EmptyDiagnostics())
            }
        });
        var reporter = new Change11Reporter((processingEvent, token) =>
        {
            fixture.RecordEvent(processingEvent);
            return ValueTask.CompletedTask;
        });
        var request = Request();

        var result = await fixture.Executor.ExecuteAsync(request, reporter, CancellationToken.None).WaitAsync(Bound);

        Assert.AreEqual(ProcessingRunOutcome.Completed, result.Outcome);
        AssertCounts(result, 8, 5, 2, 1);
        CollectionAssert.AreEqual(
            new[]
            {
                (containing.Id, new GeoResult("Country", "State", "ContainingAirport")),
                (preserveAdmin.Id, new GeoResult("Country", "State", "PreservedAdmin")),
                (airportFallback.Id, new GeoResult("Country", "State", "NearbyFallback")),
                (stateFallback.Id, new GeoResult("Country", "FallbackState", "FallbackState")),
                (countryFallback.Id, new GeoResult("Country", null, "Country"))
            },
            fixture.Writes.ToArray());
        CollectionAssert.AreEqual(new[] { noCountry.Id, noAdmin.Id }, fixture.SkippedWrites.ToArray());
        CollectionAssert.AreEqual(
            new[] { AssetCursor.Initial, new AssetCursor(handledFailure.CreatedAt, handledFailure.Id) },
            fixture.Cursors.ToArray());
        CollectionAssert.AreEqual(new[] { 9, 9 }, fixture.BatchSizes.ToArray());
        CollectionAssert.AreEqual(new[] { TimeSpan.FromMilliseconds(3) }, fixture.Delays.ToArray());
        Assert.AreEqual(1, fixture.ConfigCalls);
        Assert.AreEqual(1, fixture.SkippedSnapshotCalls);
        Assert.AreEqual(6, fixture.InfrastructureCalls);
        Assert.AreEqual(8, fixture.ResolverSessions.Count);
        Assert.AreEqual(1, fixture.ResolverSessions.Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.IsTrue(fixture.ResolverSessions.All(session => ReferenceEquals(request, session.Request)));
        Assert.IsFalse(fixture.Operations.Any(operation => operation.Contains(suppressed.Id.ToString(), StringComparison.Ordinal)));

        var eventShape = reporter.Events.Select(processingEvent => processingEvent switch
        {
            RunStarted => "Started",
            EligibilityDetermined => "Eligibility",
            LogEmitted log => $"Log:{log.Level}",
            ProgressChanged progress => $"Progress:{progress.Progress.UpdatedCount}/{progress.Progress.SkippedCount}/{progress.Progress.FailedCount}",
            RunFinished => "Finished",
            _ => processingEvent.GetType().Name
        }).ToArray();
        CollectionAssert.AreEqual(
            new[]
            {
                "Started", "Eligibility", "Log:Information", "Log:Information",
                "Log:Trace", "Progress:1/0/0",
                "Log:Trace", "Progress:2/0/0",
                "Log:Trace", "Progress:3/0/0",
                "Log:Trace", "Progress:4/0/0",
                "Log:Trace", "Progress:5/0/0",
                "Log:Warning", "Progress:5/1/0",
                "Log:Warning", "Progress:5/2/0",
                "Log:Error", "Progress:5/2/1",
                "Finished"
            },
            eventShape);
        Assert.AreEqual(reporter.Events.Count, reporter.Attempts.Count);
        Assert.IsTrue(reporter.Events.All(processingEvent => ReferenceEquals(request, processingEvent.Request)));
        Assert.AreSame(result, reporter.Events.OfType<RunFinished>().Single().Result);
        Assert.AreEqual(8, reporter.Events.OfType<ProgressChanged>().Select(item => item.Progress.ProcessedCount).Distinct().Count());

        var operations = fixture.Operations.ToArray();
        var expectedTraces = new Dictionary<Guid, string>
        {
            [containing.Id] = $"Asset {containing.Id}: ContainingAirport, State, Country",
            [preserveAdmin.Id] = $"Asset {preserveAdmin.Id}: PreservedAdmin, State, Country",
            [airportFallback.Id] = $"Asset {airportFallback.Id}: NearbyFallback, State, Country",
            [stateFallback.Id] = $"Asset {stateFallback.Id}: FallbackState, FallbackState, Country",
            [countryFallback.Id] = $"Asset {countryFallback.Id}: Country, , Country"
        };
        foreach (var asset in new[] { containing, preserveAdmin, airportFallback, stateFallback, countryFallback })
        {
            var trace = $"event:Trace:{expectedTraces[asset.Id]}";
            var disposition = $"event:Disposition:{asset.Id}:Updated";
            Assert.IsTrue(IndexOfExact(operations, $"admin:{asset.Id}") < IndexOfExact(operations, $"airport:{asset.Id}"));
            Assert.IsTrue(IndexOfExact(operations, trace) < IndexOfExact(operations, $"write-start:{asset.Id}"));
            Assert.IsTrue(IndexOfExact(operations, $"write-start:{asset.Id}") < IndexOfExact(operations, $"write-end:{asset.Id}"));
            Assert.IsTrue(IndexOfExact(operations, $"write-end:{asset.Id}") < IndexOfExact(operations, disposition));
            Assert.AreEqual(1, operations.Count(item => item == disposition));
        }

        var noCountryWarning = $"Asset {noCountry.Id}: no country found at (7.0000, 7.0000), skipping.";
        var noAdminWarning = $"Asset {noAdmin.Id}: country=Country but no admin match, skipping.";
        foreach (var pair in new[] { (Asset: noCountry, Warning: noCountryWarning), (Asset: noAdmin, Warning: noAdminWarning) })
        {
            var warning = $"event:Warning:{pair.Warning}";
            var disposition = $"event:Disposition:{pair.Asset.Id}:Skipped";
            Assert.AreEqual(pair.Warning, reporter.Events.OfType<LogEmitted>().Single(log => log.Message == pair.Warning).Message);
            Assert.IsTrue(IndexOfExact(operations, warning) < IndexOfExact(operations, $"skip-start:{pair.Asset.Id}"));
            Assert.IsTrue(IndexOfExact(operations, $"skip-start:{pair.Asset.Id}") < IndexOfExact(operations, $"skip-end:{pair.Asset.Id}"));
            Assert.IsTrue(IndexOfExact(operations, $"skip-end:{pair.Asset.Id}") < IndexOfExact(operations, disposition));
            Assert.AreEqual(1, operations.Count(item => item == disposition));
        }

        var failedDisposition = $"event:Disposition:{handledFailure.Id}:Failed";
        Assert.IsTrue(IndexOfExact(operations, $"event:Error:Asset {handledFailure.Id} [FindCountry]: resolver exploded") < IndexOfExact(operations, failedDisposition));
        Assert.AreEqual(1, operations.Count(item => item == failedDisposition));
        var dispositions = operations.Where(item => item.StartsWith("event:Disposition:", StringComparison.Ordinal)).ToArray();
        Assert.AreEqual(8, dispositions.Length);
        Assert.AreEqual(8, dispositions.Distinct(StringComparer.Ordinal).Count());
        Assert.AreEqual("event:Finished", operations.Last());
        Assert.AreEqual(IndexOfExact(operations, "event:Finished"), operations.Length - 1);

        var error = reporter.Events.OfType<LogEmitted>().Single(log => log.Level == ProcessingLogLevel.Error);
        Assert.AreEqual($"Asset {handledFailure.Id} [FindCountry]: resolver exploded", error.Message);
        var loggerError = fixture.Logger.Entries.Single(entry => ReferenceEquals(entry.Exception, fixture.HandledFailure));
        Assert.AreEqual(LogLevel.Error, loggerError.Level);
        Assert.IsTrue(loggerError.Message.Contains("step=FindCountry", StringComparison.Ordinal));
        Assert.IsTrue(loggerError.Message.Contains(handledFailure.Id.ToString(), StringComparison.Ordinal));
        Assert.IsTrue(loggerError.Message.Contains("InvalidOperationException", StringComparison.Ordinal));
        Assert.IsInstanceOfType<RunFinished>(reporter.Events.Last());
    }

    [TestMethod]
    public async Task ExecuteAsync_ForeignOceAssetFailure_AllowsPeerToCommitAndRunToComplete()
    {
        var failing = Asset(1);
        var peer = Asset(2);
        var failingEntered = Signal();
        var peerEntered = Signal();
        var release = Signal();
        var fixture = TwoAssetFixture(
            failing,
            peer,
            maxParallelism: 2,
            verbose: false,
            async (asset, session, token) =>
            {
                (asset.Id == failing.Id ? failingEntered : peerEntered).TrySetResult();
                await release.Task.WaitAsync(Bound, token).ConfigureAwait(false);
                if (asset.Id == failing.Id)
                {
                    throw new OperationCanceledException("foreign owner cancelled");
                }

                return Resolution(new GeoResult("Country", "State", "PeerCity"));
            });
        var reporter = new Change11Reporter();
        var request = Request();
        var execution = fixture.Executor.ExecuteAsync(request, reporter, CancellationToken.None);
        await failingEntered.Task.WaitAsync(Bound);
        await peerEntered.Task.WaitAsync(Bound);
        release.TrySetResult();
        var result = await execution.WaitAsync(Bound);

        Assert.AreEqual(ProcessingRunOutcome.Completed, result.Outcome);
        AssertCounts(result, 2, 1, 0, 1);
        var write = fixture.Writes.Single();
        Assert.AreEqual(peer.Id, write.AssetId);
        Assert.AreEqual(new GeoResult("Country", "State", "PeerCity"), write.Geo);
        var error = reporter.Events.OfType<LogEmitted>().Single(log => log.Level == ProcessingLogLevel.Error);
        Assert.AreEqual($"Asset {failing.Id} [FindCountry]: foreign owner cancelled", error.Message);
        var progress = reporter.Events.OfType<ProgressChanged>().ToArray();
        Assert.AreEqual(2, progress.Length);
        Assert.AreEqual(1L, progress[0].Progress.ProcessedCount);
        Assert.AreEqual(2L, progress[1].Progress.ProcessedCount);
        Assert.AreEqual(1L, progress[1].Progress.UpdatedCount);
        Assert.AreEqual(1L, progress[1].Progress.FailedCount);
        Assert.AreEqual(1, fixture.Logger.Entries.Count(entry => ReferenceEquals(entry.Exception, fixture.ForeignFailure)));
        Assert.AreEqual(1, reporter.Events.OfType<RunFinished>().Count());
    }

    [TestMethod]
    public async Task ExecuteAsync_ActiveParallelCancellation_PreservesPriorCommitClosesEveryActivityAndLeavesInterruptedAssetsUncounted()
    {
        using var cancellation = new CancellationTokenSource();
        var committed = Asset(1);
        var interruptedA = Asset(2);
        var interruptedB = Asset(3);
        var enteredA = Signal();
        var enteredB = Signal();
        var cancellationPointA = Signal();
        var cancellationPointB = Signal();
        var config = DefaultConfig();
        config.Processing.MaxDegreeOfParallelism = 2;
        var fixture = new Change11ExecutorProbe(new Change11Scenario
        {
            Eligible = 3,
            Config = config,
            Pages = [new[] { committed }, new[] { interruptedA, interruptedB }, Array.Empty<AssetRecord>()],
            Resolve = async (asset, session, token) =>
            {
                if (asset.Id == committed.Id)
                {
                    return Resolution(new GeoResult("Country", "State", "Committed"));
                }

                await using var activity = await session.BeginActivityAsync($"activity-{asset.Id}", token);
                (asset.Id == interruptedA.Id ? enteredA : enteredB).TrySetResult();
                await (asset.Id == interruptedA.Id ? cancellationPointA.Task : cancellationPointB.Task)
                    .WaitAsync(Bound, token)
                    .ConfigureAwait(false);
                return null;
            }
        });
        var reporter = new Change11Reporter();
        var request = Request();

        var execution = fixture.Executor.ExecuteAsync(request, reporter, cancellation.Token);
        await enteredA.Task.WaitAsync(Bound);
        await enteredB.Task.WaitAsync(Bound);
        cancellation.Cancel();
        var result = await execution.WaitAsync(Bound);

        Assert.AreEqual(ProcessingRunOutcome.Cancelled, result.Outcome);
        AssertCounts(result, 1, 1, 0, 0);
        Assert.AreEqual(committed.Id, fixture.Writes.Single().AssetId);
        var starts = reporter.Events.OfType<ActivityStarted>().ToArray();
        var ends = reporter.Events.OfType<ActivityEnded>().ToArray();
        Assert.AreEqual(2, starts.Length);
        Assert.AreEqual(2, ends.Length);
        CollectionAssert.AreEquivalent(starts.Select(item => item.ActivityId).ToArray(), ends.Select(item => item.ActivityId).ToArray());
        var allEvents = reporter.Events.ToArray();
        var terminalIndex = Array.FindIndex(allEvents, item => item is RunFinished);
        Assert.IsTrue(ends.All(item => Array.IndexOf(allEvents, item) < terminalIndex));
        Assert.AreEqual(0, reporter.Events.OfType<ProgressChanged>().Count(item => item.Progress.FailedCount > 0));
        Assert.AreEqual(1, reporter.Events.OfType<RunFinished>().Count());
        Assert.IsTrue(reporter.Events.All(item => ReferenceEquals(request, item.Request)));
    }

    [TestMethod]
    public async Task ExecuteAsync_ActivityEndFailure_PropagatesOriginalOnceWithoutErrorDispositionOrTerminalRetry()
    {
        var asset = Asset(1);
        var fixture = OneAssetFixture(
            asset,
            resolve: async (current, session, token) =>
            {
                await using var activity = await session.BeginActivityAsync("cleanup", token);
                return Resolution(new GeoResult("Country", "State", "City"));
            });
        var failure = new TestSinkException("activity end failed");
        var reporter = new Change11Reporter((processingEvent, token) =>
            processingEvent is ActivityEnded ? ValueTask.FromException(failure) : ValueTask.CompletedTask);
        var request = Request();

        var observed = await Assert.ThrowsExactlyAsync<TestSinkException>(() =>
            fixture.Executor.ExecuteAsync(request, reporter, CancellationToken.None).WaitAsync(Bound));

        Assert.AreSame(failure, observed);
        var started = reporter.Events.OfType<ActivityStarted>().Single();
        var attemptedEnd = reporter.Attempts.OfType<ActivityEnded>().Single();
        Assert.AreEqual(started.ActivityId, attemptedEnd.ActivityId);
        Assert.AreEqual(0, reporter.Events.OfType<ActivityEnded>().Count());
        Assert.AreEqual(0, reporter.Attempts.OfType<LogEmitted>().Count(log => log.Level == ProcessingLogLevel.Error));
        Assert.AreEqual(0, reporter.Attempts.OfType<ProgressChanged>().Count());
        Assert.AreEqual(0, reporter.Attempts.OfType<RunFinished>().Count());
        Assert.AreEqual(0, fixture.Writes.Count);
    }

    [TestMethod]
    public async Task ExecuteAsync_SkippedInsertFailure_LogsExactErrorCommitsOnlyFailedAndFinishesOnce()
    {
        var asset = Asset(1);
        var insertEntered = Signal();
        var releaseInsert = Signal();
        var failure = new InvalidOperationException("sqlite insert failed");
        var fixture = OneAssetFixture(
            asset,
            resolve: (current, session, token) => Task.FromResult<AdministrativeAreaResolution?>(null),
            addSkipped: async current =>
            {
                insertEntered.TrySetResult();
                await releaseInsert.Task.WaitAsync(Bound).ConfigureAwait(false);
                throw failure;
            });
        var reporter = new Change11Reporter();
        var request = Request();
        var execution = fixture.Executor.ExecuteAsync(request, reporter, CancellationToken.None);
        await insertEntered.Task.WaitAsync(Bound);
        releaseInsert.TrySetResult();
        var result = await execution.WaitAsync(Bound);

        Assert.AreEqual(ProcessingRunOutcome.Completed, result.Outcome);
        AssertCounts(result, 1, 0, 0, 1);
        Assert.AreEqual(0, fixture.SkippedWrites.Count);
        Assert.AreEqual(0, fixture.Writes.Count);
        var logs = reporter.Events.OfType<LogEmitted>().ToArray();
        Assert.AreEqual(1, logs.Count(log => log.Level == ProcessingLogLevel.Warning));
        var error = logs.Single(log => log.Level == ProcessingLogLevel.Error);
        Assert.AreEqual($"Asset {asset.Id} [FindCountry]: sqlite insert failed", error.Message);
        var progress = reporter.Events.OfType<ProgressChanged>().Single();
        Assert.AreEqual(1, progress.Progress.FailedCount);
        Assert.AreEqual(0, progress.Progress.SkippedCount);
        Assert.AreEqual(1, fixture.Logger.Entries.Count(entry => ReferenceEquals(failure, entry.Exception)));
        Assert.AreEqual(1, reporter.Events.OfType<RunFinished>().Count());
        Assert.IsInstanceOfType<RunFinished>(reporter.Events.Last());
        Assert.IsTrue(reporter.Events.All(item => ReferenceEquals(request, item.Request)));
    }

    [TestMethod]
    public async Task ExecuteAsync_LaterOutOfMemory_PreservesPriorCommitLogsFatalOnceAndAddsNoAssetFailure()
    {
        var committed = Asset(1);
        var fatalAsset = Asset(2);
        var fatalEntered = Signal();
        var releaseFatal = Signal();
        var fatal = new OutOfMemoryException("controlled oom");
        var fixture = TwoAssetFixture(
            committed,
            fatalAsset,
            maxParallelism: 1,
            verbose: false,
            async (asset, session, token) =>
            {
                if (asset.Id == fatalAsset.Id)
                {
                    fatalEntered.TrySetResult();
                    await releaseFatal.Task.WaitAsync(Bound, token).ConfigureAwait(false);
                    throw fatal;
                }

                return Resolution(new GeoResult("Country", "State", "Committed"));
            });
        var reporter = new Change11Reporter();
        var execution = fixture.Executor.ExecuteAsync(Request(), reporter, CancellationToken.None);
        await fatalEntered.Task.WaitAsync(Bound);
        Assert.AreEqual(committed.Id, fixture.Writes.Single().AssetId);
        releaseFatal.TrySetResult();
        var result = await execution.WaitAsync(Bound);

        Assert.AreEqual(ProcessingRunOutcome.Failed, result.Outcome);
        Assert.AreEqual("controlled oom", result.FailureMessage);
        AssertCounts(result, 1, 1, 0, 0);
        Assert.AreEqual(0, reporter.Events.OfType<LogEmitted>().Count(log => log.Level == ProcessingLogLevel.Error));
        Assert.AreEqual(0, reporter.Events.OfType<ProgressChanged>().Count(item => item.Progress.FailedCount > 0));
        var fatalLog = fixture.Logger.Entries.Single(entry => entry.Message == "Fatal error during processing run");
        Assert.AreSame(fatal, fatalLog.Exception);
        Assert.AreEqual(LogLevel.Error, fatalLog.Level);
        Assert.AreEqual(1, reporter.Events.OfType<RunFinished>().Count());
        Assert.AreSame(result, reporter.Events.OfType<RunFinished>().Single().Result);
    }

    [TestMethod]
    public async Task ExecuteAsync_PassDelayFailure_LogsExactFatalOnceRetainsCommitAndHasNoFailedDisposition()
    {
        var asset = Asset(1);
        var delayEntered = Signal();
        var releaseDelay = Signal();
        var failure = new InvalidOperationException("delay infrastructure failed");
        var config = DefaultConfig();
        config.Processing.BatchDelayMs = 5;
        var fixture = OneAssetFixture(
            asset,
            delay: async (delay, token) =>
            {
                delayEntered.TrySetResult();
                await releaseDelay.Task.WaitAsync(Bound, token).ConfigureAwait(false);
                throw failure;
            },
            config: config);
        var reporter = new Change11Reporter();
        var execution = fixture.Executor.ExecuteAsync(Request(), reporter, CancellationToken.None);
        await delayEntered.Task.WaitAsync(Bound);
        releaseDelay.TrySetResult();
        var result = await execution.WaitAsync(Bound);

        Assert.AreEqual(ProcessingRunOutcome.Failed, result.Outcome);
        Assert.AreEqual("delay infrastructure failed", result.FailureMessage);
        AssertCounts(result, 1, 1, 0, 0);
        Assert.AreEqual(1, fixture.Writes.Count);
        Assert.AreEqual(0, reporter.Events.OfType<ProgressChanged>().Count(item => item.Progress.FailedCount > 0));
        Assert.AreEqual(0, reporter.Events.OfType<LogEmitted>().Count(log => log.Level == ProcessingLogLevel.Error));
        var fatalLog = fixture.Logger.Entries.Single(entry => entry.Message == "Fatal error during processing run");
        Assert.AreSame(failure, fatalLog.Exception);
        Assert.AreEqual(1, reporter.Events.OfType<RunFinished>().Count());
    }

    [TestMethod]
    public async Task ExecuteAsync_TerminalSinkFailure_PropagatesOriginalExactlyOnceWithoutRetryOrSyntheticResult()
    {
        var failure = new TestSinkException("terminal sink failed");
        var terminalEntered = Signal();
        var releaseTerminal = Signal();
        var reporter = new Change11Reporter(async (processingEvent, token) =>
        {
            if (processingEvent is RunFinished)
            {
                terminalEntered.TrySetResult();
                await releaseTerminal.Task.WaitAsync(Bound, token).ConfigureAwait(false);
                throw failure;
            }
        });
        var fixture = new Change11ExecutorProbe(new Change11Scenario());
        var execution = fixture.Executor.ExecuteAsync(Request(), reporter, CancellationToken.None);
        await terminalEntered.Task.WaitAsync(Bound);
        releaseTerminal.TrySetResult();
        var observed = await Assert.ThrowsExactlyAsync<TestSinkException>(() => execution.WaitAsync(Bound));

        Assert.AreSame(failure, observed);
        Assert.AreEqual(1, reporter.Attempts.OfType<RunFinished>().Count());
        Assert.AreEqual(0, reporter.Events.OfType<RunFinished>().Count());
        Assert.AreEqual(0, reporter.Attempts.OfType<ProgressChanged>().Count());
        Assert.AreEqual(2, reporter.Events.Count);
    }

    [TestMethod]
    public async Task ExecuteAsync_ConfigurationReadFailure_LogsExactFatalAndReturnsFailedWithoutPerAssetWork()
    {
        var failure = new InvalidOperationException("configuration read failed");
        var configuration = new ThrowingConfiguration(failure);
        var fixture = new Change11ExecutorProbe(new Change11Scenario
        {
            Eligible = 1,
            Configuration = configuration
        });
        var reporter = new Change11Reporter((processingEvent, token) =>
        {
            fixture.RecordEvent(processingEvent);
            return ValueTask.CompletedTask;
        });
        var request = Request();

        var result = await fixture.Executor.ExecuteAsync(request, reporter, CancellationToken.None).WaitAsync(Bound);

        Assert.AreSame(request, result.Request);
        Assert.AreEqual(ProcessingRunOutcome.Failed, result.Outcome);
        Assert.AreEqual("configuration read failed", result.FailureMessage);
        AssertCounts(result, 0, 0, 0, 0);
        Assert.AreEqual(1, configuration.Calls);
        Assert.AreEqual(1, fixture.SkippedSnapshotCalls);
        Assert.AreEqual(0, fixture.BatchCalls);
        Assert.AreEqual(0, fixture.Writes.Count);
        Assert.AreEqual(0, reporter.Events.OfType<ProgressChanged>().Count());
        Assert.AreEqual(0, reporter.Events.OfType<LogEmitted>().Count());
        var fatal = fixture.Logger.Entries.Single(entry => entry.Message == "Fatal error during processing run");
        Assert.AreSame(failure, fatal.Exception);
        Assert.AreEqual(LogLevel.Error, fatal.Level);
        var terminal = reporter.Events.OfType<RunFinished>().Single();
        Assert.AreSame(request, terminal.Request);
        Assert.AreSame(result, terminal.Result);
        Assert.IsInstanceOfType<RunFinished>(reporter.Events.Last());
        Assert.AreEqual(3, reporter.Events.Count);
        Assert.AreEqual("event:Finished", fixture.Operations.Last());
    }

    [TestMethod]
    public async Task ExecuteAsync_BatchFetchForeignOceAndOrdinaryFailure_AreExactFatalPassFailuresWithoutAssetDisposition()
    {
        foreach (var failure in new Exception[]
        {
            new OperationCanceledException("foreign batch cancellation"),
            new InvalidOperationException("ordinary batch failure")
        })
        {
            var fixture = new Change11ExecutorProbe(new Change11Scenario
            {
                Eligible = 1,
                Batch = (cursor, size, call, token) => Task.FromException<List<AssetRecord>>(failure)
            });
            var reporter = new Change11Reporter((processingEvent, token) =>
            {
                fixture.RecordEvent(processingEvent);
                return ValueTask.CompletedTask;
            });
            var request = Request();

            var result = await fixture.Executor.ExecuteAsync(request, reporter, CancellationToken.None).WaitAsync(Bound);

            Assert.AreSame(request, result.Request);
            Assert.AreEqual(ProcessingRunOutcome.Failed, result.Outcome);
            Assert.AreEqual(failure.Message, result.FailureMessage);
            AssertCounts(result, 0, 0, 0, 0);
            Assert.AreEqual(1, fixture.BatchCalls);
            Assert.AreEqual(0, fixture.ResolverSessions.Count);
            Assert.AreEqual(0, fixture.Writes.Count);
            Assert.AreEqual(0, fixture.SkippedWrites.Count);
            Assert.AreEqual(0, reporter.Events.OfType<ProgressChanged>().Count());
            Assert.AreEqual(0, reporter.Events.OfType<LogEmitted>().Count());
            var fatal = fixture.Logger.Entries.Single(entry => entry.Message == "Fatal error during processing run");
            Assert.AreSame(failure, fatal.Exception);
            var terminal = reporter.Events.OfType<RunFinished>().Single();
            Assert.AreSame(request, terminal.Request);
            Assert.AreSame(result, terminal.Result);
            Assert.IsInstanceOfType<RunFinished>(reporter.Events.Last());
            Assert.AreEqual(3, reporter.Events.Count);
            Assert.AreEqual("event:Finished", fixture.Operations.Last());
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_CancellationAfterWriteEnd_CommitsUpdatedThenReturnsExactCancelledTerminal()
    {
        using var cancellation = new CancellationTokenSource();
        var asset = Asset(1);
        var writeEntered = Signal();
        var releaseWrite = Signal();
        var fixture = OneAssetFixture(
            asset,
            write: async (assetId, geo, token) =>
            {
                writeEntered.TrySetResult();
                await releaseWrite.Task.WaitAsync(Bound, token).ConfigureAwait(false);
                cancellation.Cancel();
            });
        var reporter = new Change11Reporter((processingEvent, token) =>
        {
            fixture.RecordEvent(processingEvent);
            return ValueTask.CompletedTask;
        });
        var request = Request();
        var execution = fixture.Executor.ExecuteAsync(request, reporter, cancellation.Token);
        await writeEntered.Task.WaitAsync(Bound);
        releaseWrite.TrySetResult();
        var result = await execution.WaitAsync(Bound);

        Assert.AreSame(request, result.Request);
        Assert.AreEqual(ProcessingRunOutcome.Cancelled, result.Outcome);
        AssertCounts(result, 1, 1, 0, 0);
        Assert.AreEqual((asset.Id, new GeoResult("Country", "State", "City")), fixture.Writes.Single());
        var operations = fixture.Operations.ToArray();
        var writeEnd = IndexOfExact(operations, $"write-end:{asset.Id}");
        var updated = IndexOfExact(operations, $"event:Disposition:{asset.Id}:Updated");
        var finished = IndexOfExact(operations, "event:Finished");
        Assert.IsTrue(writeEnd < updated && updated < finished);
        Assert.AreEqual(operations.Length - 1, finished);
        Assert.AreEqual(1, operations.Count(item => item == $"event:Disposition:{asset.Id}:Updated"));
        Assert.AreEqual(1, reporter.Events.OfType<ProgressChanged>().Count());
        Assert.AreEqual(1, reporter.Events.OfType<RunFinished>().Count());
        Assert.AreSame(result, reporter.Events.OfType<RunFinished>().Single().Result);
        Assert.IsTrue(reporter.Events.All(item => ReferenceEquals(request, item.Request)));
    }

    [TestMethod]
    public async Task ExecuteAsync_TwoAssetsReverseCompletion_CorrelatesOneDispositionEachAndOneFinalTerminal()
    {
        var first = Asset(1);
        var second = Asset(2);
        var firstEntered = Signal();
        var secondEntered = Signal();
        var releaseFirst = Signal();
        var releaseSecond = Signal();
        var secondDisposition = Signal();
        var fixture = TwoAssetFixture(
            first,
            second,
            maxParallelism: 2,
            verbose: false,
            async (asset, session, token) =>
            {
                var entered = asset.Id == first.Id ? firstEntered : secondEntered;
                var release = asset.Id == first.Id ? releaseFirst : releaseSecond;
                entered.TrySetResult();
                await release.Task.WaitAsync(Bound, token).ConfigureAwait(false);
                return Resolution(new GeoResult("Country", "State", asset.Id == first.Id ? "First" : "Second"));
            });
        var reporter = new Change11Reporter((processingEvent, token) =>
        {
            fixture.RecordEvent(processingEvent);
            if (fixture.Operations.LastOrDefault() == $"event:Disposition:{second.Id}:Updated")
            {
                secondDisposition.TrySetResult();
            }

            return ValueTask.CompletedTask;
        });
        var request = Request();
        var execution = fixture.Executor.ExecuteAsync(request, reporter, CancellationToken.None);
        await firstEntered.Task.WaitAsync(Bound);
        await secondEntered.Task.WaitAsync(Bound);
        releaseSecond.TrySetResult();
        await secondDisposition.Task.WaitAsync(Bound);
        releaseFirst.TrySetResult();
        var result = await execution.WaitAsync(Bound);

        Assert.AreEqual(ProcessingRunOutcome.Completed, result.Outcome);
        AssertCounts(result, 2, 2, 0, 0);
        CollectionAssert.AreEqual(new[] { second.Id, first.Id }, fixture.Writes.Select(item => item.AssetId).ToArray());
        var dispositions = fixture.Operations.Where(item => item.StartsWith("event:Disposition:", StringComparison.Ordinal)).ToArray();
        CollectionAssert.AreEqual(
            new[]
            {
                $"event:Disposition:{second.Id}:Updated",
                $"event:Disposition:{first.Id}:Updated"
            },
            dispositions);
        Assert.AreEqual(2, dispositions.Distinct(StringComparer.Ordinal).Count());
        Assert.AreEqual(2, reporter.Events.OfType<ProgressChanged>().Count());
        Assert.AreEqual(1, reporter.Events.OfType<RunFinished>().Count());
        Assert.AreSame(result, reporter.Events.OfType<RunFinished>().Single().Result);
        Assert.AreSame(request, result.Request);
        Assert.IsTrue(reporter.Events.All(item => ReferenceEquals(request, item.Request)));
        Assert.AreEqual("event:Finished", fixture.Operations.Last());
    }

    [TestMethod]
    public async Task ExecuteAsync_SettingsProviderMutationAfterCapturedSnapshot_RetainsExactRunPolicyAcrossEveryPage()
    {
        var first = Asset(1);
        var second = Asset(2);
        var third = Asset(3);
        var initial = new AppConfig
        {
            Processing = new ProcessingConfig
            {
                BatchSize = 7,
                BatchDelayMs = 11,
                MaxDegreeOfParallelism = 0,
                UseAirportInfrastructure = true,
                UseGadmAdministrativeAreas = true,
                PreferGadmAdministrativeAreas = true,
                VerboseLogging = true
            }
        };
        var provider = new Change11GatedSettingsProvider(initial);
        var firstResolverEntered = Signal();
        var releaseFirstResolver = Signal();
        var secondResolverEntered = Signal();
        var fixture = new Change11ExecutorProbe(new Change11Scenario
        {
            Eligible = 3,
            Configuration = provider,
            Pages = [new[] { first, second }, new[] { third }, Array.Empty<AssetRecord>()],
            Resolve = async (asset, session, token) =>
            {
                if (asset.Id == first.Id)
                {
                    firstResolverEntered.TrySetResult();
                    await releaseFirstResolver.Task.WaitAsync(Bound, token).ConfigureAwait(false);
                }
                else if (asset.Id == second.Id)
                {
                    secondResolverEntered.TrySetResult();
                }

                return Resolution(new GeoResult("Country", "State", $"City-{asset.Latitude}"));
            },
            Infrastructure = (asset, token) => Task.FromResult(EmptyDiagnostics())
        });
        var reporter = new Change11Reporter();
        var request = Request();
        var execution = fixture.Executor.ExecuteAsync(request, reporter, CancellationToken.None);
        var captured = await provider.SnapshotCaptured.Task.WaitAsync(Bound);

        provider.Update(processing =>
        {
            processing.BatchSize = 99;
            processing.BatchDelayMs = 999;
            processing.MaxDegreeOfParallelism = 32;
            processing.UseAirportInfrastructure = false;
            processing.UseGadmAdministrativeAreas = false;
            processing.PreferGadmAdministrativeAreas = false;
            processing.VerboseLogging = false;
        });
        provider.ReleaseSnapshot.TrySetResult();
        await firstResolverEntered.Task.WaitAsync(Bound);
        Assert.IsFalse(secondResolverEntered.Task.IsCompleted, "Configured zero must clamp to one active asset.");
        releaseFirstResolver.TrySetResult();
        await secondResolverEntered.Task.WaitAsync(Bound);
        var result = await execution.WaitAsync(Bound);

        Assert.AreEqual(ProcessingRunOutcome.Completed, result.Outcome);
        AssertCounts(result, 3, 3, 0, 0);
        Assert.AreEqual(1, provider.Calls);
        Assert.AreEqual(0, fixture.ConfigCalls);
        Assert.AreEqual(1, fixture.SkippedSnapshotCalls);
        CollectionAssert.AreEqual(new[] { 7, 7, 7 }, fixture.BatchSizes.ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                AssetCursor.Initial,
                new AssetCursor(second.CreatedAt, second.Id),
                new AssetCursor(third.CreatedAt, third.Id)
            },
            fixture.Cursors.ToArray());
        Assert.AreEqual(3, fixture.ResolverConfigs.Count);
        Assert.IsTrue(fixture.ResolverConfigs.All(item => ReferenceEquals(captured.Processing, item)));
        Assert.IsTrue(fixture.ResolverConfigs.All(item => item.BatchSize == 7
            && item.MaxDegreeOfParallelism == 0
            && item.UseGadmAdministrativeAreas
            && item.PreferGadmAdministrativeAreas
            && item.UseAirportInfrastructure
            && item.VerboseLogging));
        Assert.AreEqual(3, fixture.InfrastructureCalls);
        Assert.AreEqual(3, reporter.Events.OfType<LogEmitted>().Count(log => log.Level == ProcessingLogLevel.Trace));
        CollectionAssert.AreEqual(
            new[] { TimeSpan.FromMilliseconds(11), TimeSpan.FromMilliseconds(11) },
            fixture.Delays.ToArray());
        var operations = fixture.Operations.ToArray();
        Assert.IsTrue(IndexOf(operations, "delay:11", 0) < IndexOfPrefix(operations, "batch:", 1));
        Assert.IsTrue(IndexOf(operations, "delay:11", 1) < IndexOfPrefix(operations, "batch:", 2));
        Assert.IsTrue(IndexOfPrefix(operations, "batch:", 2) > IndexOf(operations, "delay:11", 1));
        Assert.IsTrue(reporter.Events.All(item => ReferenceEquals(request, item.Request)));
    }

    [TestMethod]
    [DataRow(0, 1)]
    [DataRow(99, 32)]
    public async Task ExecuteAsync_ParallelismClamp_UsesExactLowerAndUpperBoundary(int configured, int expected)
    {
        var assets = Enumerable.Range(1, expected + 1).Select(Asset).ToArray();
        var entered = assets.Select(_ => Signal()).ToArray();
        var releases = assets.Select(_ => Signal()).ToArray();
        var active = 0;
        var maximum = 0;
        var config = DefaultConfig();
        config.Processing.MaxDegreeOfParallelism = configured;
        var fixture = new Change11ExecutorProbe(new Change11Scenario
        {
            Eligible = assets.Length,
            Config = config,
            Pages = [assets, Array.Empty<AssetRecord>()],
            Resolve = async (asset, session, token) =>
            {
                var index = (int)asset.Latitude - 1;
                var now = Interlocked.Increment(ref active);
                SetMaximum(ref maximum, now);
                entered[index].TrySetResult();
                await releases[index].Task.WaitAsync(Bound, token).ConfigureAwait(false);
                Interlocked.Decrement(ref active);
                return Resolution(new GeoResult("Country", "State", $"City-{index}"));
            }
        });
        var execution = fixture.Executor.ExecuteAsync(Request(), new Change11Reporter(), CancellationToken.None);

        for (var index = 0; index < expected; index++)
        {
            await entered[index].Task.WaitAsync(Bound);
        }

        Assert.IsFalse(entered[expected].Task.IsCompleted);
        releases[0].TrySetResult();
        await entered[expected].Task.WaitAsync(Bound);
        for (var index = 1; index < releases.Length; index++)
        {
            releases[index].TrySetResult();
        }

        var result = await execution.WaitAsync(Bound);
        Assert.AreEqual(expected, maximum);
        AssertCounts(result, assets.Length, assets.Length, 0, 0);
        Assert.AreEqual(assets.Length, fixture.Writes.Count);
    }

    [TestMethod]
    public async Task ExecuteAsync_ConcurrentIndependentRuns_ShareNoMutableInvocationStateOrEvents()
    {
        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();
        var firstAsset = Asset(1);
        var secondAsset = Asset(2);
        var fixture = new Change11ConcurrentRunProbe(
            (firstCancellation.Token, firstAsset),
            (secondCancellation.Token, secondAsset));
        var firstReporter = new Change11Reporter();
        var secondReporter = new Change11Reporter();
        var firstRequest = Request();
        var secondRequest = Request();

        var firstRun = fixture.Executor.ExecuteAsync(firstRequest, firstReporter, firstCancellation.Token);
        var secondRun = fixture.Executor.ExecuteAsync(secondRequest, secondReporter, secondCancellation.Token);
        await fixture.FirstEntered.Task.WaitAsync(Bound);
        await fixture.SecondEntered.Task.WaitAsync(Bound);
        fixture.SecondRelease.TrySetResult();
        await fixture.SecondWritten.Task.WaitAsync(Bound);
        fixture.FirstRelease.TrySetResult();
        var results = await Task.WhenAll(firstRun, secondRun).WaitAsync(Bound);

        AssertCounts(results.Single(item => ReferenceEquals(item.Request, firstRequest)), 1, 1, 0, 0);
        AssertCounts(results.Single(item => ReferenceEquals(item.Request, secondRequest)), 1, 1, 0, 0);
        Assert.IsTrue(firstReporter.Events.All(item => ReferenceEquals(firstRequest, item.Request)));
        Assert.IsTrue(secondReporter.Events.All(item => ReferenceEquals(secondRequest, item.Request)));
        Assert.AreEqual(0, firstReporter.Events.Count(item => ReferenceEquals(secondRequest, item.Request)));
        Assert.AreEqual(0, secondReporter.Events.Count(item => ReferenceEquals(firstRequest, item.Request)));
        CollectionAssert.AreEquivalent(new[] { firstAsset.Id, secondAsset.Id }, fixture.Writes.ToArray());
    }

    [TestMethod]
    public void WithFallbackCity_MakesLoggerOnlyNoCityExecutorBranchUnreachableForEveryHasMatchShape()
    {
        var inputs = new[]
        {
            new GeoResult(string.Empty, null, null),
            new GeoResult("Country", null, null),
            new GeoResult("Country", "State", null),
            new GeoResult("Country", null, "City"),
            new GeoResult("Country", "State", "City")
        };
        var compatibilityGuardDispositions = 0;

        foreach (var input in inputs)
        {
            Assert.IsTrue(input.HasMatch);
            var afterFallback = input.WithFallbackCity();
            Assert.IsNotNull(afterFallback.City);
            if (ProcessingRunExecutor.IsLoggerOnlyNoCitySkip(afterFallback))
            {
                compatibilityGuardDispositions++;
            }
        }

        Assert.AreEqual(0, compatibilityGuardDispositions);
        Assert.IsFalse(new GeoResult(null, null, null).HasMatch);
        Assert.IsFalse(new GeoResult(null, "State", "City").HasMatch);
    }

    [TestMethod]
    public void ProcessingRunExecutor_LifetimeSurface_ExcludesControlPlaneUiAndMutableInvocationState()
    {
        var forbiddenTypes = new[]
        {
            typeof(ProcessingState),
            typeof(ProcessingBackgroundService),
            typeof(IHostedService),
            typeof(CancellationTokenSource),
            typeof(ProcessingRunRequest),
            typeof(IProcessingRunEventSession),
            typeof(AssetCursor),
            typeof(AppConfig),
            typeof(HashSet<Guid>)
        };
        var fields = typeof(ProcessingRunExecutor).GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
        var constructorTypes = typeof(ProcessingRunExecutor)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType);
        var surfaceTypes = fields.Select(field => field.FieldType).Concat(constructorTypes).ToArray();

        Assert.IsTrue(fields.All(field => field.IsInitOnly), "Every executor field must be readonly collaborator state.");
        Assert.IsFalse(surfaceTypes.Any(type => forbiddenTypes.Any(forbidden => forbidden.IsAssignableFrom(type))));
        Assert.IsFalse(surfaceTypes.Any(type =>
            (type.Namespace?.Contains("Components", StringComparison.Ordinal) ?? false)
            || type.Name.Contains("Blazor", StringComparison.OrdinalIgnoreCase)
            || type.Name.Contains("Cron", StringComparison.OrdinalIgnoreCase)
            || type.Name.Contains("Schedule", StringComparison.OrdinalIgnoreCase)
            || type.Name.Contains("ProcessingBackgroundService", StringComparison.Ordinal)
            || type.Name.Contains("ProcessingState", StringComparison.Ordinal)
            || type.Name.Contains("CancellationTokenSource", StringComparison.Ordinal)));
        Assert.IsFalse(fields.Any(field =>
            field.Name.Contains("count", StringComparison.OrdinalIgnoreCase)
            || field.Name.Contains("cursor", StringComparison.OrdinalIgnoreCase)
            || field.Name.Contains("session", StringComparison.OrdinalIgnoreCase)
            || field.Name.Contains("request", StringComparison.OrdinalIgnoreCase)
            || field.Name.Contains("result", StringComparison.OrdinalIgnoreCase)
            || field.Name.Contains("configSnapshot", StringComparison.OrdinalIgnoreCase)
            || field.Name.Contains("skippedIds", StringComparison.OrdinalIgnoreCase)));
    }

    private static Change11ExecutorProbe ConfigureAdmissionScenario(AssetRecord asset, string admission)
    {
        var config = DefaultConfig();
        var plan = admission switch
        {
            "eligibility" => new Change11Scenario(),
            "skipped-information" => new Change11Scenario
            {
                Eligible = 1,
                SkippedIds = new HashSet<Guid> { asset.Id },
                Pages = [Array.Empty<AssetRecord>()]
            },
            "batch-information" => new Change11Scenario { Eligible = 1, Pages = [new[] { asset }, Array.Empty<AssetRecord>()] },
            "warning" => new Change11Scenario
            {
                Eligible = 1,
                Pages = [new[] { asset }, Array.Empty<AssetRecord>()],
                Resolve = (current, session, token) => Task.FromResult<AdministrativeAreaResolution?>(null)
            },
            "trace" => new Change11Scenario
            {
                Eligible = 1,
                Config = VerboseConfig(),
                Pages = [new[] { asset }, Array.Empty<AssetRecord>()]
            },
            "error" => new Change11Scenario
            {
                Eligible = 1,
                Pages = [new[] { asset }, Array.Empty<AssetRecord>()],
                Resolve = (current, session, token) => throw new InvalidOperationException("domain failed")
            },
            _ => throw new AssertFailedException($"Unknown admission {admission}")
        };
        return new Change11ExecutorProbe(plan);
    }

    private static bool IsTargetAdmission(ProcessingEvent processingEvent, string admission)
    {
        return admission switch
        {
            "eligibility" => processingEvent is EligibilityDetermined,
            "skipped-information" => processingEvent is LogEmitted { Level: ProcessingLogLevel.Information, Message: var message }
                && message.StartsWith("Skipping ", StringComparison.Ordinal),
            "batch-information" => processingEvent is LogEmitted { Level: ProcessingLogLevel.Information, Message: var message }
                && message.StartsWith("Batch ", StringComparison.Ordinal),
            "warning" => processingEvent is LogEmitted { Level: ProcessingLogLevel.Warning },
            "trace" => processingEvent is LogEmitted { Level: ProcessingLogLevel.Trace },
            "error" => processingEvent is LogEmitted { Level: ProcessingLogLevel.Error },
            _ => false
        };
    }

    private static async Task AssertMatchedFallbackWriteAsync(GeoResult resolved, GeoResult expectedWrite)
    {
        var asset = Asset(1);
        var fixture = OneAssetFixture(
            asset,
            resolve: (current, session, token) => Task.FromResult<AdministrativeAreaResolution?>(Resolution(resolved)));
        var reporter = new Change11Reporter((processingEvent, token) =>
        {
            fixture.RecordEvent(processingEvent);
            return ValueTask.CompletedTask;
        });
        var request = Request();

        var result = await fixture.Executor.ExecuteAsync(request, reporter, CancellationToken.None).WaitAsync(Bound);

        Assert.AreSame(request, result.Request);
        Assert.AreEqual(ProcessingRunOutcome.Completed, result.Outcome);
        AssertCounts(result, 1, 1, 0, 0);
        Assert.AreEqual((asset.Id, expectedWrite), fixture.Writes.Single());
        Assert.AreEqual(0, fixture.SkippedWrites.Count);
        Assert.AreEqual(1, fixture.ResolverSessions.Count);
        Assert.AreSame(request, fixture.ResolverSessions.Single().Request);
        Assert.AreEqual(0, fixture.InfrastructureCalls);
        Assert.AreEqual(2, fixture.BatchCalls);

        var progress = reporter.Events.OfType<ProgressChanged>().Single().Progress;
        Assert.AreEqual(1L, progress.ProcessedCount);
        Assert.AreEqual(1L, progress.UpdatedCount);
        Assert.AreEqual(0L, progress.SkippedCount);
        Assert.AreEqual(0L, progress.FailedCount);
        Assert.AreEqual(0, reporter.Events.OfType<LogEmitted>().Count(item => item.Level == ProcessingLogLevel.Warning));
        Assert.IsFalse(fixture.Logger.Entries.Any(item => item.Level == LogLevel.Warning
            || item.Message.Contains("resolved but no city", StringComparison.Ordinal)));
        var debug = fixture.Logger.Entries.Single();
        Assert.AreEqual(LogLevel.Debug, debug.Level);
        Assert.AreEqual($"Asset {asset.Id}: {expectedWrite.City}, {expectedWrite.State ?? "(null)"}, {expectedWrite.Country}", debug.Message);

        CollectionAssert.AreEqual(
            new[]
            {
                typeof(RunStarted),
                typeof(EligibilityDetermined),
                typeof(LogEmitted),
                typeof(ProgressChanged),
                typeof(RunFinished)
            },
            reporter.Events.Select(item => item.GetType()).ToArray());
        Assert.AreEqual(
            $"Batch 1: fetched 1 assets (total processed so far: 0).",
            reporter.Events.OfType<LogEmitted>().Single().Message);
        Assert.IsTrue(reporter.Events.All(item => ReferenceEquals(request, item.Request)));
        Assert.AreSame(result, reporter.Events.OfType<RunFinished>().Single().Result);

        CollectionAssert.AreEqual(
            new[]
            {
                "event:Started",
                "count",
                "event:Eligibility",
                "skipped-snapshot",
                "config",
                $"batch:1:{AssetCursor.Initial.CreatedAt:O}:{AssetCursor.Initial.Id}:50",
                "event:Information:Batch 1: fetched 1 assets (total processed so far: 0).",
                $"admin:{asset.Id}",
                $"write-start:{asset.Id}",
                $"write-end:{asset.Id}",
                $"event:Disposition:{asset.Id}:Updated",
                $"batch:2:{asset.CreatedAt:O}:{asset.Id}:50",
                "event:Finished"
            },
            fixture.Operations.ToArray());
        Assert.AreEqual(0, fixture.Operations.Count(item => item.Contains(":Skipped", StringComparison.Ordinal)));
    }

    private static Change11ExecutorProbe OneAssetFixture(
        AssetRecord asset,
        Func<AssetRecord, IProcessingRunEventSession, CancellationToken, Task<AdministrativeAreaResolution?>>? resolve = null,
        Func<Guid, Task>? addSkipped = null,
        Func<Guid, GeoResult, CancellationToken, Task>? write = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        AppConfig? config = null)
    {
        return new Change11ExecutorProbe(new Change11Scenario
        {
            Eligible = 1,
            Config = config ?? DefaultConfig(),
            Pages = [new[] { asset }, Array.Empty<AssetRecord>()],
            Resolve = resolve,
            AddSkipped = addSkipped,
            Write = write,
            Delay = delay
        });
    }

    private static Change11ExecutorProbe TwoAssetFixture(
        AssetRecord first,
        AssetRecord second,
        int maxParallelism,
        bool verbose,
        Func<AssetRecord, IProcessingRunEventSession, CancellationToken, Task<AdministrativeAreaResolution?>> resolve)
    {
        var config = DefaultConfig();
        config.Processing.MaxDegreeOfParallelism = maxParallelism;
        config.Processing.VerboseLogging = verbose;
        return new Change11ExecutorProbe(new Change11Scenario
        {
            Eligible = 2,
            Config = config,
            Pages = [new[] { first, second }, Array.Empty<AssetRecord>()],
            Resolve = resolve
        });
    }

    private static AppConfig VerboseConfig()
    {
        var config = DefaultConfig();
        config.Processing.VerboseLogging = true;
        return config;
    }

    private static AppConfig DefaultConfig() => new()
    {
        Processing = new ProcessingConfig
        {
            BatchSize = 50,
            BatchDelayMs = 0,
            MaxDegreeOfParallelism = 1,
            UseAirportInfrastructure = false
        }
    };

    private static ProcessingRunRequest Request() => new(Guid.NewGuid(), ProcessingRunTrigger.Manual);
    private static AssetRecord Asset(int index) => new(Guid.NewGuid(), index, index, DateTime.UnixEpoch.AddSeconds(index));
    private static AdministrativeAreaResolution Resolution(GeoResult geo) => new("USA", "US", "Country", geo, null, null);
    private static OvertureInfrastructureLookupDiagnostics EmptyDiagnostics() => new(null, [], "release");
    private static OvertureInfrastructureLookupDiagnostics Diagnostics(string name, bool contains) => new(
        new OvertureInfrastructureResult("id", name, null, null, null, 1, contains, contains, []),
        [],
        "release");

    private static string EventOperation(ProcessingEvent processingEvent)
    {
        return processingEvent switch
        {
            RunStarted => "event:Started",
            EligibilityDetermined => "event:Eligibility",
            LogEmitted log => $"event:{log.Level}:{log.Message}",
            ProgressChanged progress => $"event:Progress:{progress.Progress.UpdatedCount}/{progress.Progress.SkippedCount}/{progress.Progress.FailedCount}",
            ActivityStarted activity => $"event:ActivityStarted:{activity.ActivityId}",
            ActivityEnded activity => $"event:ActivityEnded:{activity.ActivityId}",
            RunFinished => "event:Finished",
            _ => $"event:{processingEvent.GetType().Name}"
        };
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void AssertCounts(ProcessingRunResult result, long processed, long updated, long skipped, long failed)
    {
        Assert.AreEqual(processed, result.ProcessedCount);
        Assert.AreEqual(updated, result.UpdatedCount);
        Assert.AreEqual(skipped, result.SkippedCount);
        Assert.AreEqual(failed, result.FailedCount);
    }

    private static void SetMaximum(ref int maximum, int candidate)
    {
        var observed = Volatile.Read(ref maximum);
        while (candidate > observed)
        {
            var prior = Interlocked.CompareExchange(ref maximum, candidate, observed);
            if (prior == observed)
            {
                return;
            }

            observed = prior;
        }
    }

    private static int IndexOfExact(string[] values, string value)
    {
        return Array.IndexOf(values, value);
    }

    private static int IndexOf(string[] values, string value, int occurrence)
    {
        return values.Select((item, index) => (item, index)).Where(pair => pair.item == value).ElementAt(occurrence).index;
    }

    private static int IndexOfPrefix(string[] values, string prefix, int occurrence)
    {
        return values.Select((item, index) => (item, index)).Where(pair => pair.item.StartsWith(prefix, StringComparison.Ordinal)).ElementAt(occurrence).index;
    }

    private sealed class TestSinkException(string message) : Exception(message);

    private sealed class Change11Reporter(
        Func<ProcessingEvent, CancellationToken, ValueTask>? behavior = null) : ProcessingEventReporter
    {
        public ConcurrentQueue<ProcessingEvent> Attempts { get; } = new();
        public ConcurrentQueue<ProcessingEvent> Events { get; } = new();

        protected override async ValueTask AcceptAsync(ProcessingEvent processingEvent, CancellationToken cancellationToken)
        {
            Attempts.Enqueue(processingEvent);
            if (behavior is not null)
            {
                await behavior(processingEvent, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Events.Enqueue(processingEvent);
        }
    }

    private sealed class ThrowingConfiguration(Exception failure) : IProcessingRunConfiguration
    {
        public int Calls;

        public Task<AppConfig> GetConfigAsync()
        {
            Interlocked.Increment(ref Calls);
            return Task.FromException<AppConfig>(failure);
        }
    }

    private sealed class Change11GatedSettingsProvider : IProcessingRunConfiguration
    {
        private readonly object _sync = new();
        private readonly AppConfig _backing;
        public TaskCompletionSource<AppConfig> SnapshotCaptured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSnapshot { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;

        public Change11GatedSettingsProvider(AppConfig initial)
        {
            _backing = CloneConfig(initial);
        }

        public async Task<AppConfig> GetConfigAsync()
        {
            Interlocked.Increment(ref Calls);
            AppConfig captured;
            lock (_sync)
            {
                captured = CloneConfig(_backing);
            }

            SnapshotCaptured.TrySetResult(captured);
            await ReleaseSnapshot.Task.WaitAsync(Bound).ConfigureAwait(false);
            return captured;
        }

        public void Update(Action<ProcessingConfig> update)
        {
            lock (_sync)
            {
                update(_backing.Processing);
            }
        }

        private static AppConfig CloneConfig(AppConfig source)
        {
            return new AppConfig
            {
                Processing = new ProcessingConfig
                {
                    BatchSize = source.Processing.BatchSize,
                    BatchDelayMs = source.Processing.BatchDelayMs,
                    MaxDegreeOfParallelism = source.Processing.MaxDegreeOfParallelism,
                    UseAirportInfrastructure = source.Processing.UseAirportInfrastructure,
                    UseGadmAdministrativeAreas = source.Processing.UseGadmAdministrativeAreas,
                    PreferGadmAdministrativeAreas = source.Processing.PreferGadmAdministrativeAreas,
                    UseGadmTerritoryFallbacks = source.Processing.UseGadmTerritoryFallbacks,
                    VerboseLogging = source.Processing.VerboseLogging,
                    CityResolver = source.Processing.CityResolver
                },
                Schedule = new ScheduleConfig
                {
                    Cron = source.Schedule.Cron,
                    Enabled = source.Schedule.Enabled
                }
            };
        }
    }

    private sealed class Change11CaptureLogger : ILogger
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), exception, state));
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception, object? State);

    private sealed class Change11TimeProvider : TimeProvider
    {
        private long _read;
        public override DateTimeOffset GetUtcNow() => Epoch.AddSeconds(Interlocked.Increment(ref _read));
    }

    private sealed record Change11Scenario
    {
        public long Eligible { get; init; }
        public AppConfig Config { get; init; } = DefaultConfig();
        public IProcessingRunConfiguration? Configuration { get; init; }
        public IReadOnlySet<Guid> SkippedIds { get; init; } = new HashSet<Guid>();
        public IReadOnlyList<IReadOnlyList<AssetRecord>> Pages { get; init; } = [];
        public Func<AssetCursor, int, int, CancellationToken, Task<List<AssetRecord>>>? Batch { get; init; }
        public Func<AssetRecord, IProcessingRunEventSession, CancellationToken, Task<AdministrativeAreaResolution?>>? Resolve { get; init; }
        public Func<AssetRecord, CancellationToken, Task<OvertureInfrastructureLookupDiagnostics>>? Infrastructure { get; init; }
        public Func<Guid, Task>? AddSkipped { get; init; }
        public Func<Guid, GeoResult, CancellationToken, Task>? Write { get; init; }
        public Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }
    }

    private sealed class Change11ExecutorProbe :
        IProcessingRunConfiguration,
        IProcessingAssetRepository,
        IProcessingSkippedStore,
        IProcessingAdministrativeResolver,
        IProcessingInfrastructureLookup,
        IProcessingRunDelay
    {
        private readonly Change11Scenario _plan;
        private readonly ConcurrentQueue<List<AssetRecord>> _pages = new();
        private readonly ConcurrentDictionary<double, AssetRecord> _activeAssets = new();
        private readonly ConcurrentQueue<(Guid AssetId, string Disposition)> _pendingDispositions = new();

        public Change11ExecutorProbe(Change11Scenario? plan = null)
        {
            _plan = plan ?? new Change11Scenario();
            foreach (var page in _plan.Pages)
            {
                _pages.Enqueue([.. page]);
            }
        }

        public ConcurrentQueue<string> Operations { get; } = new();
        public ConcurrentQueue<AssetCursor> Cursors { get; } = new();
        public ConcurrentQueue<int> BatchSizes { get; } = new();
        public ConcurrentQueue<TimeSpan> Delays { get; } = new();
        public ConcurrentQueue<(Guid AssetId, GeoResult Geo)> Writes { get; } = new();
        public ConcurrentQueue<Guid> SkippedWrites { get; } = new();
        public ConcurrentQueue<ProcessingConfig> ResolverConfigs { get; } = new();
        public ConcurrentQueue<IProcessingRunEventSession> ResolverSessions { get; } = new();
        public Change11CaptureLogger Logger { get; } = new();
        public Exception? ForeignFailure { get; private set; }
        public Exception? HandledFailure { get; private set; }
        public int ConfigCalls;
        public int SkippedSnapshotCalls;
        public int BatchCalls;
        public int InfrastructureCalls;

        public void RecordEvent(ProcessingEvent processingEvent)
        {
            if (processingEvent is ProgressChanged)
            {
                Assert.IsTrue(_pendingDispositions.TryDequeue(out var pending), "Every accepted disposition must correlate to one completed asset operation.");
                Operations.Enqueue($"event:Disposition:{pending.AssetId}:{pending.Disposition}");
                return;
            }

            Operations.Enqueue(EventOperation(processingEvent));
        }

        public ProcessingRunExecutor Executor => new(
            Logger,
            _plan.Configuration ?? this,
            this,
            this,
            this,
            this,
            this,
            new Change11TimeProvider());

        public Task<long> GetUnprocessedCountAsync(CancellationToken cancellationToken = default)
        {
            Operations.Enqueue("count");
            return Task.FromResult(_plan.Eligible);
        }

        public Task<AppConfig> GetConfigAsync()
        {
            Interlocked.Increment(ref ConfigCalls);
            Operations.Enqueue("config");
            return Task.FromResult(_plan.Config);
        }

        public Task<HashSet<Guid>> GetAllAsync()
        {
            Interlocked.Increment(ref SkippedSnapshotCalls);
            Operations.Enqueue("skipped-snapshot");
            return Task.FromResult(_plan.SkippedIds.ToHashSet());
        }

        public async Task<List<AssetRecord>> GetUnprocessedBatchAsync(AssetCursor cursor, int batchSize, CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref BatchCalls);
            Cursors.Enqueue(cursor);
            BatchSizes.Enqueue(batchSize);
            Operations.Enqueue($"batch:{call}:{cursor.CreatedAt:O}:{cursor.Id}:{batchSize}");
            var page = _plan.Batch is null
                ? (_pages.TryDequeue(out var queued) ? queued : [])
                : await _plan.Batch(cursor, batchSize, call, cancellationToken).ConfigureAwait(false);
            foreach (var asset in page)
            {
                _activeAssets[asset.Latitude] = asset;
            }

            return page;
        }

        public async Task<AdministrativeAreaResolution?> ResolveAsync(double latitude, double longitude, ProcessingConfig config, IProcessingRunEventSession session, CancellationToken cancellationToken = default)
        {
            var asset = _activeAssets[latitude];
            ResolverConfigs.Enqueue(config);
            ResolverSessions.Enqueue(session);
            Operations.Enqueue($"admin:{asset.Id}");
            try
            {
                return _plan.Resolve is null
                    ? Resolution(new GeoResult("Country", "State", "City"))
                    : await _plan.Resolve(asset, session, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                ForeignFailure = ex;
                HandledFailure = ex;
                _pendingDispositions.Enqueue((asset.Id, "Failed"));
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && ex is not ProcessingEventReportingException)
            {
                HandledFailure = ex;
                _pendingDispositions.Enqueue((asset.Id, "Failed"));
                throw;
            }
        }

        public Task<OvertureInfrastructureLookupDiagnostics> FindNearestInfrastructureAsync(double latitude, double longitude, string? iso3, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref InfrastructureCalls);
            var asset = _activeAssets[latitude];
            Operations.Enqueue($"airport:{asset.Id}");
            return _plan.Infrastructure?.Invoke(asset, cancellationToken) ?? Task.FromResult(EmptyDiagnostics());
        }

        public async Task AddAsync(Guid assetId)
        {
            Operations.Enqueue($"skip-start:{assetId}");
            try
            {
                if (_plan.AddSkipped is not null)
                {
                    await _plan.AddSkipped(assetId).ConfigureAwait(false);
                }
            }
            catch
            {
                _pendingDispositions.Enqueue((assetId, "Failed"));
                throw;
            }

            SkippedWrites.Enqueue(assetId);
            Operations.Enqueue($"skip-end:{assetId}");
            _pendingDispositions.Enqueue((assetId, "Skipped"));
        }

        public async Task WriteLocationAsync(Guid assetId, GeoResult geoResult, CancellationToken cancellationToken = default)
        {
            Operations.Enqueue($"write-start:{assetId}");
            try
            {
                if (_plan.Write is not null)
                {
                    await _plan.Write(assetId, geoResult, cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                _pendingDispositions.Enqueue((assetId, "Failed"));
                throw;
            }

            Writes.Enqueue((assetId, geoResult));
            Operations.Enqueue($"write-end:{assetId}");
            _pendingDispositions.Enqueue((assetId, "Updated"));
        }

        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Operations.Enqueue($"delay:{delay.TotalMilliseconds}");
            Delays.Enqueue(delay);
            if (_plan.Delay is not null)
            {
                await _plan.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private sealed class Change11ConcurrentRunProbe :
        IProcessingRunConfiguration,
        IProcessingAssetRepository,
        IProcessingSkippedStore,
        IProcessingAdministrativeResolver,
        IProcessingInfrastructureLookup,
        IProcessingRunDelay
    {
        private readonly ConcurrentDictionary<CancellationToken, AssetRecord> _assets;
        private readonly ConcurrentDictionary<CancellationToken, int> _pages = new();
        private readonly Guid _firstAssetId;
        private readonly Guid _secondAssetId;
        public TaskCompletionSource FirstEntered { get; } = Signal();
        public TaskCompletionSource SecondEntered { get; } = Signal();
        public TaskCompletionSource FirstRelease { get; } = Signal();
        public TaskCompletionSource SecondRelease { get; } = Signal();
        public TaskCompletionSource SecondWritten { get; } = Signal();
        public ConcurrentQueue<Guid> Writes { get; } = new();
        public ProcessingRunExecutor Executor { get; }

        public Change11ConcurrentRunProbe(
            (CancellationToken Token, AssetRecord Asset) first,
            (CancellationToken Token, AssetRecord Asset) second)
        {
            _assets = new ConcurrentDictionary<CancellationToken, AssetRecord>();
            _assets[first.Token] = first.Asset;
            _assets[second.Token] = second.Asset;
            _firstAssetId = first.Asset.Id;
            _secondAssetId = second.Asset.Id;
            Executor = new ProcessingRunExecutor(new Change11CaptureLogger(), this, this, this, this, this, this, new Change11TimeProvider());
        }

        public Task<AppConfig> GetConfigAsync() => Task.FromResult(new AppConfig { Processing = new ProcessingConfig { BatchDelayMs = 0, MaxDegreeOfParallelism = 1 } });
        public Task<HashSet<Guid>> GetAllAsync() => Task.FromResult(new HashSet<Guid>());
        public Task<long> GetUnprocessedCountAsync(CancellationToken cancellationToken = default) => Task.FromResult(1L);
        public Task<List<AssetRecord>> GetUnprocessedBatchAsync(AssetCursor cursor, int batchSize, CancellationToken cancellationToken = default)
        {
            var page = _pages.AddOrUpdate(cancellationToken, 1, (_, current) => current + 1);
            return Task.FromResult(page == 1 ? new List<AssetRecord> { _assets[cancellationToken] } : []);
        }

        public async Task<AdministrativeAreaResolution?> ResolveAsync(double latitude, double longitude, ProcessingConfig config, IProcessingRunEventSession session, CancellationToken cancellationToken = default)
        {
            var first = latitude == 1;
            (first ? FirstEntered : SecondEntered).TrySetResult();
            await (first ? FirstRelease.Task : SecondRelease.Task).WaitAsync(Bound, cancellationToken).ConfigureAwait(false);
            return Resolution(new GeoResult("Country", "State", first ? "First" : "Second"));
        }

        public Task<OvertureInfrastructureLookupDiagnostics> FindNearestInfrastructureAsync(double latitude, double longitude, string? iso3, CancellationToken cancellationToken = default) => Task.FromResult(EmptyDiagnostics());
        public Task AddAsync(Guid assetId) => Task.CompletedTask;
        public Task WriteLocationAsync(Guid assetId, GeoResult geoResult, CancellationToken cancellationToken = default)
        {
            Writes.Enqueue(assetId);
            if (assetId == _secondAssetId)
            {
                SecondWritten.TrySetResult();
            }
            else
            {
                Assert.AreEqual(_firstAssetId, assetId);
            }

            return Task.CompletedTask;
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
