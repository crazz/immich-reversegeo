using System.Collections.Concurrent;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Overture.Models;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests;

[TestClass]
[TestCategory("Change14")]
public class ProcessingRunExecutorTests
{
    private enum EligibilityOperation { RunStarted, CountEntered, RunFinished }
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task ExecuteAsync_ZeroEligibility_UsesOneSessionBeforeCountAndReturnsExactCompletedResult()
    {
        var countEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var countRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = new ExecutorFixture().EnableReporter().EnableCount(0);
        fixture.CountBehavior = async token =>
        {
            countEntered.TrySetResult();
            await countRelease.Task.WaitAsync(token).ConfigureAwait(false);
            return 0;
        };

        var execution = fixture.Executor.ExecuteAsync(fixture.Request, fixture.Reporter, CancellationToken.None);
        await countEntered.Task.WaitAsync(Bound);
        CollectionAssert.AreEqual(new[] { typeof(RunStarted) }, fixture.Reporter.Events.Select(item => item.GetType()).ToArray());
        countRelease.TrySetResult();
        var result = await execution.WaitAsync(Bound);
        fixture.AssertTerminal(result);
        fixture.Verify("zero-eligibility", result, runToken: CancellationToken.None);

        Assert.AreSame(fixture.Request, result.Request);
        Assert.AreEqual(ProcessingRunOutcome.Completed, result.Outcome);
        Assert.AreEqual(0L, result.ProcessedCount);
        Assert.AreEqual(0L, result.UpdatedCount);
        Assert.AreEqual(0L, result.SkippedCount);
        Assert.AreEqual(0L, result.FailedCount);
        CollectionAssert.AreEqual(new[] { ExecutorCallKind.Count }, fixture.Calls.Select(item => item.Call.Kind).ToArray());
        CollectionAssert.AreEqual(
            new[] { typeof(RunStarted), typeof(EligibilityDetermined), typeof(RunFinished) },
            fixture.Reporter.Events.Select(item => item.GetType()).ToArray());
        Assert.IsTrue(fixture.Reporter.Events.All(item => ReferenceEquals(fixture.Request, item.Request)));
        Assert.AreSame(result, fixture.Reporter.Events.OfType<RunFinished>().Single().Result);
    }

    [TestMethod]
    public async Task ExecuteAsync_ActiveCancellationDuringEligibility_ReturnsCancelledWithoutEligibility()
    {
        using var cancellation = new CancellationTokenSource();
        var operations = new ConcurrentQueue<EligibilityOperation>();
        var dependencies = new GatedEligibilityCancellationOperations(operations);
        var reporter = new EligibilityBoundaryReporter(operations);
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual);
        var execution = dependencies.CreateExecutor().ExecuteAsync(request, reporter, cancellation.Token);

        await dependencies.CountEntered.Task.WaitAsync(Bound);
        cancellation.Cancel();
        var result = await execution.WaitAsync(Bound);

        Assert.AreSame(request, result.Request);
        Assert.AreEqual(ProcessingRunOutcome.Cancelled, result.Outcome);
        Assert.IsNull(result.FailureMessage);
        AssertZeroCounts(result);
        Assert.IsFalse(dependencies.NeverRelease.Task.IsCompleted);
        Assert.AreEqual(0, reporter.Events.OfType<EligibilityDetermined>().Count());
        Assert.AreEqual(0, reporter.Events.OfType<ProgressChanged>().Count());
        Assert.AreEqual(1, reporter.Events.OfType<RunFinished>().Count());
        Assert.AreSame(result, reporter.Events.OfType<RunFinished>().Single().Result);
        Assert.IsTrue(reporter.Events.All(item => ReferenceEquals(request, item.Request)));
        CollectionAssert.AreEqual(
            new[] { typeof(RunStarted), typeof(RunFinished) },
            reporter.Events.Select(item => item.GetType()).ToArray());
        CollectionAssert.AreEqual(
            new[] { EligibilityOperation.RunStarted, EligibilityOperation.CountEntered, EligibilityOperation.RunFinished },
            operations.ToArray());
    }

    [TestMethod]
    public async Task ExecuteAsync_EligibilityFailure_ReturnsFailedWithoutFabricatedEligibility()
    {
        var failure = new InvalidOperationException("eligibility count failed");
        var operations = new ConcurrentQueue<EligibilityOperation>();
        var logger = new EligibilityBoundaryLogger();
        var dependencies = new FailingEligibilityOperations(operations, failure, logger);
        var reporter = new EligibilityBoundaryReporter(operations);
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual);

        var result = await dependencies.CreateExecutor()
            .ExecuteAsync(request, reporter, CancellationToken.None)
            .WaitAsync(Bound);

        Assert.AreSame(request, result.Request);
        Assert.AreEqual(ProcessingRunOutcome.Failed, result.Outcome);
        Assert.AreEqual(failure.Message, result.FailureMessage);
        AssertZeroCounts(result);
        Assert.AreEqual(0, reporter.Events.OfType<EligibilityDetermined>().Count());
        Assert.AreEqual(0, reporter.Events.OfType<ProgressChanged>().Count());
        Assert.AreEqual(1, reporter.Events.OfType<RunFinished>().Count());
        Assert.AreSame(result, reporter.Events.OfType<RunFinished>().Single().Result);
        Assert.IsTrue(reporter.Events.All(item => ReferenceEquals(request, item.Request)));
        CollectionAssert.AreEqual(
            new[] { typeof(RunStarted), typeof(RunFinished) },
            reporter.Events.Select(item => item.GetType()).ToArray());
        var fatal = logger.Entries.Single();
        Assert.AreEqual(LogLevel.Error, fatal.Level);
        Assert.AreEqual("Fatal error during processing run", fatal.Message);
        Assert.AreSame(failure, fatal.Exception);
        CollectionAssert.AreEqual(
            new[] { EligibilityOperation.RunStarted, EligibilityOperation.CountEntered, EligibilityOperation.RunFinished },
            operations.ToArray());
    }

    private static void AssertZeroCounts(ProcessingRunResult result)
    {
        Assert.AreEqual(0L, result.ProcessedCount);
        Assert.AreEqual(0L, result.UpdatedCount);
        Assert.AreEqual(0L, result.SkippedCount);
        Assert.AreEqual(0L, result.FailedCount);
    }

    private sealed class EligibilityBoundaryReporter(ConcurrentQueue<EligibilityOperation> operations) : ProcessingEventReporter
    {
        public ConcurrentQueue<ProcessingEvent> Events { get; } = new();

        protected override ValueTask AcceptAsync(ProcessingEvent processingEvent, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Enqueue(processingEvent);
            operations.Enqueue(processingEvent switch
            {
                RunStarted => EligibilityOperation.RunStarted,
                RunFinished => EligibilityOperation.RunFinished,
                _ => throw new AssertFailedException($"Unexpected eligibility-boundary event {processingEvent.GetType().Name}.")
            });
            return ValueTask.CompletedTask;
        }
    }

    private sealed record EligibilityBoundaryLogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class EligibilityBoundaryLogger : ILogger
    {
        public ConcurrentQueue<EligibilityBoundaryLogEntry> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Enqueue(new EligibilityBoundaryLogEntry(logLevel, formatter(state, exception), exception));
        }
    }

    private abstract class EligibilityBoundaryOperations :
        IProcessingRunConfiguration,
        IProcessingAssetRepository,
        IProcessingSkippedStore,
        IProcessingAdministrativeResolver,
        IProcessingInfrastructureLookup,
        IProcessingRunDelay
    {
        private readonly ILogger _logger;
        protected ConcurrentQueue<EligibilityOperation> Operations { get; }

        protected EligibilityBoundaryOperations(ConcurrentQueue<EligibilityOperation> operations, ILogger logger)
        {
            Operations = operations;
            _logger = logger;
        }

        public ProcessingRunExecutor CreateExecutor()
        {
            return new ProcessingRunExecutor(
                _logger,
                this,
                this,
                this,
                this,
                this,
                this,
                new FixedUtcTimeProvider());
        }

        public abstract Task<long> GetUnprocessedCountAsync(CancellationToken cancellationToken = default);
        public Task<AppConfig> GetConfigAsync() => throw Unexpected("configuration");
        public Task<HashSet<Guid>> GetAllAsync() => throw Unexpected("skipped snapshot");
        public Task<List<AssetRecord>> GetUnprocessedBatchAsync(AssetCursor cursor, int batchSize, CancellationToken cancellationToken = default) => throw Unexpected("batch");
        public Task WriteLocationAsync(Guid assetId, GeoResult geoResult, CancellationToken cancellationToken = default) => throw Unexpected("write");
        public Task AddAsync(Guid assetId) => throw Unexpected("skipped insert");
        public Task<AdministrativeAreaResolution?> ResolveAsync(double latitude, double longitude, ProcessingConfig config, IProcessingRunEventSession session, CancellationToken cancellationToken = default) => throw Unexpected("resolver");
        public Task<OvertureInfrastructureLookupDiagnostics> FindNearestInfrastructureAsync(double latitude, double longitude, string? iso3, CancellationToken cancellationToken = default) => throw Unexpected("airport");
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => throw Unexpected("delay");

        private static AssertFailedException Unexpected(string operation) => new($"Unexpected {operation} operation.");
    }

    private sealed class GatedEligibilityCancellationOperations(ConcurrentQueue<EligibilityOperation> operations)
        : EligibilityBoundaryOperations(operations, NullLogger.Instance)
    {
        public TaskCompletionSource CountEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<long> NeverRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<long> GetUnprocessedCountAsync(CancellationToken cancellationToken = default)
        {
            Operations.Enqueue(EligibilityOperation.CountEntered);
            CountEntered.TrySetResult();
            return await NeverRelease.Task.WaitAsync(Bound, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class FailingEligibilityOperations(
        ConcurrentQueue<EligibilityOperation> operations,
        Exception failure,
        ILogger logger) : EligibilityBoundaryOperations(operations, logger)
    {
        public override Task<long> GetUnprocessedCountAsync(CancellationToken cancellationToken = default)
        {
            Operations.Enqueue(EligibilityOperation.CountEntered);
            return Task.FromException<long>(failure);
        }
    }

    private sealed class ImmutableRecordingReporter : ProcessingEventReporter
    {
        public ConcurrentQueue<ProcessingEvent> Events { get; } = new();

        protected override ValueTask AcceptAsync(ProcessingEvent processingEvent, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Enqueue(processingEvent);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class GatedZeroOperations :
        IProcessingRunConfiguration,
        IProcessingAssetRepository,
        IProcessingSkippedStore,
        IProcessingAdministrativeResolver,
        IProcessingInfrastructureLookup,
        IProcessingRunDelay
    {
        public TaskCompletionSource CountEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<long> CountRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<ExecutorCallKind> Calls { get; } = new();

        public ProcessingRunExecutor CreateExecutor()
        {
            return new ProcessingRunExecutor(
                NullLogger<ProcessingRunExecutor>.Instance,
                this,
                this,
                this,
                this,
                this,
                this,
                new FixedUtcTimeProvider());
        }

        public async Task<long> GetUnprocessedCountAsync(CancellationToken cancellationToken = default)
        {
            Calls.Enqueue(ExecutorCallKind.Count);
            CountEntered.TrySetResult();
            return await CountRelease.Task.WaitAsync(Bound, cancellationToken).ConfigureAwait(false);
        }

        public Task<AppConfig> GetConfigAsync() => throw Unexpected("configuration");
        public Task<HashSet<Guid>> GetAllAsync() => throw Unexpected("skipped snapshot");
        public Task<List<AssetRecord>> GetUnprocessedBatchAsync(AssetCursor cursor, int batchSize, CancellationToken cancellationToken = default) => throw Unexpected("batch");
        public Task WriteLocationAsync(Guid assetId, GeoResult geoResult, CancellationToken cancellationToken = default) => throw Unexpected("write");
        public Task AddAsync(Guid assetId) => throw Unexpected("skipped insert");
        public Task<AdministrativeAreaResolution?> ResolveAsync(double latitude, double longitude, ProcessingConfig config, IProcessingRunEventSession session, CancellationToken cancellationToken = default) => throw Unexpected("resolver");
        public Task<OvertureInfrastructureLookupDiagnostics> FindNearestInfrastructureAsync(double latitude, double longitude, string? iso3, CancellationToken cancellationToken = default) => throw Unexpected("airport");
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => throw Unexpected("delay");

        private static AssertFailedException Unexpected(string operation) => new($"Unexpected {operation} operation.");
    }
}
