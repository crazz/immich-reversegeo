using System.Collections.Concurrent;
using ImmichReverseGeo.Core.ApplicationRole;
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
public sealed class RunOnceLifecycleTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task RealExecutorAndPostgresqlGate_NoWorkRunsOnceInExactOrderAndSelfStopRemainsCompleted()
    {
        var outcomes = new WorkerProcessExitOutcomeAccumulator();
        var ledger = new ConcurrentQueue<string>();
        var session = new RecordingRunLockSession
        {
            OpenBehavior = _ =>
            {
                ledger.Enqueue("lock-open");
                return Task.CompletedTask;
            },
            ExecuteBehavior = (command, _) =>
            {
                ledger.Enqueue(command == ProcessingRunLockCommand.Acquire
                    ? "lock-acquire"
                    : command == ProcessingRunLockCommand.Release
                        ? "lock-release"
                        : "lock-probe");
                return Task.FromResult<object?>(command == ProcessingRunLockCommand.Probe ? 1 : true);
            }
        };
        var lockClock = new ManualRunLockTimeProvider();
        var runLock = new PostgresqlProcessingRunLock(
            new RecordingRunLockSessionFactory(session),
            lockClock,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
        var fixture = new ExecutorFixture();
        fixture.CountBehavior = _ =>
        {
            ledger.Enqueue("count");
            return Task.FromResult(0L);
        };
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
        var reporter = new LedgerReporter(ledger);
        var initializer = new CompletedInitializer();
        IHost host = BuildHost(outcomes, initializer, _ => executor, _ => reporter);

        int exitCode = await RunOnceApplication.RunHostAsync(host, outcomes).WaitAsync(Bound);

        Assert.AreEqual(WorkerProcessExitCodes.Completed, exitCode);
        Assert.AreEqual("completed", outcomes.Fact.Diagnostic.Token);
        Assert.AreEqual(1, fixture.CountCalls);
        Assert.AreEqual(0, fixture.ConfigCalls);
        Assert.AreEqual(0, fixture.SkippedCalls);
        Assert.AreEqual(0, fixture.BatchCalls);
        Assert.AreEqual(1, initializer.InitialiseCalls);
        Assert.AreEqual(1, session.DisposeCalls);
        Assert.AreEqual(1, session.Commands.Count(x => x == ProcessingRunLockCommand.Acquire));
        Assert.AreEqual(1, session.Commands.Count(x => x == ProcessingRunLockCommand.Release));

        ProcessingEvent[] events = reporter.Events.ToArray();
        Assert.AreEqual(3, events.Length);
        var started = Assert.IsInstanceOfType<RunStarted>(events[0]);
        Assert.AreNotEqual(Guid.Empty, started.Request.RunId);
        Assert.AreEqual(ProcessingRunTrigger.RunOnce, started.Request.Trigger);
        Assert.IsInstanceOfType<EligibilityDetermined>(events[1]);
        Assert.IsInstanceOfType<RunFinished>(events[2]);
        Assert.IsTrue(events.All(x => ReferenceEquals(started.Request, x.Request)));
        AssertOrdered(ledger, "RunStarted", "lock-open", "lock-acquire", "count", "EligibilityDetermined", "RunFinished", "lock-release");
    }

    [TestMethod]
    public async Task HostStoppingBeforeExecutorEntryProducesNoRequestOrTerminal()
    {
        var outcomes = new WorkerProcessExitOutcomeAccumulator();
        var initializer = new GatedInitializer();
        var executor = new RecordingExecutor(ProcessingRunOutcome.Completed);
        var reporter = new LedgerReporter(new ConcurrentQueue<string>());
        IHost host = BuildHost(outcomes, initializer, _ => executor, _ => reporter);
        IHostApplicationLifetime lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        Task<int> run = RunOnceApplication.RunHostAsync(host, outcomes);

        try
        {
            await initializer.Entered.Task.WaitAsync(Bound);
            lifetime.StopApplication();
        }
        finally
        {
            initializer.Release.TrySetResult();
        }

        int exitCode = await run.WaitAsync(Bound);
        Assert.AreEqual(WorkerProcessExitCodes.Cancelled, exitCode);
        Assert.AreEqual(0, executor.Calls);
        Assert.AreEqual(0, reporter.Events.Count);
        Assert.AreEqual("cancelled", outcomes.Fact.Diagnostic.Token);
    }

    [TestMethod]
    public async Task HostStoppingAfterSessionEntryUsesExactTokenAndRetainsOneCancelledTerminal()
    {
        var outcomes = new WorkerProcessExitOutcomeAccumulator();
        var initializer = new CompletedInitializer();
        var executor = new CancellationExecutor();
        var reporter = new LedgerReporter(new ConcurrentQueue<string>());
        IHost host = BuildHost(outcomes, initializer, _ => executor, _ => reporter);
        IHostApplicationLifetime lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        Task<int> run = RunOnceApplication.RunHostAsync(host, outcomes);

        await executor.SessionEntered.Task.WaitAsync(Bound);
        lifetime.StopApplication();
        int exitCode = await run.WaitAsync(Bound);

        Assert.AreEqual(WorkerProcessExitCodes.Cancelled, exitCode);
        Assert.AreEqual(lifetime.ApplicationStopping, executor.Token);
        Assert.AreEqual(1, executor.Calls);
        Assert.AreEqual(1, reporter.Events.Count(x => x is RunStarted));
        RunFinished terminal = reporter.Events.OfType<RunFinished>().Single();
        Assert.AreEqual(ProcessingRunOutcome.Cancelled, terminal.Result.Outcome);
        Assert.AreEqual("cancelled", outcomes.Fact.Diagnostic.Token);
    }

    [TestMethod]
    public async Task BusyTerminalPlusExternalStopAndRealScopeProviderCleanupFailuresEndsInfrastructureWithoutRetry()
    {
        var outcomes = new WorkerProcessExitOutcomeAccumulator();
        var lockSession = new RecordingRunLockSession
        {
            ExecuteBehavior = (command, _) => Task.FromResult<object?>(
                command == ProcessingRunLockCommand.Acquire ? false : true)
        };
        var runLock = new PostgresqlProcessingRunLock(
            new RecordingRunLockSessionFactory(lockSession),
            new ManualRunLockTimeProvider(),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
        var fixture = new ExecutorFixture();
        var realExecutor = new ProcessingRunExecutor(
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
        var scopedExecutor = new ThrowingScopedExecutor(realExecutor);
        var initializer = new ThrowingProviderInitializer();
        var reporter = new GatedTerminalReporter();
        IHost host = BuildHost(outcomes, initializer, _ => scopedExecutor, _ => reporter);
        IHostApplicationLifetime lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        Task<int> run = RunOnceApplication.RunHostAsync(host, outcomes);

        try
        {
            await reporter.TerminalEntered.Task.WaitAsync(Bound);
            lifetime.StopApplication();
        }
        finally
        {
            reporter.ReleaseTerminal.TrySetResult();
        }

        int exitCode = await run.WaitAsync(Bound);
        Assert.AreEqual(WorkerProcessExitCodes.InfrastructureFailure, exitCode);
        Assert.AreEqual("infrastructure-failure", outcomes.Fact.Diagnostic.Token);
        Assert.AreEqual(1, scopedExecutor.Calls);
        Assert.AreEqual(1, scopedExecutor.DisposeCalls, "The actual async execution scope must attempt disposal.");
        Assert.AreEqual(1, initializer.DisposeCalls, "The actual root provider must attempt disposal after the scope fault.");
        Assert.AreEqual(0, fixture.CountCalls);
        Assert.AreEqual(0, fixture.ConfigCalls);
        Assert.AreEqual(0, fixture.SkippedCalls);
        Assert.AreEqual(1, lockSession.Commands.Count(x => x == ProcessingRunLockCommand.Acquire));
        RunFinished terminal = reporter.Events.OfType<RunFinished>().Single();
        Assert.AreEqual(ProcessingRunOutcome.Failed, terminal.Result.Outcome);
        Assert.AreEqual("Another Immich ReverseGeo worker is already processing this database.", terminal.Result.FailureMessage);
        Assert.AreEqual(1, reporter.Events.Count(x => x is RunStarted));
        Assert.AreEqual(1, reporter.Events.Count(x => x is RunFinished));
    }

    [TestMethod]
    public async Task ExternalStopDuringGatedScopeFinalizationOverridesCompletedWithoutChangingTerminal()
    {
        var outcomes = new WorkerProcessExitOutcomeAccumulator();
        var inner = new RecordingExecutor(ProcessingRunOutcome.Completed);
        var executor = new GatedDisposalExecutor(inner);
        var reporter = new LedgerReporter(new ConcurrentQueue<string>());
        IHost host = BuildHost(outcomes, new CompletedInitializer(), _ => executor, _ => reporter);
        IHostApplicationLifetime lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        Task<int> run = RunOnceApplication.RunHostAsync(host, outcomes);

        try
        {
            await executor.DisposeEntered.Task.WaitAsync(Bound);
            lifetime.StopApplication();
        }
        finally
        {
            executor.ReleaseDispose.TrySetResult();
        }

        int exitCode = await run.WaitAsync(Bound);
        Assert.AreEqual(WorkerProcessExitCodes.Cancelled, exitCode);
        Assert.AreEqual(1, executor.Calls);
        Assert.AreEqual(1, executor.DisposeCalls);
        RunFinished terminal = reporter.Events.OfType<RunFinished>().Single();
        Assert.AreEqual(ProcessingRunOutcome.Completed, terminal.Result.Outcome);
        Assert.AreEqual("cancelled", outcomes.Fact.Diagnostic.Token);
    }

    [TestMethod]
    public async Task PartialHostStartFailureStillStopsStartedServiceAndDisposesProvider()
    {
        var outcomes = new WorkerProcessExitOutcomeAccumulator();
        var started = new RecordingHostedService();
        var failing = new FailingStartHostedService();
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [] });
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(outcomes);
        builder.Services.AddSingleton<IHostedService>(_ => started);
        builder.Services.AddSingleton<IHostedService>(_ => failing);
        IHost host = builder.Build();

        int exitCode = await RunOnceApplication.RunHostAsync(host, outcomes).WaitAsync(Bound);

        Assert.AreEqual(WorkerProcessExitCodes.InfrastructureFailure, exitCode);
        Assert.AreEqual(1, started.StartCalls);
        Assert.AreEqual(1, started.StopCalls);
        Assert.AreEqual(1, started.DisposeCalls);
        Assert.AreEqual(1, failing.StartCalls);
        Assert.AreEqual(1, failing.DisposeCalls);
    }

    [TestMethod]
    [DataRow(ProcessingRunOutcome.Completed, WorkerProcessExitCodes.Completed)]
    [DataRow(ProcessingRunOutcome.Failed, WorkerProcessExitCodes.ExecutorFailure)]
    public async Task ReturnedExecutorOutcomeMapsWithoutASecondRequest(
        ProcessingRunOutcome outcome,
        int expectedExit)
    {
        var outcomes = new WorkerProcessExitOutcomeAccumulator();
        var executor = new RecordingExecutor(outcome);
        var reporter = new LedgerReporter(new ConcurrentQueue<string>());
        IHost host = BuildHost(outcomes, new CompletedInitializer(), _ => executor, _ => reporter);

        int exitCode = await RunOnceApplication.RunHostAsync(host, outcomes).WaitAsync(Bound);

        Assert.AreEqual(expectedExit, exitCode);
        Assert.AreEqual(1, executor.Calls);
        Assert.IsNotNull(executor.Request);
        Assert.AreNotEqual(Guid.Empty, executor.Request.RunId);
        Assert.AreEqual(ProcessingRunTrigger.RunOnce, executor.Request.Trigger);
        Assert.AreEqual(1, reporter.Events.Count(x => x is RunStarted));
        Assert.AreEqual(1, reporter.Events.Count(x => x is RunFinished));
    }

    [TestMethod]
    public async Task InitializerFailureIsInfrastructureAndCreatesNoRequest()
    {
        var outcomes = new WorkerProcessExitOutcomeAccumulator();
        var executor = new RecordingExecutor(ProcessingRunOutcome.Completed);
        var reporter = new LedgerReporter(new ConcurrentQueue<string>());
        IHost host = BuildHost(outcomes, new FailingInitializer(), _ => executor, _ => reporter);

        int exitCode = await RunOnceApplication.RunHostAsync(host, outcomes).WaitAsync(Bound);

        Assert.AreEqual(WorkerProcessExitCodes.InfrastructureFailure, exitCode);
        Assert.AreEqual(0, executor.Calls);
        Assert.AreEqual(0, reporter.Events.Count);
    }

    private static IHost BuildHost(
        WorkerProcessExitOutcomeAccumulator outcomes,
        IWorkerStartupInitializer initializer,
        Func<IServiceProvider, IProcessingRunExecutor> executorFactory,
        Func<IServiceProvider, IProcessingEventReporter> reporterFactory)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [] });
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(outcomes);
        builder.Services.AddSingleton<IWorkerStartupInitializer>(_ => initializer);
        builder.Services.AddScoped(executorFactory);
        builder.Services.AddScoped(reporterFactory);
        return builder.Build();
    }

    private static void AssertOrdered(ConcurrentQueue<string> ledger, params string[] expected)
    {
        string[] observed = ledger.ToArray();
        var prior = -1;
        foreach (string value in expected)
        {
            int next = Array.IndexOf(observed, value);
            Assert.IsTrue(next > prior, $"Expected {value} after index {prior}. Observed: {string.Join(", ", observed)}");
            prior = next;
        }
    }

    private sealed class CompletedInitializer : IWorkerStartupInitializer
    {
        internal int InitialiseCalls { get; private set; }

        public Task InitialiseAsync(CancellationToken cancellationToken)
        {
            InitialiseCalls++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class GatedInitializer : IWorkerStartupInitializer
    {
        internal TaskCompletionSource Entered { get; } = NewSignal();
        internal TaskCompletionSource Release { get; } = NewSignal();

        public async Task InitialiseAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(Bound).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private sealed class FailingInitializer : IWorkerStartupInitializer
    {
        public Task InitialiseAsync(CancellationToken cancellationToken) =>
            Task.FromException(new IOException("startup-secret-candidate"));
    }

    private sealed class ThrowingProviderInitializer : IWorkerStartupInitializer, IAsyncDisposable
    {
        internal int DisposeCalls { get; private set; }

        public Task InitialiseAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.FromException(new IOException("provider cleanup failed"));
        }
    }

    private sealed class RecordingExecutor(ProcessingRunOutcome outcome) : IProcessingRunExecutor
    {
        internal int Calls { get; private set; }
        internal ProcessingRunRequest? Request { get; private set; }

        public async Task<ProcessingRunResult> ExecuteAsync(
            ProcessingRunRequest request,
            IProcessingEventReporter reporter,
            CancellationToken cancellationToken)
        {
            Calls++;
            Request = request;
            var started = DateTimeOffset.UnixEpoch;
            var session = await reporter.OpenRunAsync(request, started, CancellationToken.None);
            await session.DetermineEligibilityAsync(0, cancellationToken);
            var result = new ProcessingRunResult(
                request,
                started,
                started,
                0,
                0,
                0,
                0,
                outcome,
                outcome == ProcessingRunOutcome.Failed ? "domain failed" : null);
            await session.FinishAsync(result);
            return result;
        }
    }

    private sealed class CancellationExecutor : IProcessingRunExecutor
    {
        internal int Calls { get; private set; }
        internal CancellationToken Token { get; private set; }
        internal TaskCompletionSource SessionEntered { get; } = NewSignal();

        public async Task<ProcessingRunResult> ExecuteAsync(
            ProcessingRunRequest request,
            IProcessingEventReporter reporter,
            CancellationToken cancellationToken)
        {
            Calls++;
            Token = cancellationToken;
            var started = DateTimeOffset.UnixEpoch;
            var session = await reporter.OpenRunAsync(request, started, CancellationToken.None);
            SessionEntered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                Assert.Fail("Cancellation must end the one attempt.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }

            var result = new ProcessingRunResult(
                request,
                started,
                started,
                0,
                0,
                0,
                0,
                ProcessingRunOutcome.Cancelled,
                null);
            await session.FinishAsync(result);
            return result;
        }
    }

    private sealed class ThrowingScopedExecutor(IProcessingRunExecutor inner) : IProcessingRunExecutor, IAsyncDisposable
    {
        internal int Calls { get; private set; }
        internal int DisposeCalls { get; private set; }

        public Task<ProcessingRunResult> ExecuteAsync(
            ProcessingRunRequest request,
            IProcessingEventReporter reporter,
            CancellationToken cancellationToken)
        {
            Calls++;
            return inner.ExecuteAsync(request, reporter, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.FromException(new IOException("scope cleanup failed"));
        }
    }

    private sealed class GatedDisposalExecutor(IProcessingRunExecutor inner) : IProcessingRunExecutor, IAsyncDisposable
    {
        internal int Calls { get; private set; }
        internal int DisposeCalls { get; private set; }
        internal TaskCompletionSource DisposeEntered { get; } = NewSignal();
        internal TaskCompletionSource ReleaseDispose { get; } = NewSignal();

        public Task<ProcessingRunResult> ExecuteAsync(
            ProcessingRunRequest request,
            IProcessingEventReporter reporter,
            CancellationToken cancellationToken)
        {
            Calls++;
            return inner.ExecuteAsync(request, reporter, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            DisposeCalls++;
            DisposeEntered.TrySetResult();
            await ReleaseDispose.Task.WaitAsync(Bound).ConfigureAwait(false);
        }
    }

    private sealed class RecordingHostedService : IHostedService, IAsyncDisposable
    {
        internal int StartCalls { get; private set; }
        internal int StopCalls { get; private set; }
        internal int DisposeCalls { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            StartCalls++;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopCalls++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingStartHostedService : IHostedService, IAsyncDisposable
    {
        internal int StartCalls { get; private set; }
        internal int DisposeCalls { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            StartCalls++;
            return Task.FromException(new IOException("host startup failed"));
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private class LedgerReporter(ConcurrentQueue<string> ledger) : ProcessingEventReporter
    {
        internal ConcurrentQueue<ProcessingEvent> Events { get; } = new();

        protected override ValueTask AcceptAsync(ProcessingEvent processingEvent, CancellationToken cancellationToken)
        {
            Events.Enqueue(processingEvent);
            ledger.Enqueue(processingEvent.GetType().Name);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class GatedTerminalReporter : LedgerReporter
    {
        internal TaskCompletionSource TerminalEntered { get; } = NewSignal();
        internal TaskCompletionSource ReleaseTerminal { get; } = NewSignal();

        internal GatedTerminalReporter()
            : base(new ConcurrentQueue<string>())
        {
        }

        protected override async ValueTask AcceptAsync(
            ProcessingEvent processingEvent,
            CancellationToken cancellationToken)
        {
            if (processingEvent is RunFinished)
            {
                TerminalEntered.TrySetResult();
                await ReleaseTerminal.Task.WaitAsync(Bound).ConfigureAwait(false);
            }

            await base.AcceptAsync(processingEvent, cancellationToken);
        }
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
