using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using ImmichReverseGeo.Web.Composition;
using ImmichReverseGeo.Web.ProcessingRunLocking;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerHost.WorkerNdjsonOutput;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ImmichReverseGeo.Tests.ProcessingRunLocking;

[TestClass]
[TestCategory("Change31")]
public sealed class ProcessingRunLockExecutorTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task ExecuteAsync_AcquisitionGate_FollowsRunStartedAndPrecedesEligibility()
    {
        ExecutorFixture fixture = new ExecutorFixture().EnableReporter().EnableCount(0);
        TaskCompletionSource gateEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingRunLockLease lease = new();
        RecordingRunLock runLock = new(async token =>
        {
            Assert.AreEqual(1, fixture.Reporter.Events.OfType<RunStarted>().Count());
            Assert.AreEqual(0, fixture.CountCalls);
            gateEntered.TrySetResult();
            await releaseGate.Task.WaitAsync(token).ConfigureAwait(false);
            return new ProcessingRunLockAcquisition.Acquired(lease);
        });
        WorkerProcessExitOutcomeAccumulator outcomes = new();
        ProcessingRunExecutor executor = CreateExecutor(fixture, runLock, outcomes);

        Task<ProcessingRunResult> execution = executor.ExecuteAsync(
            fixture.Request,
            fixture.Reporter,
            CancellationToken.None);
        await gateEntered.Task.WaitAsync(Bound);

        Assert.AreEqual(0, fixture.CountCalls);
        Assert.AreEqual(0, fixture.Reporter.Events.OfType<EligibilityDetermined>().Count());
        releaseGate.TrySetResult();
        ProcessingRunResult result = await execution.WaitAsync(Bound);

        Assert.AreEqual(ProcessingRunOutcome.Completed, result.Outcome);
        Assert.AreEqual(1, fixture.CountCalls);
        Assert.AreEqual(1, lease.ReleaseCalls);
        Assert.IsFalse(outcomes.HasFact);
        CollectionAssert.AreEqual(
            new[] { typeof(RunStarted), typeof(EligibilityDetermined), typeof(RunFinished) },
            fixture.Reporter.Events.Select(item => item.GetType()).ToArray());
    }

    [TestMethod]
    public async Task ExecuteAsync_Busy_ReturnsZeroCountFailedTerminalAndBusyFact()
    {
        ExecutorFixture fixture = new ExecutorFixture().EnableReporter();
        RecordingRunLock runLock = new(_ =>
            ValueTask.FromResult<ProcessingRunLockAcquisition>(new ProcessingRunLockAcquisition.Busy()));
        WorkerProcessExitOutcomeAccumulator outcomes = new();

        ProcessingRunResult result = await CreateExecutor(fixture, runLock, outcomes)
            .ExecuteAsync(fixture.Request, fixture.Reporter, CancellationToken.None)
            .WaitAsync(Bound);

        AssertGateFailure(result, ProcessingRunOutcome.Failed);
        Assert.AreEqual("Another Immich ReverseGeo worker is already processing this database.", result.FailureMessage);
        Assert.AreSame(WorkerProcessExitFact.Busy(), outcomes.Fact);
        Assert.AreEqual(3, outcomes.Fact.ExitCode);
        AssertOneStartedAndFinishedWithoutEligibility(fixture);
    }

    [TestMethod]
    public async Task ExecuteAsync_CancelledAcquisition_ReturnsZeroCountCancelledTerminalAndShutdownFact()
    {
        ExecutorFixture fixture = new ExecutorFixture().EnableReporter();
        RecordingRunLock runLock = new(_ =>
            ValueTask.FromResult<ProcessingRunLockAcquisition>(new ProcessingRunLockAcquisition.Cancelled()));
        WorkerProcessExitOutcomeAccumulator outcomes = new();

        ProcessingRunResult result = await CreateExecutor(fixture, runLock, outcomes)
            .ExecuteAsync(fixture.Request, fixture.Reporter, CancellationToken.None)
            .WaitAsync(Bound);

        AssertGateFailure(result, ProcessingRunOutcome.Cancelled);
        Assert.IsNull(result.FailureMessage);
        Assert.AreSame(WorkerProcessExitFact.ShutdownCancelled(), outcomes.Fact);
        Assert.AreEqual(130, outcomes.Fact.ExitCode);
        AssertOneStartedAndFinishedWithoutEligibility(fixture);
    }

    [TestMethod]
    public async Task ExecuteAsync_InfrastructureAcquisition_ReturnsSafeZeroCountFailedTerminalAndFact()
    {
        ExecutorFixture fixture = new ExecutorFixture().EnableReporter();
        RecordingRunLock runLock = new(_ =>
            ValueTask.FromResult<ProcessingRunLockAcquisition>(new ProcessingRunLockAcquisition.InfrastructureFailure()));
        WorkerProcessExitOutcomeAccumulator outcomes = new();

        ProcessingRunResult result = await CreateExecutor(fixture, runLock, outcomes)
            .ExecuteAsync(fixture.Request, fixture.Reporter, CancellationToken.None)
            .WaitAsync(Bound);

        AssertGateFailure(result, ProcessingRunOutcome.Failed);
        Assert.AreEqual("The database run lock could not be acquired.", result.FailureMessage);
        Assert.AreSame(WorkerProcessExitFact.ExecutionInfrastructure(), outcomes.Fact);
        Assert.AreEqual(5, outcomes.Fact.ExitCode);
        AssertOneStartedAndFinishedWithoutEligibility(fixture);
    }

    [TestMethod]
    public async Task ExecuteAsync_AcquisitionOutOfMemory_RemainsFatalWithoutTerminal()
    {
        OutOfMemoryException failure = new("controlled acquisition oom");
        ExecutorFixture fixture = new ExecutorFixture().EnableReporter();
        RecordingRunLock runLock = new(_ =>
            ValueTask.FromException<ProcessingRunLockAcquisition>(failure));
        WorkerProcessExitOutcomeAccumulator outcomes = new();

        OutOfMemoryException thrown = await Assert.ThrowsExactlyAsync<OutOfMemoryException>(() =>
            CreateExecutor(fixture, runLock, outcomes)
                .ExecuteAsync(fixture.Request, fixture.Reporter, CancellationToken.None)
                .WaitAsync(Bound));

        Assert.AreSame(failure, thrown);
        Assert.AreEqual(1, fixture.Reporter.Events.OfType<RunStarted>().Count());
        Assert.AreEqual(0, fixture.Reporter.Events.OfType<RunFinished>().Count());
        Assert.IsFalse(outcomes.HasFact);
    }

    [TestMethod]
    public async Task ExecuteAsync_AcquiredFullRun_UsesLinkedTokenAndReleasesAfterUpdate()
    {
        ExecutorFixture fixture = new ExecutorFixture()
            .EnableReporter()
            .EnableCount(1)
            .EnableSnapshots()
            .EnablePages()
            .EnableAdmin()
            .EnableWrite();
        fixture.SetPages([ExecutorFixture.Asset(1)], []);
        RecordingRunLockLease lease = new();
        RecordingRunLock runLock = RecordingRunLock.Acquired(lease);
        WorkerProcessExitOutcomeAccumulator outcomes = new();

        ProcessingRunResult result = await CreateExecutor(fixture, runLock, outcomes)
            .ExecuteAsync(fixture.Request, fixture.Reporter, CancellationToken.None)
            .WaitAsync(Bound);

        Assert.AreEqual(ProcessingRunOutcome.Completed, result.Outcome);
        Assert.AreEqual(1L, result.ProcessedCount);
        Assert.AreEqual(1L, result.UpdatedCount);
        Assert.AreEqual(1, fixture.WriteAttempts);
        Assert.AreEqual(1, lease.ReleaseCalls);
        Assert.IsFalse(outcomes.HasFact);
    }

    [TestMethod]
    public async Task ExecuteAsync_CancelledAfterAcceptedUpdate_RetainsCompletedCounts()
    {
        using CancellationTokenSource cancellation = new();
        AssetRecord asset = ExecutorFixture.Asset(1);
        ExecutorFixture fixture = new ExecutorFixture()
            .EnableReporter()
            .EnableCount(1)
            .EnableSnapshots()
            .EnablePages()
            .EnableAdmin()
            .EnableWrite();
        fixture.BatchBehavior = (_, _, call, token) =>
        {
            if (call == 1)
            {
                return Task.FromResult(new List<AssetRecord> { asset });
            }

            cancellation.Cancel();
            return Task.FromCanceled<List<AssetRecord>>(token);
        };
        RecordingRunLockLease lease = new();
        WorkerProcessExitOutcomeAccumulator outcomes = new();

        ProcessingRunResult result = await CreateExecutor(
                fixture,
                RecordingRunLock.Acquired(lease),
                outcomes)
            .ExecuteAsync(fixture.Request, fixture.Reporter, cancellation.Token)
            .WaitAsync(Bound);

        Assert.AreEqual(ProcessingRunOutcome.Cancelled, result.Outcome);
        Assert.AreEqual(1L, result.ProcessedCount);
        Assert.AreEqual(1L, result.UpdatedCount);
        Assert.AreEqual(0L, result.SkippedCount);
        Assert.AreEqual(0L, result.FailedCount);
        Assert.AreEqual(1, fixture.WriteAttempts);
        Assert.AreEqual(1, fixture.Reporter.Events.OfType<RunFinished>().Count());
        Assert.AreSame(WorkerProcessExitFact.ShutdownCancelled(), outcomes.Fact);
        Assert.AreEqual(1, lease.ReleaseCalls);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExecuteAsync_StopAtSuccessfulWriteCompletion_RetainsUpdateAndStartsNoFurtherWork(
        bool ownershipLost)
    {
        using CancellationTokenSource cancellation = new();
        AssetRecord first = ExecutorFixture.Asset(1);
        AssetRecord second = ExecutorFixture.Asset(2);
        RecordingRunLockLease lease = new();
        ExecutorFixture fixture = new ExecutorFixture()
            .EnableReporter()
            .EnableCount(2)
            .EnableSnapshots()
            .EnablePages()
            .EnableAdmin()
            .EnableWrite();
        fixture.SetPages([first, second], []);
        fixture.WriteBehavior = (assetId, geo, token) =>
        {
            Assert.AreEqual(first.Id, assetId);
            if (ownershipLost)
            {
                lease.LoseOwnership();
            }
            else
            {
                cancellation.Cancel();
            }
            return Task.CompletedTask;
        };
        WorkerProcessExitOutcomeAccumulator outcomes = new();

        ProcessingRunResult result = await CreateExecutor(
                fixture,
                RecordingRunLock.Acquired(lease),
                outcomes)
            .ExecuteAsync(fixture.Request, fixture.Reporter, cancellation.Token)
            .WaitAsync(Bound);

        Assert.AreEqual(
            ownershipLost ? ProcessingRunOutcome.Failed : ProcessingRunOutcome.Cancelled,
            result.Outcome);
        Assert.AreEqual(1L, result.ProcessedCount);
        Assert.AreEqual(1L, result.UpdatedCount);
        Assert.AreEqual(0L, result.SkippedCount);
        Assert.AreEqual(0L, result.FailedCount);
        Assert.AreEqual(1, fixture.WriteAttempts);
        Assert.AreEqual(first.Id, fixture.Writes.Single().AssetId);
        Assert.AreEqual(first.Id, fixture.Resolutions.Single().AssetId);
        Assert.AreEqual(1, fixture.Reporter.Events.OfType<ProgressChanged>().Count());
        Assert.AreEqual(ownershipLost ? 5 : 130, outcomes.Fact.ExitCode);
        Assert.AreEqual(1, lease.ReleaseCalls);
    }

    [TestMethod]
    [DataRow("no-country")]
    [DataRow("no-admin-match")]
    public async Task ExecuteAsync_OwnershipLostAtSuccessfulSkipPersistence_RetainsSkipAndStartsNoFurtherWork(
        string scenario)
    {
        AssetRecord first = ExecutorFixture.Asset(1);
        AssetRecord second = ExecutorFixture.Asset(2);
        RecordingRunLockLease lease = new();
        ExecutorFixture fixture = new ExecutorFixture()
            .EnableReporter()
            .EnableCount(2)
            .EnableSnapshots()
            .EnablePages()
            .EnableAdmin()
            .EnableSkippedInsert();
        fixture.SetPages([first, second], []);
        fixture.ResolveBehavior = (asset, session, token) => scenario == "no-country"
            ? Task.FromResult<AdministrativeAreaResolution?>(null)
            : Task.FromResult<AdministrativeAreaResolution?>(
                ExecutorFixture.Resolution(new GeoResult(null, null, null)));
        fixture.AddSkippedBehavior = assetId =>
        {
            Assert.AreEqual(first.Id, assetId);
            lease.LoseOwnership();
            return Task.CompletedTask;
        };
        WorkerProcessExitOutcomeAccumulator outcomes = new();

        ProcessingRunResult result = await CreateExecutor(
                fixture,
                RecordingRunLock.Acquired(lease),
                outcomes)
            .ExecuteAsync(fixture.Request, fixture.Reporter, CancellationToken.None)
            .WaitAsync(Bound);

        Assert.AreEqual(ProcessingRunOutcome.Failed, result.Outcome);
        Assert.AreEqual("The database run lock was lost during processing.", result.FailureMessage);
        Assert.AreEqual(1L, result.ProcessedCount);
        Assert.AreEqual(0L, result.UpdatedCount);
        Assert.AreEqual(1L, result.SkippedCount);
        Assert.AreEqual(0L, result.FailedCount);
        Assert.AreEqual(1, fixture.SkippedInsertAttempts);
        Assert.AreEqual(first.Id, fixture.SkippedWrites.Single());
        Assert.AreEqual(first.Id, fixture.Resolutions.Single().AssetId);
        Assert.AreEqual(1, fixture.Reporter.Events.OfType<ProgressChanged>().Count());
        Assert.AreEqual(5, outcomes.Fact.ExitCode);
        Assert.AreEqual(1, lease.ReleaseCalls);
    }

    [TestMethod]
    public async Task ExecuteAsync_ManagedOutOfMemoryAfterAcceptedUpdate_EmitsFailedTerminalWithRetainedCounts()
    {
        AssetRecord first = ExecutorFixture.Asset(1);
        AssetRecord second = ExecutorFixture.Asset(2);
        OutOfMemoryException failure = new("controlled domain oom");
        int resolveCalls = 0;
        ExecutorFixture fixture = new ExecutorFixture()
            .EnableReporter()
            .EnableCount(2)
            .EnableSnapshots()
            .EnablePages()
            .EnableAdmin()
            .EnableWrite();
        fixture.SetPages([first, second], []);
        var resolveNormally = fixture.ResolveBehavior!;
        fixture.ResolveBehavior = async (asset, session, token) =>
        {
            if (Interlocked.Increment(ref resolveCalls) == 1)
            {
                return await resolveNormally(asset, session, token).ConfigureAwait(false);
            }

            throw failure;
        };
        RecordingRunLockLease lease = new();

        ProcessingRunResult result = await CreateExecutor(
                fixture,
                RecordingRunLock.Acquired(lease),
                new WorkerProcessExitOutcomeAccumulator())
            .ExecuteAsync(fixture.Request, fixture.Reporter, CancellationToken.None)
            .WaitAsync(Bound);

        Assert.AreEqual(ProcessingRunOutcome.Failed, result.Outcome);
        Assert.AreEqual(failure.Message, result.FailureMessage);
        Assert.AreEqual(1L, result.ProcessedCount);
        Assert.AreEqual(1L, result.UpdatedCount);
        Assert.AreEqual(0L, result.SkippedCount);
        Assert.AreEqual(0L, result.FailedCount);
        Assert.AreEqual(1, fixture.WriteAttempts);
        RunFinished terminal = fixture.Reporter.Events.OfType<RunFinished>().Single();
        Assert.AreEqual(ProcessingRunOutcome.Failed, terminal.Result.Outcome);
        Assert.AreEqual(1L, terminal.Result.ProcessedCount);
        Assert.AreEqual(1, lease.ReleaseCalls);
    }

    [TestMethod]
    public async Task ExecuteAsync_OwnershipLostDuringResolverThatReturnsNormally_DoesNotWrite()
    {
        AssetRecord asset = ExecutorFixture.Asset(1);
        RecordingRunLockLease lease = new();
        ExecutorFixture fixture = new ExecutorFixture()
            .EnableReporter()
            .EnableCount(1)
            .EnableSnapshots()
            .EnablePages()
            .EnableAdmin()
            .EnableWrite();
        fixture.SetPages([asset], []);
        var resolveNormally = fixture.ResolveBehavior!;
        fixture.ResolveBehavior = async (resolvedAsset, session, token) =>
        {
            var resolution = await resolveNormally(resolvedAsset, session, token).ConfigureAwait(false);
            lease.LoseOwnership();
            return resolution;
        };
        WorkerProcessExitOutcomeAccumulator outcomes = new();

        ProcessingRunResult result = await CreateExecutor(
                fixture,
                RecordingRunLock.Acquired(lease),
                outcomes)
            .ExecuteAsync(fixture.Request, fixture.Reporter, CancellationToken.None)
            .WaitAsync(Bound);

        AssertGateFailure(result, ProcessingRunOutcome.Failed);
        Assert.AreEqual("The database run lock was lost during processing.", result.FailureMessage);
        Assert.AreEqual(0, fixture.WriteAttempts);
        Assert.AreEqual(5, outcomes.Fact.ExitCode);
        Assert.AreEqual(1, fixture.Reporter.Events.OfType<RunFinished>().Count());
        Assert.AreEqual(1, lease.ReleaseCalls);
    }

    [TestMethod]
    public async Task ExecuteAsync_CallerCancellationWhileHeld_ReturnsCancelledAndReleases()
    {
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource countEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ExecutorFixture fixture = new ExecutorFixture().EnableReporter();
        fixture.CountBehavior = async token =>
        {
            countEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return 0;
        };
        RecordingRunLockLease lease = new();
        WorkerProcessExitOutcomeAccumulator outcomes = new();
        Task<ProcessingRunResult> execution = CreateExecutor(
                fixture,
                RecordingRunLock.Acquired(lease),
                outcomes)
            .ExecuteAsync(fixture.Request, fixture.Reporter, cancellation.Token);

        await countEntered.Task.WaitAsync(Bound);
        cancellation.Cancel();
        ProcessingRunResult result = await execution.WaitAsync(Bound);

        Assert.AreEqual(ProcessingRunOutcome.Cancelled, result.Outcome);
        Assert.AreEqual(1, lease.ReleaseCalls);
        Assert.AreSame(WorkerProcessExitFact.ShutdownCancelled(), outcomes.Fact);
    }

    [TestMethod]
    public async Task ExecuteAsync_OwnershipLossRacingCallerCancellation_ReturnsSafeInfrastructureFailure()
    {
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource countEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ExecutorFixture fixture = new ExecutorFixture().EnableReporter();
        fixture.CountBehavior = async token =>
        {
            countEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return 0;
        };
        RecordingRunLockLease lease = new();
        WorkerProcessExitOutcomeAccumulator outcomes = new();
        Task<ProcessingRunResult> execution = CreateExecutor(
                fixture,
                RecordingRunLock.Acquired(lease),
                outcomes)
            .ExecuteAsync(fixture.Request, fixture.Reporter, cancellation.Token);

        await countEntered.Task.WaitAsync(Bound);
        lease.LoseOwnership();
        cancellation.Cancel();
        ProcessingRunResult result = await execution.WaitAsync(Bound);

        Assert.AreEqual(ProcessingRunOutcome.Failed, result.Outcome);
        Assert.AreEqual("The database run lock was lost during processing.", result.FailureMessage);
        Assert.AreEqual(5, outcomes.Fact.ExitCode);
        Assert.AreEqual(1, lease.ReleaseCalls);
    }

    [TestMethod]
    public async Task ExecuteAsync_LeaseAlreadyLostWhenAcquired_DoesNotStartDomainWork()
    {
        ExecutorFixture fixture = new ExecutorFixture().EnableReporter();
        RecordingRunLockLease lease = new();
        lease.LoseOwnership();
        WorkerProcessExitOutcomeAccumulator outcomes = new();

        ProcessingRunResult result = await CreateExecutor(
                fixture,
                RecordingRunLock.Acquired(lease),
                outcomes)
            .ExecuteAsync(fixture.Request, fixture.Reporter, CancellationToken.None)
            .WaitAsync(Bound);

        AssertGateFailure(result, ProcessingRunOutcome.Failed);
        Assert.AreEqual("The database run lock was lost during processing.", result.FailureMessage);
        Assert.AreEqual(0, fixture.CountCalls);
        Assert.AreEqual(5, outcomes.Fact.ExitCode);
    }

    [TestMethod]
    public async Task ExecuteAsync_TerminalIsGated_LeaseRemainsHeldUntilTerminalCompletes()
    {
        TaskCompletionSource terminalEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseTerminal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ExecutorFixture fixture = new ExecutorFixture().EnableReporter().EnableCount(0);
        fixture.EventBehavior = async (processingEvent, _) =>
        {
            if (processingEvent is RunFinished)
            {
                terminalEntered.TrySetResult();
                await releaseTerminal.Task.ConfigureAwait(false);
            }
        };
        RecordingRunLockLease lease = new();
        Task<ProcessingRunResult> execution = CreateExecutor(
                fixture,
                RecordingRunLock.Acquired(lease),
                new WorkerProcessExitOutcomeAccumulator())
            .ExecuteAsync(fixture.Request, fixture.Reporter, CancellationToken.None);

        await terminalEntered.Task.WaitAsync(Bound);
        Assert.AreEqual(0, lease.ReleaseCalls);
        releaseTerminal.TrySetResult();
        ProcessingRunResult result = await execution.WaitAsync(Bound);

        Assert.AreEqual(ProcessingRunOutcome.Completed, result.Outcome);
        Assert.AreEqual(1, lease.ReleaseCalls);
    }

    [TestMethod]
    public async Task ExecuteAsync_TerminalOutputFailure_StillReleasesAndOutputFactWins()
    {
        TestSinkException failure = new("terminal output failed");
        WorkerProcessExitOutcomeAccumulator outcomes = new();
        ExecutorFixture fixture = new ExecutorFixture().EnableReporter().EnableCount(0);
        fixture.EventBehavior = (processingEvent, _) =>
        {
            if (processingEvent is RunFinished)
            {
                outcomes.Add(WorkerProcessExitFact.OutputTransport());
                return ValueTask.FromException(failure);
            }

            return ValueTask.CompletedTask;
        };
        RecordingRunLockLease lease = new()
        {
            ReleaseBehavior = static () =>
                Task.FromResult(new ProcessingRunLockRelease(InfrastructureFailure: true))
        };

        TestSinkException thrown = await Assert.ThrowsExactlyAsync<TestSinkException>(() =>
            CreateExecutor(fixture, RecordingRunLock.Acquired(lease), outcomes)
                .ExecuteAsync(fixture.Request, fixture.Reporter, CancellationToken.None));

        Assert.AreSame(failure, thrown);
        Assert.AreEqual(1, lease.ReleaseCalls);
        Assert.AreEqual(6, outcomes.Fact.ExitCode);
        Assert.AreSame(WorkerProcessExitFact.OutputTransport(), outcomes.Fact);
    }

    [TestMethod]
    public async Task ExecuteAsync_CleanupFailureAfterCompleted_DoesNotRewriteTerminal()
    {
        ExecutorFixture fixture = new ExecutorFixture().EnableReporter().EnableCount(0);
        RecordingRunLockLease lease = new()
        {
            ReleaseBehavior = static () =>
                Task.FromResult(new ProcessingRunLockRelease(InfrastructureFailure: true))
        };
        WorkerProcessExitOutcomeAccumulator outcomes = new();

        ProcessingRunResult result = await CreateExecutor(
                fixture,
                RecordingRunLock.Acquired(lease),
                outcomes)
            .ExecuteAsync(fixture.Request, fixture.Reporter, CancellationToken.None)
            .WaitAsync(Bound);

        Assert.AreEqual(ProcessingRunOutcome.Completed, result.Outcome);
        Assert.AreEqual(ProcessingRunOutcome.Completed, fixture.Reporter.Events.OfType<RunFinished>().Single().Result.Outcome);
        Assert.AreSame(WorkerProcessExitFact.CleanupInfrastructure(), outcomes.Fact);
        Assert.AreEqual(5, outcomes.Fact.ExitCode);
    }

    [TestMethod]
    public async Task ExecuteAsync_ReleaseThrowsAfterCompleted_DoesNotRewriteTerminal()
    {
        ExecutorFixture fixture = new ExecutorFixture().EnableReporter().EnableCount(0);
        RecordingRunLockLease lease = new()
        {
            ReleaseBehavior = static () =>
                Task.FromException<ProcessingRunLockRelease>(new InvalidOperationException("release failed"))
        };
        WorkerProcessExitOutcomeAccumulator outcomes = new();

        ProcessingRunResult result = await CreateExecutor(
                fixture,
                RecordingRunLock.Acquired(lease),
                outcomes)
            .ExecuteAsync(fixture.Request, fixture.Reporter, CancellationToken.None)
            .WaitAsync(Bound);

        Assert.AreEqual(ProcessingRunOutcome.Completed, result.Outcome);
        Assert.AreEqual(1, lease.ReleaseCalls);
        Assert.AreSame(WorkerProcessExitFact.CleanupInfrastructure(), outcomes.Fact);
    }

    [TestMethod]
    public async Task ExecuteAsync_CompletedRun_ReleasesOnceAndDisposesLinkedCancellation()
    {
        CancellationToken protectedToken = default;
        ExecutorFixture fixture = new ExecutorFixture().EnableReporter().EnableCount(0);
        fixture.CountBehavior = token =>
        {
            protectedToken = token;
            return Task.FromResult(0L);
        };
        RecordingRunLockLease lease = new();

        _ = await CreateExecutor(
                fixture,
                RecordingRunLock.Acquired(lease),
                new WorkerProcessExitOutcomeAccumulator())
            .ExecuteAsync(fixture.Request, fixture.Reporter, CancellationToken.None)
            .WaitAsync(Bound);
        lease.LoseOwnership();

        Assert.AreEqual(1, lease.ReleaseCalls);
        Assert.IsFalse(protectedToken.IsCancellationRequested);
    }

    [TestMethod]
    public async Task ExecuteAsync_NoRunLock_PreservesLegacyZeroWorkBehavior()
    {
        ExecutorFixture fixture = new ExecutorFixture().EnableReporter().EnableCount(0);

        ProcessingRunResult result = await fixture.Executor
            .ExecuteAsync(fixture.Request, fixture.Reporter, CancellationToken.None)
            .WaitAsync(Bound);

        Assert.AreEqual(ProcessingRunOutcome.Completed, result.Outcome);
        Assert.AreEqual(0L, result.ProcessedCount);
        Assert.AreEqual(1, fixture.CountCalls);
        CollectionAssert.AreEqual(
            new[] { typeof(RunStarted), typeof(EligibilityDetermined), typeof(RunFinished) },
            fixture.Reporter.Events.Select(item => item.GetType()).ToArray());
    }

    [TestMethod]
    public async Task WorkerHostRegistration_AddsOneSharedRunLockWhileExecutionRegistrationDoesNot()
    {
        ServiceCollection webServices = new();
        webServices.AddWorkerExecutionComposition();
        Assert.IsFalse(webServices.Any(item => item.ServiceType == typeof(IProcessingRunLock)));

        await using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(
            "Host=localhost;Database=immich;Username=test;Password=test");
        ServiceCollection workerServices = new();
        workerServices.AddSingleton(dataSource);
        workerServices.AddSingleton(TimeProvider.System);
        workerServices.AddInternalWorkerHostServices(
            new MemoryOutputStreamFactory(),
            new WorkerProcessExitOutcomeAccumulator());
        await using ServiceProvider provider = workerServices.BuildServiceProvider();

        IProcessingRunLock abstraction = provider.GetRequiredService<IProcessingRunLock>();
        PostgresqlProcessingRunLock implementation = provider.GetRequiredService<PostgresqlProcessingRunLock>();
        Assert.AreSame(implementation, abstraction);
    }

    private static ProcessingRunExecutor CreateExecutor(
        ExecutorFixture fixture,
        IProcessingRunLock runLock,
        WorkerProcessExitOutcomeAccumulator outcomes)
    {
        return new ProcessingRunExecutor(
            fixture.Logger,
            fixture,
            fixture,
            fixture,
            fixture,
            fixture,
            fixture,
            fixture.TimeProvider,
            runLock,
            outcomes);
    }

    private static void AssertGateFailure(ProcessingRunResult result, ProcessingRunOutcome outcome)
    {
        Assert.AreEqual(outcome, result.Outcome);
        Assert.AreEqual(0L, result.ProcessedCount);
        Assert.AreEqual(0L, result.UpdatedCount);
        Assert.AreEqual(0L, result.SkippedCount);
        Assert.AreEqual(0L, result.FailedCount);
    }

    private static void AssertOneStartedAndFinishedWithoutEligibility(ExecutorFixture fixture)
    {
        Assert.AreEqual(0, fixture.CountCalls);
        Assert.AreEqual(0, fixture.Reporter.Events.OfType<EligibilityDetermined>().Count());
        Assert.AreEqual(1, fixture.Reporter.Events.OfType<RunFinished>().Count());
        CollectionAssert.AreEqual(
            new[] { typeof(RunStarted), typeof(RunFinished) },
            fixture.Reporter.Events.Select(item => item.GetType()).ToArray());
    }

    private sealed class RecordingRunLock(
        Func<CancellationToken, ValueTask<ProcessingRunLockAcquisition>> acquire)
        : IProcessingRunLock
    {
        internal int AcquireCalls { get; private set; }

        public ValueTask<ProcessingRunLockAcquisition> AcquireAsync(CancellationToken cancellationToken)
        {
            AcquireCalls++;
            return acquire(cancellationToken);
        }

        internal static RecordingRunLock Acquired(IProcessingRunLockLease lease) =>
            new(_ => ValueTask.FromResult<ProcessingRunLockAcquisition>(
                new ProcessingRunLockAcquisition.Acquired(lease)));
    }

    private sealed class RecordingRunLockLease : IProcessingRunLockLease
    {
        private readonly CancellationTokenSource _ownershipLost = new();
        private int _isOwnershipLost;

        internal Func<Task<ProcessingRunLockRelease>> ReleaseBehavior { get; set; } = static () =>
            Task.FromResult(new ProcessingRunLockRelease(InfrastructureFailure: false));

        internal int ReleaseCalls { get; private set; }

        public CancellationToken OwnershipLost => _ownershipLost.Token;

        public bool IsOwnershipLost => Volatile.Read(ref _isOwnershipLost) != 0;

        public Task<ProcessingRunLockRelease> ReleaseAsync()
        {
            ReleaseCalls++;
            return ReleaseBehavior();
        }

        public async ValueTask DisposeAsync()
        {
            _ = await ReleaseAsync().ConfigureAwait(false);
        }

        internal void LoseOwnership()
        {
            if (Interlocked.Exchange(ref _isOwnershipLost, 1) == 0)
            {
                _ownershipLost.Cancel();
            }
        }
    }

    private sealed class MemoryOutputStreamFactory : IWorkerNdjsonOutputStreamFactory
    {
        public Stream OpenStandardOutput() => new MemoryStream();
    }
}
