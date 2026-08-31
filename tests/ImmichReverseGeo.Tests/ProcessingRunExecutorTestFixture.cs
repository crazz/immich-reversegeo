using System.Collections.Concurrent;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Overture.Models;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Tests;

internal sealed class FixedUtcTimeProvider : TimeProvider
{
    internal static readonly DateTimeOffset Start = new(2026, 8, 31, 13, 52, 21, TimeSpan.Zero);
    internal static readonly DateTimeOffset End = Start.AddSeconds(1);
    private int _read;

    public override DateTimeOffset GetUtcNow()
    {
        return Interlocked.Increment(ref _read) == 1 ? Start : End;
    }
}

internal sealed class AsyncGate
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Entered => _entered.Task;
    public void Release() => _release.TrySetResult();

    public async Task EnterAsync(CancellationToken token)
    {
        _entered.TrySetResult();
        await _release.Task.WaitAsync(ExecutorFixture.Bound, token).ConfigureAwait(false);
    }
}

internal sealed record TestLogEntry(LogLevel Level, string Message, Exception? Exception);
internal sealed record RecordedReporterEvent(ProcessingEvent Event, CancellationToken Token, Guid? AssetId, long Sequence);

internal sealed class CaptureLogger : ILogger
{
    public ConcurrentQueue<TestLogEntry> Entries { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Entries.Enqueue(new TestLogEntry(logLevel, formatter(state, exception), exception));
    }
}

internal sealed class RecordingFaultReporter : IProcessingEventReporter
{
    private sealed class Adapter(RecordingFaultReporter owner) : ProcessingEventReporter
    {
        protected override ValueTask AcceptAsync(ProcessingEvent processingEvent, CancellationToken cancellationToken) =>
            owner.AcceptCoreAsync(processingEvent, cancellationToken);
    }

    private readonly Func<ProcessingEvent, CancellationToken, ValueTask>? _behavior;
    private readonly ProcessingEventReporter _adapter;
    private int _acceptedActivities;

    public RecordingFaultReporter(Func<ProcessingEvent, CancellationToken, ValueTask>? behavior = null)
    {
        _behavior = behavior;
        _adapter = new Adapter(this);
    }

    public bool SessionConstructed { get; private set; }
    public bool SessionReturned { get; private set; }
    public bool TerminalAttempted { get; private set; }
    public bool TerminalAccepted { get; private set; }
    public bool ActivitiesBalanced => Volatile.Read(ref _acceptedActivities) == 0;
    public async ValueTask<IProcessingRunEventSession> OpenRunAsync(ProcessingRunRequest request, DateTimeOffset startedAtUtc, CancellationToken cancellationToken = default)
    {
        try
        {
            var session = await _adapter.OpenRunAsync(request, startedAtUtc, cancellationToken).ConfigureAwait(false);
            SessionReturned = true;
            return session;
        }
        finally
        {
            SessionConstructed = true;
        }
    }

    public ConcurrentQueue<ProcessingEvent> Attempts { get; } = new();
    public ConcurrentQueue<ProcessingEvent> Events { get; } = new();
    public ConcurrentQueue<RecordedReporterEvent> AttemptObservations { get; } = new();
    public ConcurrentQueue<RecordedReporterEvent> EventObservations { get; } = new();
    private ConcurrentQueue<Exception> RejectedExceptions { get; } = new();
    public bool Rejected(Exception exception)
    {
        return RejectedExceptions.TryPeek(out var candidate) && ReferenceEquals(candidate, exception)
            && RejectedExceptions.TryDequeue(out _);
    }
    public void CompleteRejectedExceptions()
    {
        while (RejectedExceptions.TryDequeue(out _))
        {
        }
        Assert.AreEqual(0, RejectedExceptions.Count, "Reporter rejection references leaked beyond run.");
    }
    public Func<long> NextSequence { get; set; } = static () => 0;
    public Action<ProcessingEvent>? Accepted { get; set; }
    public Func<CancellationToken, Guid?>? TokenAssetResolver { get; set; }
    public ExecutorEventCorrelation Correlation { get; } = new();

    private async ValueTask AcceptCoreAsync(ProcessingEvent processingEvent, CancellationToken cancellationToken)
    {
        if (processingEvent is RunFinished)
        {
            TerminalAttempted = true;
        }
        Correlation.ObserveActivity(processingEvent);
        var observation = new RecordedReporterEvent(processingEvent, cancellationToken,
            TokenAssetResolver?.Invoke(cancellationToken), NextSequence());
        Attempts.Enqueue(processingEvent);
        AttemptObservations.Enqueue(observation);
        try
        {
            if (_behavior is not null)
            {
                await _behavior(processingEvent, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Events.Enqueue(processingEvent);
            if (processingEvent is RunFinished)
            {
                TerminalAccepted = true;
            }
            else if (processingEvent is ActivityStarted)
            {
                Interlocked.Increment(ref _acceptedActivities);
            }
            else if (processingEvent is ActivityEnded)
            {
                Interlocked.Decrement(ref _acceptedActivities);
            }
            EventObservations.Enqueue(observation with
            {
                AssetId = Correlation.TakeCorrelatedAsset(processingEvent) ?? observation.AssetId,
                Sequence = NextSequence()
            });
            Accepted?.Invoke(processingEvent);
        }
        catch (Exception exception)
        {
            RejectedExceptions.Enqueue(exception);
            Correlation.Reject(processingEvent);
            throw;
        }
    }
}

internal sealed class TestSinkException(string message) : Exception(message);

internal sealed class ExecutorFixture :
    IProcessingRunConfiguration,
    IProcessingAssetRepository,
    IProcessingSkippedStore,
    IProcessingAdministrativeResolver,
    IProcessingInfrastructureLookup,
    IProcessingRunDelay
{
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private readonly ConcurrentQueue<List<AssetRecord>> _pages = new();
    private long _sequence;
    private readonly SemaphoreSlim _dispositionAdmission = new(1, 1);
    private (Guid AssetId, string Outcome)? _pendingDisposition;

    public ExecutorFixture()
    {
        Logger = new CaptureLogger();
        Reporter = new RecordingFaultReporter((processingEvent, token) =>
            ValueTask.FromException(new AssertFailedException($"Unexpected reporter event {processingEvent.GetType().Name}.")));
        TimeProvider = new FixedUtcTimeProvider();
        Request = new ProcessingRunRequest(Guid.Parse("11111111-1111-1111-1111-111111111111"), ProcessingRunTrigger.Manual);
        Config = DefaultConfig();
        CountBehavior = token => Task.FromException<long>(Unexpected("count"));
        ConfigBehavior = () => Task.FromException<AppConfig>(Unexpected("configuration snapshot"));
        SkippedBehavior = () => Task.FromException<HashSet<Guid>>(Unexpected("skipped snapshot"));
        BatchBehavior = (cursor, size, call, token) => Task.FromException<List<AssetRecord>>(Unexpected("batch"));
        ResolveBehavior = (asset, session, token) => Task.FromException<AdministrativeAreaResolution?>(Unexpected($"admin {asset.Id}"));
        AirportBehavior = (asset, token) => Task.FromException<OvertureInfrastructureLookupDiagnostics>(Unexpected($"airport {asset.Id}"));
        WriteBehavior = (assetId, geo, token) => Task.FromException(Unexpected($"write {assetId}"));
        AddSkippedBehavior = assetId => Task.FromException(Unexpected($"skipped insert {assetId}"));
        DelayBehavior = (delay, token) => Task.FromException(Unexpected($"delay {delay}"));
        EventBehavior = (processingEvent, token) => ValueTask.FromException(Unexpected($"reporter event {processingEvent.GetType().Name}"));
        Executor = new ProcessingRunExecutor(Logger, this, this, this, this, this, this, TimeProvider);
    }

    public ProcessingRunExecutor Executor { get; }
    public ProcessingRunRequest Request { get; }
    public RecordingFaultReporter Reporter { get; set; }
    public CaptureLogger Logger { get; }
    public TimeProvider TimeProvider { get; }
    public long Eligible { get; set; } = 1;
    public AppConfig Config { get; set; }
    public HashSet<Guid> SkippedIds { get; set; } = [];
    public Func<CancellationToken, Task<long>>? CountBehavior { get; set; }
    public Func<Task<AppConfig>>? ConfigBehavior { get; set; }
    public Func<Task<HashSet<Guid>>>? SkippedBehavior { get; set; }
    public Func<AssetCursor, int, int, CancellationToken, Task<List<AssetRecord>>>? BatchBehavior { get; set; }
    public Func<AssetRecord, IProcessingRunEventSession, CancellationToken, Task<AdministrativeAreaResolution?>>? ResolveBehavior { get; set; }
    public Func<AssetRecord, CancellationToken, Task<OvertureInfrastructureLookupDiagnostics>>? AirportBehavior { get; set; }
    public Func<Guid, GeoResult, CancellationToken, Task>? WriteBehavior { get; set; }
    public Func<Guid, Task>? AddSkippedBehavior { get; set; }
    public Func<TimeSpan, CancellationToken, Task>? DelayBehavior { get; set; }
    public Func<ProcessingEvent, CancellationToken, ValueTask>? EventBehavior { get; set; }
    public ConcurrentQueue<ExecutorCallObservation> Calls { get; } = new();
    public ConcurrentQueue<(Guid AssetId, GeoResult Geo)> Writes { get; } = new();
    public ConcurrentQueue<Guid> SkippedWrites { get; } = new();
    public ConcurrentQueue<AssetCursor> Cursors { get; } = new();
    public ConcurrentQueue<int> BatchSizes { get; } = new();
    public ConcurrentQueue<TimeSpan> Delays { get; } = new();
    public ConcurrentQueue<(Guid AssetId, ProcessingConfig Config)> Resolutions { get; } = new();
    public ConcurrentQueue<Guid> AirportCalls { get; } = new();
    public ConcurrentQueue<Guid> FetchedAssets { get; } = new();
    public ConcurrentQueue<SeamExceptionObservation> SeamExceptions { get; } = new();
    public int MaximumActive { get; set; }
    public int CountCalls;
    public int ConfigCalls;
    public int SkippedCalls;
    public int BatchCalls;
    public int WriteAttempts;
    public int SkippedInsertAttempts;

    public ExecutorFixture EnableReporter()
    {
        EventBehavior = (processingEvent, token) => ValueTask.CompletedTask;
        Reporter = new RecordingFaultReporter(RecordEventAsync)
        {
            NextSequence = () => Interlocked.Increment(ref _sequence),
            TokenAssetResolver = token =>
            {
                var assets = Calls.Where(item => item.Call.AssetId.HasValue && item.Token == token)
                    .Select(item => item.Call.AssetId!.Value).Distinct().ToArray();
                return assets.Length == 1 ? assets[0] : null;
            },
            Accepted = processingEvent =>
            {
                if (processingEvent is ProgressChanged)
                {
                    _dispositionAdmission.Release();
                }
            }
        };
        return this;
    }

    public ExecutorFixture EnableCount(long eligible = 1)
    {
        Eligible = eligible;
        CountBehavior = token => Task.FromResult(Eligible);
        return this;
    }

    public ExecutorFixture EnableSnapshots()
    {
        ConfigBehavior = () => Task.FromResult(Clone(Config));
        SkippedBehavior = () => Task.FromResult(new HashSet<Guid>(SkippedIds));
        return this;
    }

    public ExecutorFixture EnablePages()
    {
        BatchBehavior = (cursor, size, call, token) => Task.FromResult(_pages.TryDequeue(out var page) ? page : []);
        return this;
    }

    public ExecutorFixture EnableAdmin()
    {
        ResolveBehavior = (asset, session, token) => Task.FromResult<AdministrativeAreaResolution?>(Resolution(new GeoResult("Country", "State", $"City-{(int)asset.Latitude}")));
        return this;
    }

    public ExecutorFixture EnableAirport()
    {
        AirportBehavior = (asset, token) => Task.FromResult(EmptyAirport());
        return this;
    }

    public ExecutorFixture EnableWrite()
    {
        WriteBehavior = (assetId, geo, token) => Task.CompletedTask;
        return this;
    }

    public ExecutorFixture EnableSkippedInsert()
    {
        AddSkippedBehavior = assetId => Task.CompletedTask;
        return this;
    }

    public ExecutorFixture EnableDelay()
    {
        DelayBehavior = (delay, token) => Task.CompletedTask;
        return this;
    }

    public void SetPages(params IReadOnlyList<AssetRecord>[] pages)
    {
        foreach (var page in pages)
        {
            _pages.Enqueue([.. page]);
        }
    }

    public async Task<ProcessingRunResult> ExecuteAsync(CancellationToken token = default)
    {
        var result = await Executor.ExecuteAsync(Request, Reporter, token).WaitAsync(Bound).ConfigureAwait(false);
        AssertTerminal(result);
        return result;
    }

    public ExecutorCaseObservation Observe(
        ProcessingRunResult? result,
        Exception? escapedException = null,
        Exception? expectedEscapedException = null,
        CancellationToken runToken = default,
        CancellationToken? foreignToken = null)
    {
        var effects = Writes.Select(item => new ExecutorEffectContract(ExecutorEffectKind.Write, item.AssetId, item.Geo.Country, item.Geo.State, item.Geo.City))
            .Concat(SkippedWrites.Select(id => new ExecutorEffectContract(ExecutorEffectKind.Skip, id, null, null, null)))
            .ToArray();
        var attempts = Reporter.AttemptObservations.Select(item => new ExecutorEventObservation(
            Reporter.Correlation.Create(item.Event, Request, result), item.Token, item.AssetId, item.Sequence)).ToArray();
        var events = Reporter.EventObservations.Select(item => new ExecutorEventObservation(
            Reporter.Correlation.Create(item.Event, Request, result), item.Token, item.AssetId, item.Sequence)).ToArray();
        var dispositions = events.Where(item => item.Event.Kind == ExecutorEventKind.ProgressChanged).Select(item =>
        {
            Assert.IsTrue(item.AssetId.HasValue, "Every disposition must carry its asset identity.");
            var progress = item.Event;
            var previous = events.TakeWhile(candidate => !ReferenceEquals(candidate, item))
                .Where(candidate => candidate.Event.Kind == ExecutorEventKind.ProgressChanged)
                .Select(candidate => candidate.Event).LastOrDefault();
            var outcome = progress.UpdatedCount > (previous?.UpdatedCount ?? 0) ? "Updated"
                : progress.SkippedCount > (previous?.SkippedCount ?? 0) ? "Skipped" : "Failed";
            return new ExecutorDispositionObservation(item.AssetId.Value, outcome, progress.ProcessedCount!.Value,
                progress.UpdatedCount!.Value, progress.SkippedCount!.Value, progress.FailedCount!.Value, item.Sequence);
        }).ToArray();
        return new ExecutorCaseObservation(
            Request, result, escapedException, expectedEscapedException, Calls.ToArray(), effects, attempts, events,
            Logger.Entries.Select(item => new ExecutorLogContract("ILogger", item.Level.ToString(), item.Message,
                item.Exception?.GetType().FullName, item.Exception?.Message)).ToArray(),
            FetchedAssets.ToArray(), dispositions, SeamExceptions.ToArray(), runToken, foreignToken, MaximumActive,
            new ExecutorCleanupObservation(Reporter.SessionConstructed, Reporter.SessionReturned, Reporter.TerminalAttempted,
                Reporter.TerminalAccepted, Reporter.ActivitiesBalanced));
    }

    public void Verify(
        string caseId,
        ProcessingRunResult? result,
        Exception? escapedException = null,
        Exception? expectedEscapedException = null,
        CancellationToken runToken = default,
        CancellationToken? foreignToken = null)
    {
        ExecutorCaseContractEngine.Verify(caseId, Observe(result, escapedException, expectedEscapedException, runToken, foreignToken));
        Reporter.Correlation.Complete();
        Assert.AreEqual(0, Reporter.Correlation.PendingCount, caseId);
        Assert.IsFalse(_pendingDisposition.HasValue, caseId + " pending disposition correlation");
        Reporter.CompleteRejectedExceptions();
    }

    public void AssertTerminal(ProcessingRunResult result)
    {
        Assert.AreSame(Request, result.Request);
        Assert.AreEqual(result.ProcessedCount, result.UpdatedCount + result.SkippedCount + result.FailedCount);
        Assert.AreEqual(TimeSpan.Zero, result.StartedAtUtc.Offset);
        Assert.AreEqual(TimeSpan.Zero, result.EndedAtUtc.Offset);
        Assert.IsTrue(result.EndedAtUtc >= result.StartedAtUtc);
        var finished = Reporter.Events.OfType<RunFinished>().ToArray();
        Assert.AreEqual(1, finished.Length);
        Assert.AreSame(result, finished[0].Result);
        Assert.IsTrue(Reporter.Events.All(item => ReferenceEquals(Request, item.Request)));
        Assert.IsInstanceOfType<RunFinished>(Reporter.Events.Last());
    }

    public static AssetRecord Asset(int index)
    {
        var id = index switch
        {
            1 => Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa1"),
            2 => Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbb2"),
            3 => Guid.Parse("cccccccc-cccc-cccc-cccc-ccccccccccc3"),
            4 => Guid.Parse("dddddddd-dddd-dddd-dddd-ddddddddddd4"),
            _ => Guid.Parse($"00000000-0000-0000-0000-{index:D12}")
        };
        return new AssetRecord(id, index, index, DateTime.UnixEpoch.AddSeconds(index));
    }

    public static AdministrativeAreaResolution Resolution(GeoResult geo)
    {
        return new AdministrativeAreaResolution("USA", "US", "Country", geo, null, null);
    }

    public static OvertureInfrastructureLookupDiagnostics EmptyAirport()
    {
        return new OvertureInfrastructureLookupDiagnostics(null, [], "test");
    }

    public static OvertureInfrastructureLookupDiagnostics Airport(string name, bool contains)
    {
        return new OvertureInfrastructureLookupDiagnostics(
            new OvertureInfrastructureResult("id", name, null, null, null, 1, contains, contains, []),
            [],
            "test");
    }

    public static AppConfig DefaultConfig()
    {
        return new AppConfig
        {
            Processing = new ProcessingConfig
            {
                BatchSize = 50,
                BatchDelayMs = 0,
                MaxDegreeOfParallelism = 1,
                UseAirportInfrastructure = false
            }
        };
    }

    public Task<long> GetUnprocessedCountAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref CountCalls);
        RecordCall(new ExecutorCallContract(ExecutorCallKind.Count), cancellationToken);
        return ObserveExceptionAsync(ExecutorCallKind.Count, null, () => CountBehavior!(cancellationToken));
    }

    public Task<AppConfig> GetConfigAsync()
    {
        Interlocked.Increment(ref ConfigCalls);
        RecordCall(new ExecutorCallContract(ExecutorCallKind.ConfigurationSnapshot));
        return ObserveExceptionAsync(ExecutorCallKind.ConfigurationSnapshot, null, ConfigBehavior!);
    }

    public Task<HashSet<Guid>> GetAllAsync()
    {
        Interlocked.Increment(ref SkippedCalls);
        RecordCall(new ExecutorCallContract(ExecutorCallKind.SkippedSnapshot));
        return ObserveExceptionAsync(ExecutorCallKind.SkippedSnapshot, null, SkippedBehavior!);
    }

    public async Task<List<AssetRecord>> GetUnprocessedBatchAsync(AssetCursor cursor, int batchSize, CancellationToken cancellationToken = default)
    {
        var index = Interlocked.Increment(ref BatchCalls);
        Cursors.Enqueue(cursor);
        BatchSizes.Enqueue(batchSize);
        RecordCall(new ExecutorCallContract(ExecutorCallKind.Batch, index, cursor.CreatedAt, cursor.Id, batchSize), cancellationToken);
        var batch = await ObserveExceptionAsync(ExecutorCallKind.Batch, null, () => BatchBehavior!(cursor, batchSize, index, cancellationToken)).ConfigureAwait(false);
        foreach (var asset in batch)
        {
            FetchedAssets.Enqueue(asset.Id);
        }
        return batch;
    }

    public async Task<AdministrativeAreaResolution?> ResolveAsync(double latitude, double longitude, ProcessingConfig config, IProcessingRunEventSession session, CancellationToken cancellationToken = default)
    {
        var asset = Asset((int)latitude);
        Resolutions.Enqueue((asset.Id, config));
        RecordCall(new ExecutorCallContract(ExecutorCallKind.Admin, AssetId: asset.Id), cancellationToken);
        return await ObserveExceptionAsync(ExecutorCallKind.Admin, asset.Id, () => ResolveBehavior!(asset, session, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public async Task<OvertureInfrastructureLookupDiagnostics> FindNearestInfrastructureAsync(double latitude, double longitude, string? iso3, CancellationToken cancellationToken = default)
    {
        var asset = Asset((int)latitude);
        AirportCalls.Enqueue(asset.Id);
        RecordCall(new ExecutorCallContract(ExecutorCallKind.Airport, AssetId: asset.Id), cancellationToken);
        return await ObserveExceptionAsync(ExecutorCallKind.Airport, asset.Id, () => AirportBehavior!(asset, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteLocationAsync(Guid assetId, GeoResult geoResult, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref WriteAttempts);
        RecordCall(new ExecutorCallContract(ExecutorCallKind.WriteAttempt, AssetId: assetId), cancellationToken);
        await ObserveExceptionAsync(ExecutorCallKind.WriteAttempt, assetId, () => WriteBehavior!(assetId, geoResult, cancellationToken), cancellationToken).ConfigureAwait(false);

        Writes.Enqueue((assetId, geoResult));
        await RecordPendingDispositionAsync(assetId, "Updated").ConfigureAwait(false);
        RecordCall(new ExecutorCallContract(ExecutorCallKind.WriteAccepted, AssetId: assetId));
    }

    public async Task AddAsync(Guid assetId)
    {
        Interlocked.Increment(ref SkippedInsertAttempts);
        RecordCall(new ExecutorCallContract(ExecutorCallKind.SkipAttempt, AssetId: assetId));
        await ObserveExceptionAsync(ExecutorCallKind.SkipAttempt, assetId, () => AddSkippedBehavior!(assetId)).ConfigureAwait(false);

        SkippedWrites.Enqueue(assetId);
        await RecordPendingDispositionAsync(assetId, "Skipped").ConfigureAwait(false);
        RecordCall(new ExecutorCallContract(ExecutorCallKind.SkipAccepted, AssetId: assetId));
    }

    public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        Delays.Enqueue(delay);
        RecordCall(new ExecutorCallContract(ExecutorCallKind.Delay, DelayMs: delay.TotalMilliseconds), cancellationToken);
        await ObserveExceptionAsync(ExecutorCallKind.Delay, null, () => DelayBehavior!(delay, cancellationToken)).ConfigureAwait(false);
    }

    private void RecordCall(ExecutorCallContract call, CancellationToken? token = null)
    {
        Calls.Enqueue(new ExecutorCallObservation(call, token, Interlocked.Increment(ref _sequence)));
    }

    private async ValueTask RecordEventAsync(ProcessingEvent processingEvent, CancellationToken token)
    {
        if (processingEvent is ProgressChanged)
        {
            Assert.IsTrue(_pendingDisposition.HasValue, "Progress event has no causally completed asset disposition.");
            var pending = _pendingDisposition.Value;
            var progress = ((ProgressChanged)processingEvent).Progress;
            var prior = Reporter.EventObservations.Where(item => item.Event is ProgressChanged)
                .Select(item => ((ProgressChanged)item.Event).Progress).LastOrDefault();
            var outcome = progress.UpdatedCount > (prior?.UpdatedCount ?? 0) ? "Updated"
                : progress.SkippedCount > (prior?.SkippedCount ?? 0) ? "Skipped" : "Failed";
            Assert.AreEqual(pending.Outcome, outcome, "Causal disposition outcome mismatch.");
            _pendingDisposition = null;
            Reporter.Correlation.Correlate(processingEvent, pending.AssetId);
        }
        else if (token != CancellationToken.None)
        {
            var assetIds = Calls.Where(item => item.Call.AssetId.HasValue && item.Token == token)
                .Select(item => item.Call.AssetId!.Value).Distinct().ToArray();
            if (assetIds.Length == 1)
            {
                Reporter.Correlation.Correlate(processingEvent, assetIds[0]);
            }
        }
        await EventBehavior!(processingEvent, token).ConfigureAwait(false);
    }

    private async Task<T> ObserveExceptionAsync<T>(ExecutorCallKind kind, Guid? assetId, Func<Task<T>> operation, CancellationToken? ownerToken = null)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            SeamExceptions.Enqueue(new SeamExceptionObservation(kind, assetId, exception));
            if (IsHandledAssetFailure(assetId, exception, ownerToken))
            {
                await RecordPendingDispositionAsync(assetId!.Value, "Failed").ConfigureAwait(false);
            }
            throw;
        }
    }

    private async Task ObserveExceptionAsync(ExecutorCallKind kind, Guid? assetId, Func<Task> operation, CancellationToken? ownerToken = null)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            SeamExceptions.Enqueue(new SeamExceptionObservation(kind, assetId, exception));
            if (IsHandledAssetFailure(assetId, exception, ownerToken))
            {
                await RecordPendingDispositionAsync(assetId!.Value, "Failed").ConfigureAwait(false);
            }
            throw;
        }
    }

    private async Task RecordPendingDispositionAsync(Guid assetId, string outcome)
    {
        await _dispositionAdmission.WaitAsync(Bound).ConfigureAwait(false);
        Assert.IsFalse(_pendingDisposition.HasValue, "Disposition admission gate admitted overlapping identities.");
        _pendingDisposition = (assetId, outcome);
    }

    private bool IsHandledAssetFailure(Guid? assetId, Exception exception, CancellationToken? ownerToken)
    {
        return assetId.HasValue
            && exception is not OutOfMemoryException
            && exception is not ProcessingEventReportingException
            && !Reporter.Rejected(exception)
            && (exception is not OperationCanceledException cancelled
                || !(ownerToken?.IsCancellationRequested ?? cancelled.CancellationToken.IsCancellationRequested));
    }

    private static AssertFailedException Unexpected(string seam)
    {
        return new AssertFailedException($"Unexpected executor collaborator seam: {seam}.");
    }

    internal static AppConfig Clone(AppConfig source)
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
                Enabled = source.Schedule.Enabled,
                Cron = source.Schedule.Cron
            }
        };
    }
}

internal static class ExecutorAssertions
{
    public static void Counts(ProcessingRunResult result, long processed, long updated, long skipped, long failed)
    {
        Assert.AreEqual(processed, result.ProcessedCount);
        Assert.AreEqual(updated, result.UpdatedCount);
        Assert.AreEqual(skipped, result.SkippedCount);
        Assert.AreEqual(failed, result.FailedCount);
    }

    public static void Completed(ProcessingRunResult result, long processed, long updated, long skipped, long failed)
    {
        Assert.AreEqual(ProcessingRunOutcome.Completed, result.Outcome);
        Assert.IsNull(result.FailureMessage);
        Counts(result, processed, updated, skipped, failed);
    }
}
