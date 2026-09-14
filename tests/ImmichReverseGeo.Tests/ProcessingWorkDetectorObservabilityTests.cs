using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using ImmichReverseGeo.Tests.ApplicationComposition;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ImmichReverseGeo.Tests;

[TestClass]
[TestCategory("Change59")]
public sealed class ProcessingWorkDetectorObservabilityTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private const string ExpectedTemplate = "Processing work detector completed: duration_ms={duration_ms}, outcome={outcome}, strategy={strategy}, trigger={trigger}, purpose={purpose}, coverage={coverage}, database_operation={database_operation}.";

    [TestMethod]
    [DataRow(false, 999, false)]
    [DataRow(true, 999, true)]
    [DataRow(false, 1000, true)]
    [DataRow(true, 1000, false)]
    public async Task SuccessfulResultKeepsIdentityAndEmitsOneExactMeasurement(bool hasWork, int elapsed, bool fallback)
    {
        var clock = new DetectorClock();
        var logger = new DetectorLog();
        using var caller = new CancellationTokenSource();
        var request = ProcessingWorkDetectorStub.Request();
        var expected = ProcessingWorkDetectorStub.Result(hasWork, ProcessingWorkDetectorKind.Existence, fallback);
        var inner = new ProcessingWorkDetectorStub((actualRequest, token) =>
        {
            Assert.AreSame(request, actualRequest);
            Assert.AreEqual(caller.Token, token);
            clock.Advance(elapsed);
            return Task.FromResult(expected);
        });
        var detector = new InstrumentedProcessingWorkDetector(inner, clock, logger);
        Assert.AreSame(expected, await detector.DetectAsync(request, caller.Token));
        Assert.AreEqual(1, inner.Calls.Length);
        Entry entry = logger.Entries.Single();
        AssertMeasurement(entry, hasWork ? "HasWork" : "NoWork", elapsed,
            elapsed >= 1000 ? LogLevel.Warning : LogLevel.Information, fallback);
    }

    [TestMethod]
    [DataRow("ordinary", false, 999)]
    [DataRow("ordinary", true, 999)]
    [DataRow("timeout", false, 999)]
    [DataRow("timeout", true, 1000)]
    [DataRow("database-timeout", false, 999)]
    [DataRow("database-timeout", true, 999)]
    [DataRow("unmatched-cancellation", false, 999)]
    [DataRow("unmatched-cancellation", true, 999)]
    [DataRow("caller-cancellation", false, 1000)]
    [DataRow("caller-cancellation", true, 999)]
    [DataRow("caller-cancellation", true, 1000)]
    public async Task ExceptionClassificationUsesOnlyTheSuppliedTokenAndPreservesTheException(
        string kind, bool cancelCaller, int elapsed)
    {
        var clock = new DetectorClock();
        var logger = new DetectorLog();
        using var caller = new CancellationTokenSource();
        using var other = new CancellationTokenSource();
        other.Cancel();
        Exception expected = kind switch
        {
            "timeout" => new TimeoutException("timeout-sentinel"),
            "database-timeout" => new NpgsqlException("database-timeout-sentinel", new TimeoutException()),
            "unmatched-cancellation" => new OperationCanceledException(other.Token),
            "caller-cancellation" => new OperationCanceledException(caller.Token),
            _ => new InvalidOperationException("failure-sentinel")
        };
        var inner = new ProcessingWorkDetectorStub((_, token) =>
        {
            Assert.AreEqual(caller.Token, token);
            clock.Advance(elapsed);
            if (cancelCaller)
            {
                caller.Cancel();
            }
            return Task.FromException<ProcessingWorkDetectionResult>(expected);
        });
        var detector = new InstrumentedProcessingWorkDetector(inner, clock, logger);
        Exception actual = await Assert.ThrowsAsync<Exception>(() => detector.DetectAsync(ProcessingWorkDetectorStub.Request(), caller.Token));
        Assert.AreSame(expected, actual);
        if (actual is OperationCanceledException cancellation)
        {
            Assert.AreEqual(((OperationCanceledException)expected).CancellationToken, cancellation.CancellationToken);
        }
        Assert.AreEqual(1, inner.Calls.Length);
        AssertMeasurement(logger.Entries.Single(), cancelCaller ? "Cancelled" : "Failed", elapsed,
            !cancelCaller || elapsed >= 1000 ? LogLevel.Warning : LogLevel.Information, fallback: null);
    }

    [TestMethod]
    public async Task SuccessfulReturnAfterCallerCancellationIsNotTranslatedByTheObserver()
    {
        var clock = new DetectorClock();
        var logger = new DetectorLog();
        using var caller = new CancellationTokenSource();
        var expected = ProcessingWorkDetectorStub.Result(false, ProcessingWorkDetectorKind.Existence);
        var inner = new ProcessingWorkDetectorStub((_, _) =>
        {
            caller.Cancel();
            clock.Advance(17);
            return Task.FromResult(expected);
        });
        var detector = new InstrumentedProcessingWorkDetector(inner, clock, logger);
        Assert.AreSame(expected, await detector.DetectAsync(ProcessingWorkDetectorStub.Request(), caller.Token));
        AssertMeasurement(logger.Entries.Single(), "NoWork", 17, LogLevel.Information, false);
    }

    [TestMethod]
    public async Task HostileExceptionAndClosedRequestCannotExportSensitiveDimensions()
    {
        string[] forbidden =
        [
            "SELECT secret_location FROM private_assets WHERE secret_id=@secret_parameter",
            "gps=51.234,-0.987", "asset=93f91e86-5a34-4ce2-a2b0-615b56016e76", "run=secret-run-id",
            "Host=secret-host;Database=secret-db;Username=secret-user;Password=secret-password",
            "parameter-value=private-value", "cursor=private-cursor", "work-set=private-set"
        ];
        var expected = new InvalidOperationException(string.Join(";", forbidden))
        {
            HelpLink = "https://secret-host/private-path",
            Source = "secret-source"
        };
        expected.Data["secret-data-key"] = forbidden;
        var request = ProcessingWorkDetectorStub.Request();
        CollectionAssert.AreEquivalent(new[] { "Trigger", "Snapshot" },
            request.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).ToArray(), "The request has no free-form text, identities or connection fields.");
        CollectionAssert.AreEquivalent(new[] { "Purpose", "Coverage" },
            request.Snapshot.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).ToArray());
        var logger = new DetectorLog();
        var detector = new InstrumentedProcessingWorkDetector(ProcessingWorkDetectorStub.Throwing(expected), new DetectorClock(), logger);
        Assert.AreSame(expected, await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => detector.DetectAsync(request, CancellationToken.None)));
        Entry entry = logger.Entries.Single();
        AssertMeasurement(entry, "Failed", 0, LogLevel.Warning, null);
        string serialized = System.Text.Json.JsonSerializer.Serialize(entry.Fields) + entry.Message + entry.StateText + entry.Template;
        foreach (string secret in forbidden.Concat(new[] { expected.Source!, expected.HelpLink!, "secret-data-key", expected.GetType().FullName! }))
        {
            Assert.IsFalse(serialized.Contains(secret, StringComparison.Ordinal), "Sensitive sentinel must not enter any terminal surface: " + secret);
        }
        Assert.IsNull(entry.Exception);
    }

    [TestMethod]
    public async Task ConcurrentCallsFinishOutOfOrderWithIndependentDurationsAndOneEventEach()
    {
        var clock = new DetectorClock();
        var logger = new DetectorLog();
        var inner = new GatedProcessingWorkDetector();
        var detector = new InstrumentedProcessingWorkDetector(inner, clock, logger);
        Task<ProcessingWorkDetectionResult>? first = null;
        Task<ProcessingWorkDetectionResult>? second = null;
        var firstResult = ProcessingWorkDetectorStub.Result(true, ProcessingWorkDetectorKind.Existence);
        var secondResult = ProcessingWorkDetectorStub.Result(false, ProcessingWorkDetectorKind.Existence);
        try
        {
            first = detector.DetectAsync(ProcessingWorkDetectorStub.Request(), CancellationToken.None);
            var firstCall = await inner.NextAsync().WaitAsync(Bound);
            clock.Advance(100);
            second = detector.DetectAsync(ProcessingWorkDetectorStub.Request(), CancellationToken.None);
            var secondCall = await inner.NextAsync().WaitAsync(Bound);
            clock.Advance(200);
            secondCall.Release(secondResult);
            Assert.AreSame(secondResult, await second.WaitAsync(Bound));
            Assert.IsFalse(first.IsCompleted, "The first invocation is still held after the second completed and logged.");
            AssertMeasurement(logger.Entries.Single(), "NoWork", 200, LogLevel.Information, false);
            clock.Advance(700);
            firstCall.Release(firstResult);
            Assert.AreSame(firstResult, await first.WaitAsync(Bound));
            Assert.AreEqual(2, logger.Entries.Count);
            AssertMeasurement(logger.Entries.Last(), "HasWork", 1000, LogLevel.Warning, false);
        }
        finally
        {
            foreach (var call in inner.Calls)
            {
                call.Completion.TrySetResult(ProcessingWorkDetectorStub.Result(false));
            }
            await Task.WhenAll(new[] { first, second }.OfType<Task<ProcessingWorkDetectionResult>>()).WaitAsync(Bound);
        }
        Assert.AreEqual(2, inner.Calls.Length);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LoggingFailureCannotReplaceTheDetectorOutcomeOrCauseARetry(bool innerFails)
    {
        var expected = new InvalidOperationException("primary-detector-error");
        var result = ProcessingWorkDetectorStub.Result(true, ProcessingWorkDetectorKind.Existence);
        var inner = innerFails ? ProcessingWorkDetectorStub.Throwing(expected) : new ProcessingWorkDetectorStub((_, _) => Task.FromResult(result));
        var logger = new DetectorLog { ThrowOnWrite = true };
        var detector = new InstrumentedProcessingWorkDetector(inner, new DetectorClock(), logger);
        if (innerFails)
        {
            Assert.AreSame(expected, await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => detector.DetectAsync(ProcessingWorkDetectorStub.Request(), CancellationToken.None)));
        }
        else
        {
            Assert.AreSame(result, await detector.DetectAsync(ProcessingWorkDetectorStub.Request(), CancellationToken.None));
        }
        Assert.AreEqual(1, logger.Attempts);
        Assert.AreEqual(1, inner.Calls.Length);
    }

    [TestMethod]
    public async Task EveryCallAttemptsEmissionWithoutSamplingOrAnEnabledCheck()
    {
        var logger = new DetectorLog { Enabled = false };
        var inner = ProcessingWorkDetectorStub.Constant(false);
        var detector = new InstrumentedProcessingWorkDetector(inner, new DetectorClock(), logger);
        for (int i = 0; i < 5; i++)
        {
            await detector.DetectAsync(ProcessingWorkDetectorStub.Request(), CancellationToken.None);
        }
        Assert.AreEqual(5, logger.Attempts);
        Assert.AreEqual(5, logger.Entries.Count);
        Assert.AreEqual(0, logger.EnabledChecks);
        Assert.AreEqual(5, inner.Calls.Length);
    }

    [TestMethod]
    public async Task ProductionStandardRootExposesOneLazyInstrumentedSingletonAroundTheRealStrategy()
    {
        using var fixture = ControlPlaneRolePolicyTests.RoleDescriptors.Create(BoundaryRole.Standard);
        var probe = new ObservedProbe();
        var logger = new DetectorLog();
        fixture.Services.RemoveAll<IScheduledRunWorkProbe>();
        fixture.Services.AddSingleton<IScheduledRunWorkProbe>(probe);
        fixture.Services.AddSingleton<ILogger<InstrumentedProcessingWorkDetector>>(logger);
        using ServiceProvider provider = fixture.Services.BuildServiceProvider();
        var detector = provider.GetRequiredService<IProcessingWorkDetector>();
        Assert.AreSame(provider.GetRequiredService<InstrumentedProcessingWorkDetector>(), detector);
        Assert.AreSame(detector, provider.GetRequiredService<IProcessingWorkDetector>());
        Assert.AreNotSame(detector, provider.GetRequiredService<ExistenceProcessingWorkDetector>());
        Assert.AreEqual(1, fixture.Services.Count(d => d.ServiceType == typeof(IProcessingWorkDetector)));
        Assert.AreSame(TimeProvider.System, provider.GetRequiredService<TimeProvider>());
        Assert.AreEqual(0, probe.Calls);
        Assert.IsEmpty(logger.Entries);
        Assert.IsTrue((await detector.DetectAsync(ProcessingWorkDetectorStub.Request(), CancellationToken.None)).HasWork);
        Assert.AreEqual(1, probe.Calls);
        Assert.AreEqual(1, logger.Entries.Count);
        Assert.AreEqual(1, logger.Entries.Single().Fields["database_roundtrips"]);
    }

    private static void AssertMeasurement(Entry entry, string outcome, double duration, LogLevel level, bool? fallback)
    {
        Assert.AreEqual(new EventId(5901, "ProcessingWorkDetectorCompleted"), entry.Event);
        Assert.AreEqual("ProcessingWorkDetectorCompleted", entry.Event.Name);
        Assert.AreEqual(level, entry.Level);
        Assert.AreEqual(ExpectedTemplate, entry.Template);
        Assert.AreEqual(duration, entry.Fields["duration_ms"]);
        Assert.AreEqual(outcome, entry.Fields["outcome"]);
        Assert.AreEqual("postgres-exists-v1", entry.Fields["strategy"]);
        Assert.AreEqual("eligibility-existence-probe", entry.Fields["database_operation"]);
        Assert.AreEqual("Scheduled", entry.Fields["trigger"]);
        Assert.AreEqual("ScheduledLaunch", entry.Fields["purpose"]);
        Assert.AreEqual("FullEligibility", entry.Fields["coverage"]);
        string[] keys = ["duration_ms", "outcome", "strategy", "database_operation", "trigger", "purpose", "coverage", "{OriginalFormat}"];
        if (fallback.HasValue)
        {
            keys = [.. keys, "fallback_used", "database_roundtrips"];
            Assert.AreEqual(fallback.Value, entry.Fields["fallback_used"]);
            Assert.AreEqual(1, entry.Fields["database_roundtrips"]);
        }
        CollectionAssert.AreEquivalent(keys, entry.Fields.Keys.ToArray());
        Assert.IsNull(entry.Exception);
        Assert.AreEqual(entry.Message, entry.StateText);
        Assert.IsTrue(entry.Message.Contains("duration_ms=" + duration.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
        Assert.IsFalse(entry.Message.Contains(nameof(InstrumentedProcessingWorkDetector), StringComparison.Ordinal));
    }

    private sealed class DetectorClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Volatile.Read(ref _timestamp);
        public override DateTimeOffset GetUtcNow() => throw new AssertFailedException("Detector timing must be monotonic, not wall time.");
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => throw new AssertFailedException("Instrumentation must not create a timeout.");
        internal void Advance(long milliseconds) => Interlocked.Add(ref _timestamp, milliseconds);
    }

    private sealed class ObservedProbe : IScheduledRunWorkProbe
    {
        internal int Calls { get; private set; }
        public Task<bool> HasUnprocessedAssetsAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(true);
        }
    }

    private sealed record Entry(LogLevel Level, EventId Event, IReadOnlyDictionary<string, object?> Fields,
        string Template, string Message, string StateText, Exception? Exception);

    private sealed class DetectorLog : ILogger<InstrumentedProcessingWorkDetector>
    {
        internal ConcurrentQueue<Entry> Entries { get; } = new();
        internal bool ThrowOnWrite { get; init; }
        internal bool Enabled { get; init; } = true;
        internal int Attempts;
        internal int EnabledChecks;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel)
        {
            Interlocked.Increment(ref EnabledChecks);
            return Enabled;
        }
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Interlocked.Increment(ref Attempts);
            if (ThrowOnWrite)
            {
                throw new InvalidOperationException("secondary-logging-error");
            }
            var fields = ((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary(p => p.Key, p => p.Value);
            Entries.Enqueue(new(logLevel, eventId, fields, (string)fields["{OriginalFormat}"]!,
                formatter(state, exception), state!.ToString()!, exception));
        }
    }
}
