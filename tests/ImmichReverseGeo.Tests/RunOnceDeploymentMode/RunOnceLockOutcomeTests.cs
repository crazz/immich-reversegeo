using System.Collections.Concurrent;
using System.Data;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using ImmichReverseGeo.Tests.ProcessingRunLocking;
using ImmichReverseGeo.Web.ProcessingRunLocking;
using ImmichReverseGeo.Web.RunOnce;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Tests.RunOnceDeploymentMode;

[TestClass]
[TestCategory("Change43")]
public sealed class RunOnceLockOutcomeTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [TestMethod]
    [DataRow(LockCase.OpenFailure, WorkerProcessExitCodes.InfrastructureFailure, ProcessingRunOutcome.Failed, 0)]
    [DataRow(LockCase.OwnershipLoss, WorkerProcessExitCodes.InfrastructureFailure, ProcessingRunOutcome.Failed, 1)]
    [DataRow(LockCase.UnlockFailureAfterCompleted, WorkerProcessExitCodes.InfrastructureFailure, ProcessingRunOutcome.Completed, 1)]
    [DataRow(LockCase.DomainFailure, WorkerProcessExitCodes.ExecutorFailure, ProcessingRunOutcome.Failed, 1)]
    public async Task RealExecutorAndPostgresqlLock_PreserveRunOnceOutcomePrecedence(
        LockCase @case,
        int expectedExit,
        ProcessingRunOutcome expectedTerminal,
        int expectedCountCalls)
    {
        var outcomes = new WorkerProcessExitOutcomeAccumulator();
        var lockSession = new RecordingRunLockSession();
        var fixture = new ExecutorFixture();
        Configure(@case, lockSession, fixture);
        var runLock = new PostgresqlProcessingRunLock(
            new RecordingRunLockSessionFactory(lockSession),
            new ManualRunLockTimeProvider(),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
        var executor = new ProcessingRunExecutor(
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
        var reporter = new RecordingReporter();

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [] });
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(outcomes);
        builder.Services.AddSingleton<IWorkerStartupInitializer, CompletedInitializer>();
        builder.Services.AddSingleton<IProcessingRunExecutor>(_ => executor);
        builder.Services.AddSingleton<IProcessingEventReporter>(_ => reporter);

        int exitCode = await RunOnceApplication.RunHostAsync(builder.Build(), outcomes).WaitAsync(Bound);

        Assert.AreEqual(expectedExit, exitCode);
        Assert.AreEqual(expectedCountCalls, fixture.CountCalls);
        Assert.AreEqual(1, reporter.Events.Count(x => x is RunStarted));
        RunFinished terminal = reporter.Events.OfType<RunFinished>().Single();
        Assert.AreEqual(expectedTerminal, terminal.Result.Outcome);
        Assert.AreEqual(1, reporter.Events.Count(x => x is RunFinished));
        Assert.AreEqual(expectedExit == WorkerProcessExitCodes.InfrastructureFailure
            ? "infrastructure-failure"
            : "executor-failure", outcomes.Fact.Diagnostic.Token);
    }

    private static void Configure(
        LockCase @case,
        RecordingRunLockSession lockSession,
        ExecutorFixture fixture)
    {
        switch (@case)
        {
            case LockCase.OpenFailure:
                lockSession.OpenBehavior = _ => Task.FromException(new IOException("database unavailable"));
                break;
            case LockCase.OwnershipLoss:
                fixture.CountBehavior = _ =>
                {
                    lockSession.SetState(ConnectionState.Broken);
                    return Task.FromResult(1L);
                };
                break;
            case LockCase.UnlockFailureAfterCompleted:
                fixture.CountBehavior = _ => Task.FromResult(0L);
                lockSession.ExecuteBehavior = (command, _) => Task.FromResult<object?>(
                    command == ProcessingRunLockCommand.Release ? false : command == ProcessingRunLockCommand.Probe ? 1 : true);
                break;
            case LockCase.DomainFailure:
                fixture.CountBehavior = _ => Task.FromResult(1L);
                fixture.ConfigBehavior = () => Task.FromException<AppConfig>(new InvalidOperationException("domain snapshot failed"));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(@case));
        }
    }

    public enum LockCase
    {
        OpenFailure,
        OwnershipLoss,
        UnlockFailureAfterCompleted,
        DomainFailure
    }

    private sealed class CompletedInitializer : IWorkerStartupInitializer
    {
        public Task InitialiseAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingReporter : ProcessingEventReporter
    {
        internal ConcurrentQueue<ProcessingEvent> Events { get; } = new();

        protected override ValueTask AcceptAsync(
            ProcessingEvent processingEvent,
            CancellationToken cancellationToken)
        {
            Events.Enqueue(processingEvent);
            return ValueTask.CompletedTask;
        }
    }
}
