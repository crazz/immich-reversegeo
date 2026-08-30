using System.Collections.Concurrent;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Gadm.Services;
using ImmichReverseGeo.Overture.Services;
using ImmichReverseGeo.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests;

[TestClass]
public sealed class AdministrativeAreaResolverEventReportingTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    [TestMethod]
    [DataRow(false, "Starting Overture administrative cache download for USA.", "Downloading Overture administrative cache for USA...")]
    [DataRow(true, "Starting GADM administrative cache download for USA.", "Downloading GADM administrative cache for USA...")]
    public async Task StartedDownload_ReportsExactInfoActivityAndReadinessAfterWait(bool gadm, string cacheMessage, string label)
    {
        await using var fixture = await ResolverFixture.CreateAsync(gadm ? Source.Gadm : Source.Overture, gated: true);
        var session = await fixture.OpenSessionAsync();
        var resolution = fixture.ResolveAsync(gadm, session);
        await WaitAsync(fixture.SourceEntered.Task);
        var start = await fixture.Signals.WaitForStartAsync(label);
        Assert.AreNotEqual(Guid.Empty, start.ActivityId);
        fixture.ReleaseSource.TrySetResult();
        await WaitAsync(resolution);

        AssertExactEventSequence(fixture.Reporter.EventsFor(session.Request), ExpectedSuccessfulCacheEvents(gadm, cacheMessage));
    }

    [TestMethod]
    [DataRow(false, "Waiting for in-flight Overture administrative cache download for USA.", "Waiting for Overture administrative cache for USA...")]
    [DataRow(true, "Waiting for in-flight GADM administrative cache download for USA.", "Waiting for GADM administrative cache for USA...")]
    public async Task AwaitedExistingDownload_ReportsDistinctWaiterActivity(bool gadm, string cacheMessage, string label)
    {
        await using var fixture = await ResolverFixture.CreateAsync(gadm ? Source.Gadm : Source.Overture, gated: true);
        var owner = fixture.StartOwner(gadm);
        await WaitAsync(fixture.SourceEntered.Task);
        var session = await fixture.OpenSessionAsync();
        var resolution = fixture.ResolveAsync(gadm, session);
        var start = await fixture.Signals.WaitForStartAsync(label);
        Assert.AreNotEqual(Guid.Empty, start.ActivityId);
        fixture.ReleaseSource.TrySetResult();
        await Task.WhenAll(WaitAsync(owner), WaitAsync(resolution));

        AssertExactEventSequence(fixture.Reporter.EventsFor(session.Request), ExpectedSuccessfulCacheEvents(gadm, cacheMessage));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AlreadyReady_ReportsExactInfoWithoutActivity(bool gadm)
    {
        await using var fixture = await ResolverFixture.CreateAsync(gadm ? Source.Gadm : Source.Overture, ready: true);
        var session = await fixture.OpenSessionAsync();
        await WaitAsync(fixture.ResolveAsync(gadm, session));
        AssertInformationLogs(fixture.Reporter.Events, ExpectedAlreadyReadyLogs(gadm));
        Assert.AreEqual(0, fixture.Reporter.Events.OfType<ActivityStarted>().Count());
        Assert.AreEqual(0, fixture.Reporter.Events.OfType<ActivityEnded>().Count());
    }

    [TestMethod]
    public async Task ConcurrentEqualLabelActivities_RemainUntilTheirOwnEnds()
    {
        var reporter = new RecordingProcessingEventReporter();
        var signals = new ActivitySignals(reporter);
        await using var firstFixture = await ResolverFixture.CreateAsync(Source.Overture, gated: true, reporter: reporter, signals: signals);
        await using var secondFixture = await ResolverFixture.CreateAsync(Source.Overture, gated: true, reporter: reporter, signals: signals);
        var firstSession = await firstFixture.OpenSessionAsync();
        var secondSession = await secondFixture.OpenSessionAsync();
        var first = firstFixture.ResolveAsync(false, firstSession);
        var second = secondFixture.ResolveAsync(false, secondSession);
        await Task.WhenAll(WaitAsync(firstFixture.SourceEntered.Task), WaitAsync(secondFixture.SourceEntered.Task));
        var firstStart = reporter.EventsFor(firstSession.Request).OfType<ActivityStarted>().Single();
        var secondStart = reporter.EventsFor(secondSession.Request).OfType<ActivityStarted>().Single();
        Assert.AreNotEqual(firstStart.ActivityId, secondStart.ActivityId);
        firstFixture.ReleaseSource.TrySetResult();
        await signals.WaitForEndAsync(firstStart.ActivityId);
        Assert.AreEqual(0, reporter.EventsFor(secondSession.Request).OfType<ActivityEnded>().Count(x => x.ActivityId == secondStart.ActivityId));
        secondFixture.ReleaseSource.TrySetResult();
        await Task.WhenAll(WaitAsync(first), WaitAsync(second));
        await signals.WaitForEndAsync(secondStart.ActivityId);
        AssertActivityIdsPaired(reporter.EventsFor(firstSession.Request), 1, firstStart.ActivityId);
        AssertActivityIdsPaired(reporter.EventsFor(secondSession.Request), 1, secondStart.ActivityId);
    }

    [TestMethod]
    public async Task ConcurrentCrossSourceActivities_RemainUntilTheirOwnEnds()
    {
        await using var fixture = await ResolverFixture.CreateAsync(Source.Both, gated: true);
        var overtureSession = await fixture.OpenSessionAsync();
        var gadmSession = await fixture.OpenSessionAsync();
        var overture = fixture.ResolveAsync(false, overtureSession);
        var gadm = fixture.ResolveAsync(true, gadmSession);
        await Task.WhenAll(WaitAsync(fixture.OvertureEntered.Task), WaitAsync(fixture.GadmEntered.Task));
        var overtureStart = await fixture.Signals.WaitForStartAsync("Downloading Overture administrative cache for USA...");
        var gadmStart = await fixture.Signals.WaitForStartAsync("Downloading GADM administrative cache for USA...");
        fixture.ReleaseGadm.TrySetResult();
        await fixture.Signals.WaitForEndAsync(gadmStart.ActivityId);
        Assert.AreEqual(0, fixture.Reporter.EventsFor(overtureSession.Request).OfType<ActivityEnded>().Count(activity => activity.ActivityId == gadmStart.ActivityId));
        Assert.AreEqual(0, fixture.Reporter.EventsFor(gadmSession.Request).OfType<ActivityEnded>().Count(activity => activity.ActivityId == overtureStart.ActivityId));
        var overtureWaitStart = await fixture.Signals.WaitForStartAsync("Waiting for Overture administrative cache for USA...");
        fixture.ReleaseOverture.TrySetResult();
        await Task.WhenAll(WaitAsync(overture), WaitAsync(gadm));
        await fixture.Signals.WaitForEndAsync(overtureStart.ActivityId);

        AssertExactEventSequence(fixture.Reporter.EventsFor(overtureSession.Request),
            "RunStarted", "EligibilityDetermined:0",
            "LogEmitted:Information:Checking bundled Overture country coverage...",
            "LogEmitted:Information:Country resolved as United States (USA).",
            "ActivityStarted:Downloading Overture administrative cache for USA...",
            "LogEmitted:Information:Starting Overture administrative cache download for USA.",
            "ActivityEnded:Downloading Overture administrative cache for USA...",
            "LogEmitted:Information:Overture administrative cache ready for USA.",
            "LogEmitted:Information:Querying cached Overture administrative areas...");
        AssertExactEventSequence(fixture.Reporter.EventsFor(gadmSession.Request),
            "RunStarted", "EligibilityDetermined:0",
            "LogEmitted:Information:Checking bundled Overture country coverage...",
            "LogEmitted:Information:Country resolved as United States (USA).",
            "LogEmitted:Information:Preparing GADM administrative caches for USA...",
            "ActivityStarted:Downloading GADM administrative cache for USA...",
            "LogEmitted:Information:Starting GADM administrative cache download for USA.",
            "ActivityEnded:Downloading GADM administrative cache for USA...",
            "LogEmitted:Information:GADM administrative cache ready for USA.",
            "LogEmitted:Information:Querying cached GADM administrative areas across USA...",
            "ActivityStarted:Waiting for Overture administrative cache for USA...",
            "LogEmitted:Information:Waiting for in-flight Overture administrative cache download for USA.",
            "ActivityEnded:Waiting for Overture administrative cache for USA...",
            "LogEmitted:Information:Overture administrative cache ready for USA.",
            "LogEmitted:Information:Querying cached Overture administrative areas...");
    }
    [TestMethod]
    public async Task ResolverFailureMatrix_PreservesSourceCancellationMemoryAndReporterSemantics()
    {
        await using (var fallback = await ResolverFixture.CreateAsync(Source.Gadm, failure: new InvalidOperationException("offline")))
        {
            var session = await fallback.OpenSessionAsync();
            var result = await WaitAsync(fallback.ResolveAsync(true, session));
            Assert.IsNotNull(result);
            AssertInformationLogs(fallback.Reporter.Events, ExpectedGadmFallbackLogs("offline"));
            AssertActivityIdsPaired(fallback.Reporter.Events, 2,
                AcceptedActivityId(fallback.Reporter.Events, "Downloading GADM administrative cache for USA..."),
                AcceptedActivityId(fallback.Reporter.Events, "Downloading Overture administrative cache for USA..."));
        }
        await using (var foreignCancellation = await ResolverFixture.CreateAsync(Source.Gadm, failure: new OperationCanceledException("foreign owner")))
        {
            var session = await foreignCancellation.OpenSessionAsync();
            var result = await WaitAsync(foreignCancellation.ResolveAsync(true, session));
            Assert.IsNotNull(result);
            AssertInformationLogs(foreignCancellation.Reporter.Events, ExpectedGadmFallbackLogs("foreign owner"));
            AssertActivityIdsPaired(foreignCancellation.Reporter.Events, 2,
                AcceptedActivityId(foreignCancellation.Reporter.Events, "Downloading GADM administrative cache for USA..."),
                AcceptedActivityId(foreignCancellation.Reporter.Events, "Downloading Overture administrative cache for USA..."));
        }
        await using (var overture = await ResolverFixture.CreateAsync(Source.Overture, failure: new InvalidOperationException("offline")))
        {
            var session = await overture.OpenSessionAsync();
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => WaitAsync(overture.ResolveAsync(false, session)));
            AssertInformationLogs(overture.Reporter.Events, ExpectedOvertureUnwindLogs());
            AssertActivityIdsPaired(overture.Reporter.Events, 1, AcceptedActivityId(overture.Reporter.Events, "Downloading Overture administrative cache for USA..."));
        }
        await using (var oom = await ResolverFixture.CreateAsync(Source.Gadm, failure: new OutOfMemoryException("oom")))
        {
            var session = await oom.OpenSessionAsync();
            await Assert.ThrowsExactlyAsync<OutOfMemoryException>(() => WaitAsync(oom.ResolveAsync(true, session)));
            AssertInformationLogs(oom.Reporter.Events, ExpectedGadmUnwindLogs());
            AssertActivityIdsPaired(oom.Reporter.Events, 1, AcceptedActivityId(oom.Reporter.Events, "Downloading GADM administrative cache for USA..."));
        }
        await using (var cancelled = await ResolverFixture.CreateAsync(Source.Gadm, gated: true))
        {
            var session = await cancelled.OpenSessionAsync();
            using var cts = new CancellationTokenSource();
            var task = cancelled.ResolveAsync(true, session, cts.Token);
            await WaitAsync(cancelled.SourceEntered.Task);
            cts.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => WaitAsync(task));
            AssertInformationLogs(cancelled.Reporter.Events, ExpectedGadmUnwindLogs());
            AssertActivityIdsPaired(cancelled.Reporter.Events, 1, AcceptedActivityId(cancelled.Reporter.Events, "Downloading GADM administrative cache for USA..."));
        }
        foreach (var (fault, gadm) in new[] { (typeof(ActivityStarted), false), (typeof(LogEmitted), false), (typeof(ActivityEnded), false), (typeof(LogEmitted), true) })
        {
            await using var fixture = await ResolverFixture.CreateAsync(gadm ? Source.Gadm : Source.Overture, ready: fault == typeof(LogEmitted));
            var sinkFailure = new InvalidOperationException(gadm ? "GADM sink failed" : "sink failed");
            fixture.Reporter.FailureFactory = processingEvent => processingEvent.GetType() == fault
                && (!gadm || processingEvent is LogEmitted { Message: "GADM administrative cache already ready for USA." })
                    ? sinkFailure
                    : null;
            var session = await fixture.OpenSessionAsync();
            var reporterFailure = await Assert.ThrowsExactlyAsync<ProcessingEventReportingException>(() => WaitAsync(fixture.ResolveAsync(gadm, session)));
            Assert.AreSame(sinkFailure, reporterFailure.ReporterException);
            Assert.AreSame(sinkFailure, reporterFailure.InnerException);
            var expectedAttempts = (fault, gadm) switch
            {
                ({ } type, false) when type == typeof(ActivityStarted) => new[]
                {
                    "RunStarted", "EligibilityDetermined:0",
                    "LogEmitted:Information:Checking bundled Overture country coverage...",
                    "LogEmitted:Information:Country resolved as United States (USA).",
                    "ActivityStarted:Downloading Overture administrative cache for USA..."
                },
                ({ } type, false) when type == typeof(LogEmitted) => new[]
                {
                    "RunStarted", "EligibilityDetermined:0",
                    "LogEmitted:Information:Checking bundled Overture country coverage..."
                },
                ({ } type, false) when type == typeof(ActivityEnded) => new[]
                {
                    "RunStarted", "EligibilityDetermined:0",
                    "LogEmitted:Information:Checking bundled Overture country coverage...",
                    "LogEmitted:Information:Country resolved as United States (USA).",
                    "ActivityStarted:Downloading Overture administrative cache for USA...",
                    "LogEmitted:Information:Starting Overture administrative cache download for USA.",
                    "ActivityEnded:Downloading Overture administrative cache for USA..."
                },
                _ => new[]
                {
                    "RunStarted", "EligibilityDetermined:0",
                    "LogEmitted:Information:Checking bundled Overture country coverage...",
                    "LogEmitted:Information:Country resolved as United States (USA).",
                    "LogEmitted:Information:Preparing GADM administrative caches for USA...",
                    "LogEmitted:Information:GADM administrative cache already ready for USA."
                }
            };
            AssertExactEventSequence(fixture.Reporter.Attempts, expectedAttempts);
            AssertExactEventSequence(fixture.Reporter.Events, expectedAttempts[..^1]);
        }
    }

    [TestMethod]
    [DataRow("activity")]
    [DataRow("log")]
    public async Task ForeignReporterCancellationAtResolverAdmission_PreservesMarkerAndDoesNotNormalize(string admission)
    {
        await using var fixture = await ResolverFixture.CreateAsync(Source.Gadm);
        var foreignCancellation = new OperationCanceledException("foreign reporter cancellation", CancellationToken.None);
        fixture.Reporter.FailureFactory = processingEvent => admission switch
        {
            "activity" when processingEvent is ActivityStarted { Label: "Downloading GADM administrative cache for USA..." } => foreignCancellation,
            "log" when processingEvent is LogEmitted { Message: "Starting GADM administrative cache download for USA." } => foreignCancellation,
            _ => null
        };
        var session = await fixture.OpenSessionAsync();

        var failure = await Assert.ThrowsExactlyAsync<ProcessingEventReportingException>(() => WaitAsync(fixture.ResolveAsync(true, session)));

        Assert.IsFalse(foreignCancellation.CancellationToken.IsCancellationRequested);
        Assert.AreSame(foreignCancellation, failure.ReporterException);
        Assert.AreSame(foreignCancellation, failure.InnerException);
        Assert.AreEqual(0, fixture.Reporter.Attempts.OfType<LogEmitted>().Count(log => log.Message.Contains("unavailable", StringComparison.OrdinalIgnoreCase)));
        Assert.AreEqual(0, fixture.Reporter.Attempts.OfType<LogEmitted>().Count(log => log.Message.Contains("Overture administrative cache", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ResolverInformationAndActivities_ReportsCompleteArraysAcrossTerminalUnwinds()
    {
        await using (var success = await ResolverFixture.CreateAsync(Source.Overture))
        {
            var session = await success.OpenSessionAsync();
            await WaitAsync(success.ResolveAsync(false, session));
            AssertInformationLogs(success.Reporter.Events, ExpectedLogs(false, "Starting Overture administrative cache download for USA."));
            AssertActivityIdsPaired(success.Reporter.Events, 1, AcceptedActivityId(success.Reporter.Events, "Downloading Overture administrative cache for USA..."));
        }
        await using (var failure = await ResolverFixture.CreateAsync(Source.Gadm, failure: new InvalidOperationException("offline")))
        {
            var session = await failure.OpenSessionAsync();
            await WaitAsync(failure.ResolveAsync(true, session));
            AssertInformationLogs(failure.Reporter.Events, ExpectedGadmFallbackLogs("offline"));
            AssertActivityIdsPaired(failure.Reporter.Events, 2,
                AcceptedActivityId(failure.Reporter.Events, "Downloading GADM administrative cache for USA..."),
                AcceptedActivityId(failure.Reporter.Events, "Downloading Overture administrative cache for USA..."));
        }
        await using (var overtureFailure = await ResolverFixture.CreateAsync(Source.Overture, failure: new InvalidOperationException("offline")))
        {
            var session = await overtureFailure.OpenSessionAsync();
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => WaitAsync(overtureFailure.ResolveAsync(false, session)));
            AssertInformationLogs(overtureFailure.Reporter.Events, ExpectedOvertureUnwindLogs());
            AssertActivityIdsPaired(overtureFailure.Reporter.Events, 1, AcceptedActivityId(overtureFailure.Reporter.Events, "Downloading Overture administrative cache for USA..."));
        }
        await using (var cancelled = await ResolverFixture.CreateAsync(Source.Gadm, gated: true))
        {
            var session = await cancelled.OpenSessionAsync();
            using var cts = new CancellationTokenSource();
            var task = cancelled.ResolveAsync(true, session, cts.Token);
            await WaitAsync(cancelled.SourceEntered.Task);
            cts.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => WaitAsync(task));
            AssertInformationLogs(cancelled.Reporter.Events, ExpectedGadmUnwindLogs());
            AssertActivityIdsPaired(cancelled.Reporter.Events, 1, AcceptedActivityId(cancelled.Reporter.Events, "Downloading GADM administrative cache for USA..."));
        }
        await using (var oom = await ResolverFixture.CreateAsync(Source.Gadm, failure: new OutOfMemoryException("oom")))
        {
            var session = await oom.OpenSessionAsync();
            await Assert.ThrowsExactlyAsync<OutOfMemoryException>(() => WaitAsync(oom.ResolveAsync(true, session)));
            AssertInformationLogs(oom.Reporter.Events, ExpectedGadmUnwindLogs());
            AssertActivityIdsPaired(oom.Reporter.Events, 1, AcceptedActivityId(oom.Reporter.Events, "Downloading GADM administrative cache for USA..."));
        }
    }

    [TestMethod]
    public async Task NoOpSession_ProducesTheSameResolutionWithoutReceiverEvents()
    {
        await using var fixture = await ResolverFixture.CreateAsync(Source.Overture, ready: true);
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual);
        var session = await NoOpProcessingEventReporter.Instance.OpenRunAsync(request, DateTimeOffset.UtcNow);
        await session.DetermineEligibilityAsync(0);

        var noOpResult = await WaitAsync(fixture.ResolveAsync(false, session));
        var unreportedResult = await WaitAsync(fixture.ResolveAsync(false, null));

        Assert.AreEqual(unreportedResult, noOpResult);
        Assert.AreEqual(0, fixture.Reporter.Events.Count);
    }

    [TestMethod]
    public async Task ReportedAndLookupStyleNoReportOverlap_StaysInvocationIsolated()
    {
        await using var fixture = await ResolverFixture.CreateAsync(Source.Overture, gated: true);
        var session = await fixture.OpenSessionAsync();
        var reported = fixture.ResolveAsync(false, session);
        var lookupStyleNoReport = fixture.ResolveAsync(false, null);
        await WaitAsync(fixture.SourceEntered.Task);
        fixture.ReleaseSource.TrySetResult();
        await Task.WhenAll(WaitAsync(reported), WaitAsync(lookupStyleNoReport));

        var events = fixture.Reporter.Events;
        Assert.AreEqual(0, events.Count(x => !ReferenceEquals(x.Request, session.Request)));
        var reportedStart = events.OfType<ActivityStarted>().Single(activity => activity.Label == "Downloading Overture administrative cache for USA...");
        AssertActivityIdsPaired(events, 1, reportedStart.ActivityId);
        AssertInformationLogs(events, ExpectedLogs(false, "Starting Overture administrative cache download for USA."));
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
        CollectionAssert.AreEqual(expected, actual);
    }

    private static string DescribeStart(ActivityStarted started, IDictionary<Guid, string> labelsById)
    {
        labelsById.Add(started.ActivityId, started.Label);
        return $"ActivityStarted:{started.Label}";
    }

    private static void AssertInformationLogs(IEnumerable<ProcessingEvent> events, params string[] expected)
    {
        var actual = events.OfType<LogEmitted>().ToArray();
        Assert.AreEqual(expected.Length, actual.Length, $"Expected complete Information log array: {string.Join(" | ", expected)}. Actual: {string.Join(" | ", actual.Select(x => $"[{x.Level}] {x.Message}"))}");
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.AreEqual(ProcessingLogLevel.Information, actual[index].Level, $"Log {index} must be Information.");
            Assert.AreEqual(expected[index], actual[index].Message, $"Unexpected Information log at index {index}.");
        }
    }

    private static Guid AcceptedActivityId(IEnumerable<ProcessingEvent> events, string expectedLabel)
    {
        return events.OfType<ActivityStarted>().Single(activity => activity.Label == expectedLabel).ActivityId;
    }

    private static void AssertActivityIdsPaired(IEnumerable<ProcessingEvent> events, int expectedAcceptedCount, params Guid[] expectedAcceptedIds)
    {
        Assert.AreEqual(expectedAcceptedCount, expectedAcceptedIds.Length, "The proof must name every expected accepted activity ID.");
        Assert.AreEqual(expectedAcceptedIds.Length, expectedAcceptedIds.Distinct().Count(), "Expected accepted activity IDs must be unique.");
        var starts = events.OfType<ActivityStarted>().ToArray();
        var ends = events.OfType<ActivityEnded>().ToArray();
        foreach (var start in starts)
        {
            Assert.AreNotEqual(Guid.Empty, start.ActivityId, "Every accepted activity-start ID must be non-empty.");
        }

        Assert.AreEqual(expectedAcceptedCount, starts.Length, "Unexpected accepted activity-start count.");
        Assert.AreEqual(expectedAcceptedCount, ends.Length, "Unexpected accepted activity-end count.");
        CollectionAssert.AreEquivalent(expectedAcceptedIds, starts.Select(activity => activity.ActivityId).ToArray(), "Accepted activity IDs differed.");
        foreach (var expectedId in expectedAcceptedIds)
        {
            Assert.AreEqual(1, starts.Count(activity => activity.ActivityId == expectedId), $"Activity {expectedId} must start exactly once.");
            Assert.AreEqual(1, ends.Count(activity => activity.ActivityId == expectedId), $"Activity {expectedId} must have exactly one paired end.");
        }
    }

    private static string[] ExpectedLogs(bool gadm, string cacheMessage)
    {
        var messages = new List<string>
        {
            "Checking bundled Overture country coverage...",
            "Country resolved as United States (USA)."
        };
        if (!gadm)
        {
            messages.Add(cacheMessage);
            messages.Add("Overture administrative cache ready for USA.");
            messages.Add("Querying cached Overture administrative areas...");
            return messages.ToArray();
        }

        messages.Add("Preparing GADM administrative caches for USA...");
        messages.Add(cacheMessage);
        messages.Add("GADM administrative cache ready for USA.");
        messages.Add("Querying cached GADM administrative areas across USA...");
        messages.Add("Starting Overture administrative cache download for USA.");
        messages.Add("Overture administrative cache ready for USA.");
        messages.Add("Querying cached Overture administrative areas...");
        return messages.ToArray();
    }

    private static string[] ExpectedSuccessfulCacheEvents(bool gadm, string cacheMessage)
    {
        var events = new List<string>
        {
            "RunStarted", "EligibilityDetermined:0",
            "LogEmitted:Information:Checking bundled Overture country coverage...",
            "LogEmitted:Information:Country resolved as United States (USA)."
        };
        if (!gadm)
        {
            events.Add(cacheMessage.StartsWith("Starting", StringComparison.Ordinal) ? "ActivityStarted:Downloading Overture administrative cache for USA..." : "ActivityStarted:Waiting for Overture administrative cache for USA...");
            events.Add($"LogEmitted:Information:{cacheMessage}");
            events.Add(cacheMessage.StartsWith("Starting", StringComparison.Ordinal) ? "ActivityEnded:Downloading Overture administrative cache for USA..." : "ActivityEnded:Waiting for Overture administrative cache for USA...");
            events.Add("LogEmitted:Information:Overture administrative cache ready for USA.");
            events.Add("LogEmitted:Information:Querying cached Overture administrative areas...");
            return events.ToArray();
        }

        events.Add("LogEmitted:Information:Preparing GADM administrative caches for USA...");
        events.Add(cacheMessage.StartsWith("Starting", StringComparison.Ordinal) ? "ActivityStarted:Downloading GADM administrative cache for USA..." : "ActivityStarted:Waiting for GADM administrative cache for USA...");
        events.Add($"LogEmitted:Information:{cacheMessage}");
        events.Add(cacheMessage.StartsWith("Starting", StringComparison.Ordinal) ? "ActivityEnded:Downloading GADM administrative cache for USA..." : "ActivityEnded:Waiting for GADM administrative cache for USA...");
        events.Add("LogEmitted:Information:GADM administrative cache ready for USA.");
        events.Add("LogEmitted:Information:Querying cached GADM administrative areas across USA...");
        events.Add("ActivityStarted:Downloading Overture administrative cache for USA...");
        events.Add("LogEmitted:Information:Starting Overture administrative cache download for USA.");
        events.Add("ActivityEnded:Downloading Overture administrative cache for USA...");
        events.Add("LogEmitted:Information:Overture administrative cache ready for USA.");
        events.Add("LogEmitted:Information:Querying cached Overture administrative areas...");
        return events.ToArray();
    }

    private static string[] ExpectedAlreadyReadyLogs(bool gadm)
    {
        if (!gadm)
        {
            return ExpectedLogs(false, "Overture administrative cache already ready for USA.");
        }

        return
        [
            "Checking bundled Overture country coverage...",
            "Country resolved as United States (USA).",
            "Preparing GADM administrative caches for USA...",
            "GADM administrative cache already ready for USA.",
            "GADM administrative cache ready for USA.",
            "Querying cached GADM administrative areas across USA...",
            "Overture administrative cache already ready for USA.",
            "Overture administrative cache ready for USA.",
            "Querying cached Overture administrative areas..."
        ];
    }

    private static string[] ExpectedGadmFallbackLogs(string reason) =>
    [
        "Checking bundled Overture country coverage...",
        "Country resolved as United States (USA).",
        "Preparing GADM administrative caches for USA...",
        "Starting GADM administrative cache download for USA.",
        $"GADM administrative cache unavailable for USA: {reason}",
        "No GADM administrative caches are available for this lookup.",
        "Starting Overture administrative cache download for USA.",
        "Overture administrative cache ready for USA.",
        "Querying cached Overture administrative areas..."
    ];

    private static string[] ExpectedOvertureUnwindLogs() =>
    [
        "Checking bundled Overture country coverage...",
        "Country resolved as United States (USA).",
        "Starting Overture administrative cache download for USA."
    ];

    private static string[] ExpectedGadmUnwindLogs() =>
    [
        "Checking bundled Overture country coverage...",
        "Country resolved as United States (USA).",
        "Preparing GADM administrative caches for USA...",
        "Starting GADM administrative cache download for USA."
    ];
    private static async Task WaitAsync(Task task) => await task.WaitAsync(TestTimeout);
    private static async Task<T> WaitAsync<T>(Task<T> task) => await task.WaitAsync(TestTimeout);
    private static string FindRepoRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    internal enum Source { Overture, Gadm, Both }

    internal sealed class ActivitySignals
    {
        private readonly ConcurrentDictionary<string, ConcurrentQueue<ActivityStarted>> _starts = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<(string Label, int Count), TaskCompletionSource> _startSignals = new();
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _endSignals = new();

        public ActivitySignals(RecordingProcessingEventReporter reporter)
        {
            reporter.AfterAcceptAsync = (processingEvent, _) =>
            {
                if (processingEvent is ActivityStarted started)
                {
                    var starts = _starts.GetOrAdd(started.Label, _ => new());
                    starts.Enqueue(started);
                    foreach (var signal in _startSignals.Where(x => x.Key.Label == started.Label && starts.Count >= x.Key.Count))
                    {
                        signal.Value.TrySetResult();
                    }
                }
                else if (processingEvent is ActivityEnded ended)
                {
                    _endSignals.GetOrAdd(ended.ActivityId, _ => NewSignal()).TrySetResult();
                }

                return ValueTask.CompletedTask;
            };
        }

        public async Task<ActivityStarted> WaitForStartAsync(string label)
        {
            return (await WaitForStartsAsync(label, 1))[0];
        }

        public async Task<ActivityStarted[]> WaitForStartsAsync(string label, int count)
        {
            var signal = _startSignals.GetOrAdd((label, count), _ => NewSignal());
            if (_starts.TryGetValue(label, out var starts) && starts.Count >= count)
            {
                signal.TrySetResult();
            }

            await signal.Task.WaitAsync(TestTimeout);
            return _starts[label].ToArray();
        }

        public async Task WaitForEndAsync(Guid activityId)
        {
            await _endSignals.GetOrAdd(activityId, _ => NewSignal()).Task.WaitAsync(TestTimeout);
        }

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed class ResolverFixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly Source _source;
        private readonly TaskCompletionSource _overtureEntered;
        private readonly TaskCompletionSource _gadmEntered;
        private readonly TaskCompletionSource _releaseOverture;
        private readonly TaskCompletionSource _releaseGadm;
        private readonly OvertureDivisionCacheService _overture;
        private readonly GadmDivisionCacheService _gadm;
        private readonly AdministrativeAreaResolverService _resolver;
        public RecordingProcessingEventReporter Reporter { get; }
        public ActivitySignals Signals { get; }
        public TaskCompletionSource SourceEntered => _source == Source.Gadm ? _gadmEntered : _overtureEntered;
        public TaskCompletionSource ReleaseSource => _source == Source.Gadm ? _releaseGadm : _releaseOverture;
        public TaskCompletionSource OvertureEntered => _overtureEntered;
        public TaskCompletionSource GadmEntered => _gadmEntered;
        public TaskCompletionSource ReleaseOverture => _releaseOverture;
        public TaskCompletionSource ReleaseGadm => _releaseGadm;

        private ResolverFixture(string root, Source source, OvertureDivisionCacheService overture, GadmDivisionCacheService gadm, AdministrativeAreaResolverService resolver, TaskCompletionSource overtureEntered, TaskCompletionSource gadmEntered, TaskCompletionSource releaseOverture, TaskCompletionSource releaseGadm, RecordingProcessingEventReporter reporter, ActivitySignals signals)
        { _root = root; _source = source; _overture = overture; _gadm = gadm; _resolver = resolver; _overtureEntered = overtureEntered; _gadmEntered = gadmEntered; _releaseOverture = releaseOverture; _releaseGadm = releaseGadm; Reporter = reporter; Signals = signals; }

        public static async Task<ResolverFixture> CreateAsync(Source source, bool ready = false, bool gated = false, Exception? failure = null, RecordingProcessingEventReporter? reporter = null, ActivitySignals? signals = null)
        {
            var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(root, "defaults"));
            File.Copy(Path.Combine(FindRepoRoot(), "src/ImmichReverseGeo.Web/bundled-data/defaults/overture-country-divisions.db"), Path.Combine(root, "defaults/overture-country-divisions.db"));
            if (ready) { CreateOverture(root); CreateGadm(root); }
            var oe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var ge = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ro = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var rg = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task OSource(string _, CancellationToken ct)
            {
                oe.TrySetResult();
                if (gated && source is Source.Overture or Source.Both) { await ro.Task.WaitAsync(TestTimeout, ct); }
                if (failure is not null && source is Source.Overture) { throw failure; }
                CreateOverture(root);
            }
            async Task GSource(string _, CancellationToken ct)
            {
                ge.TrySetResult();
                if (gated && source is Source.Gadm or Source.Both) { await rg.Task.WaitAsync(TestTimeout, ct); }
                if (failure is not null && source is Source.Gadm) { throw failure; }
                CreateGadm(root);
            }
            reporter ??= new RecordingProcessingEventReporter();
            signals ??= new ActivitySignals(reporter);
            var catalog = CountryIdentityCatalog.Load(Path.Combine(FindRepoRoot(), "src/ImmichReverseGeo.Web/bundled-data/iso3166.json"));
            var overture = new OvertureDivisionCacheService(NullLogger<OvertureDivisionCacheService>.Instance, root, iso => catalog.FindByAlpha3(iso)?.Alpha2, new OvertureDivisionCacheTestHooks { SourceOperation = OSource });
            var gadm = CreateGadmCache(root, GSource);
            var storage = new StorageOptions(root, root); var places = new OverturePlacesService(NullLogger<OverturePlacesService>.Instance, root, root); var divisions = new OvertureDivisionsService(NullLogger<OvertureDivisionsService>.Instance, places, root, root, alpha => catalog.FindByAlpha2(alpha)?.Alpha3);
            var resolver = new AdministrativeAreaResolverService(NullLogger<AdministrativeAreaResolverService>.Instance, new CityResolverProfileCatalogService(NullLogger<CityResolverProfileCatalogService>.Instance, storage), divisions, overture, new GadmDivisionsService(NullLogger<GadmDivisionsService>.Instance, root), gadm);
            await Task.CompletedTask; return new(root, source, overture, gadm, resolver, oe, ge, ro, rg, reporter, signals);
        }

        public async Task<IProcessingRunEventSession> OpenSessionAsync() { var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual); var session = await Reporter.OpenRunAsync(request, DateTimeOffset.UtcNow); await session.DetermineEligibilityAsync(0); return session; }
        public Task StartOwner(bool gadm) => gadm ? _gadm.GetOrStartDownload("USA").Task : _overture.GetOrStartDownload("USA").Task;
        public Task<AdministrativeAreaResolution?> ResolveAsync(bool gadm, IProcessingRunEventSession? session, CancellationToken ct = default) { var config = new ProcessingConfig { UseGadmAdministrativeAreas = gadm, PreferGadmAdministrativeAreas = gadm, UseGadmTerritoryFallbacks = false }; return session is null ? _resolver.ResolveAsync(38.9, -77.0, config, ct) : _resolver.ResolveAsync(38.9, -77.0, config, session, ct); }
        public string[] Logs() => Reporter.Events.OfType<LogEmitted>().Select(x => x.Message).ToArray();
        public ValueTask DisposeAsync() { SqliteConnection.ClearAllPools(); Directory.Delete(_root, true); return ValueTask.CompletedTask; }
        private static GadmDivisionCacheService CreateGadmCache(string root, Func<string, CancellationToken, Task> source)
        {
            var constructor = typeof(GadmDivisionCacheService).GetConstructor(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, binder: null, new[] { typeof(Microsoft.Extensions.Logging.ILogger<GadmDivisionCacheService>), typeof(string), typeof(Func<string, CancellationToken, Task>) }, modifiers: null)!;
            return (GadmDivisionCacheService)constructor.Invoke(new object[] { NullLogger<GadmDivisionCacheService>.Instance, root, source });
        }
        private static void CreateOverture(string root) => CreateDb(Path.Combine(root, "overture-divisions/USA.db"), "division_area", "release");
        private static void CreateGadm(string root) => CreateDb(Path.Combine(root, "gadm-divisions/USA.db"), "gadm_area", "version");
        private static void CreateDb(string path, string table, string version) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); using var c = new SqliteConnection($"Data Source={path};Pooling=false"); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = $"CREATE TABLE IF NOT EXISTS {table} (id TEXT PRIMARY KEY); INSERT OR IGNORE INTO {table} VALUES ('x'); CREATE TABLE IF NOT EXISTS _meta (key TEXT PRIMARY KEY, value TEXT NOT NULL); INSERT OR REPLACE INTO _meta VALUES ('{version}','test'); INSERT OR REPLACE INTO _meta VALUES ('downloadedAt','2026-01-01T00:00:00Z');"; cmd.ExecuteNonQuery(); }
    }
}
