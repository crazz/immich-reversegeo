using ImmichReverseGeo.Core.ApplicationRole;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using ImmichReverseGeo.Tests.ProcessingRunLocking;
using ImmichReverseGeo.Web.ProcessingRunLocking;
using ImmichReverseGeo.Web.RunOnce;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace ImmichReverseGeo.Tests.RunOnceDeploymentMode;

[TestClass]
[TestCategory("Change43")]
public sealed class RunOnceProductionRootTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task ProductionRoot_ActualExecutorReporterAndPostgresqlLockCompleteAuthoritativeNoWork()
    {
        using var root = new TemporaryRunOnceRoot();
        Directory.CreateDirectory(root.ConfigDirectory);
        await File.WriteAllTextAsync(Path.Combine(root.ConfigDirectory, "settings.json"), "{ must-stay-unread");
        var outcomes = new WorkerProcessExitOutcomeAccumulator();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var fixture = new ExecutorFixture().EnableCount(0);
        var session = new RecordingRunLockSession();
        IHost host = BuildProductionHost(
            root,
            outcomes,
            stdout,
            stderr,
            fixture,
            session,
            retainProductionConfiguration: true);

        int exitCode = await RunOnceApplication.RunHostAsync(host, outcomes).WaitAsync(Bound);

        Assert.AreEqual(WorkerProcessExitCodes.Completed, exitCode);
        Assert.AreEqual(1, fixture.CountCalls);
        Assert.AreEqual(0, fixture.ConfigCalls);
        Assert.AreEqual(0, fixture.SkippedCalls);
        Assert.AreEqual(0, fixture.BatchCalls);
        Assert.AreEqual(1, session.Commands.Count(command => command == ProcessingRunLockCommand.Acquire));
        Assert.AreEqual(1, session.Commands.Count(command => command == ProcessingRunLockCommand.Release));
        Assert.AreEqual(1, CountOccurrences(stdout.ToString(), "Run started."));
        StringAssert.Contains(stdout.ToString(), "Eligible assets: 0. Nothing to process.");
        StringAssert.Contains(stdout.ToString(), "Run completed: processed=0 updated=0 skipped=0 failed=0.");
        Assert.AreEqual(string.Empty, stderr.ToString());
    }

    [TestMethod]
    public async Task ProductionRoot_ActualExecutorReporterAndPostgresqlLockReturnBusyBeforeAnyDomainDependency()
    {
        using var root = new TemporaryRunOnceRoot();
        var outcomes = new WorkerProcessExitOutcomeAccumulator();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var fixture = new ExecutorFixture();
        var session = new RecordingRunLockSession
        {
            ExecuteBehavior = (command, _) => Task.FromResult<object?>(
                command == ProcessingRunLockCommand.Acquire ? false : true)
        };
        IHost host = BuildProductionHost(root, outcomes, stdout, stderr, fixture, session);

        int exitCode = await RunOnceApplication.RunHostAsync(host, outcomes).WaitAsync(Bound);

        Assert.AreEqual(WorkerProcessExitCodes.Busy, exitCode);
        Assert.AreEqual(0, fixture.CountCalls);
        Assert.AreEqual(0, fixture.ConfigCalls);
        Assert.AreEqual(0, fixture.SkippedCalls);
        Assert.AreEqual(0, fixture.BatchCalls);
        Assert.AreEqual(1, session.Commands.Count(command => command == ProcessingRunLockCommand.Acquire));
        Assert.AreEqual(0, session.Commands.Count(command => command == ProcessingRunLockCommand.Release));
        Assert.AreEqual(1, CountOccurrences(stdout.ToString(), "Run started."));
        Assert.AreEqual(1, CountOccurrences(stderr.ToString(), "Run failed:"));
    }

    [TestMethod]
    public async Task ProductionRoot_EligibleCommittedUpdateThenExternalStopRetainsEffectAndReturnsCancelled()
    {
        using var root = new TemporaryRunOnceRoot();
        var outcomes = new WorkerProcessExitOutcomeAccumulator();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var secondFetchEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondFetch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var asset = ExecutorFixture.Asset(1);
        var fixture = new ExecutorFixture()
            .EnableCount(1)
            .EnableSnapshots()
            .EnableAdmin()
            .EnableAirport()
            .EnableWrite()
            .EnableDelay();
        fixture.BatchBehavior = async (_, _, call, token) =>
        {
            if (call == 1)
            {
                return [asset];
            }

            secondFetchEntered.TrySetResult();
            await releaseSecondFetch.Task.WaitAsync(token).ConfigureAwait(false);
            return [];
        };
        var session = new RecordingRunLockSession();
        IHost host = BuildProductionHost(
            root,
            outcomes,
            stdout,
            stderr,
            fixture,
            session,
            retainProductionConfiguration: true);
        IHostApplicationLifetime lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        Task<int> run = RunOnceApplication.RunHostAsync(host, outcomes);

        try
        {
            await secondFetchEntered.Task.WaitAsync(Bound);
            Assert.AreEqual(1, fixture.Writes.Count);
            lifetime.StopApplication();

            int exitCode = await run.WaitAsync(Bound);
            Assert.AreEqual(WorkerProcessExitCodes.Cancelled, exitCode);
            Assert.AreEqual(1, fixture.CountCalls);
            Assert.AreEqual(0, fixture.ConfigCalls, "The production ConfigService must own the accepted snapshot.");
            Assert.AreEqual(1, fixture.SkippedCalls);
            Assert.AreEqual(2, fixture.BatchCalls);
            Assert.AreEqual(1, fixture.WriteAttempts);
            Assert.AreEqual(asset.Id, fixture.Writes.Single().AssetId);
            Assert.AreEqual(1, session.Commands.Count(command => command == ProcessingRunLockCommand.Release));
            Assert.AreEqual(1, CountOccurrences(stdout.ToString(), "Run started."));
            StringAssert.Contains(stdout.ToString(), "Progress: processed=1 updated=1 skipped=0 failed=0.");
            Assert.AreEqual(1, CountOccurrences(stderr.ToString(), "Run cancelled:"));
        }
        finally
        {
            if (!run.IsCompleted)
            {
                lifetime.StopApplication();
            }

            releaseSecondFetch.TrySetResult();
            await run.WaitAsync(Bound);
        }
    }

    [TestMethod]
    public async Task ProductionRoot_MalformedRequiredSettingsIsInfrastructureAfterOneFailedTerminal()
    {
        using var root = new TemporaryRunOnceRoot();
        Directory.CreateDirectory(root.ConfigDirectory);
        await File.WriteAllTextAsync(Path.Combine(root.ConfigDirectory, "settings.json"), "{ malformed-settings");
        var outcomes = new WorkerProcessExitOutcomeAccumulator();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var fixture = new ExecutorFixture().EnableCount(1).EnableSnapshots();
        var session = new RecordingRunLockSession();
        IHost host = BuildProductionHost(
            root,
            outcomes,
            stdout,
            stderr,
            fixture,
            session,
            retainProductionConfiguration: true);

        int exitCode = await RunOnceApplication.RunHostAsync(host, outcomes).WaitAsync(Bound);

        Assert.AreEqual(WorkerProcessExitCodes.InfrastructureFailure, exitCode);
        Assert.AreEqual(1, fixture.CountCalls);
        Assert.AreEqual(1, fixture.SkippedCalls);
        Assert.AreEqual(0, fixture.ConfigCalls);
        Assert.AreEqual(0, fixture.BatchCalls);
        Assert.AreEqual(1, session.Commands.Count(command => command == ProcessingRunLockCommand.Release));
        Assert.AreEqual(1, CountOccurrences(stdout.ToString(), "Run started."));
        Assert.AreEqual(1, CountOccurrences(stderr.ToString(), "Run failed:"));
        Assert.IsFalse(stderr.ToString().Contains("malformed-settings", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ProductionRoot_DatabaseCountFailureIsInfrastructureAfterOneFailedTerminal()
    {
        using var root = new TemporaryRunOnceRoot();
        var outcomes = new WorkerProcessExitOutcomeAccumulator();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var fixture = new ExecutorFixture
        {
            CountBehavior = _ => Task.FromException<long>(new IOException("database-count-secret"))
        };
        var session = new RecordingRunLockSession();
        IHost host = BuildProductionHost(root, outcomes, stdout, stderr, fixture, session);

        int exitCode = await RunOnceApplication.RunHostAsync(host, outcomes).WaitAsync(Bound);

        Assert.AreEqual(WorkerProcessExitCodes.InfrastructureFailure, exitCode);
        Assert.AreEqual(1, fixture.CountCalls);
        Assert.AreEqual(0, fixture.ConfigCalls);
        Assert.AreEqual(0, fixture.SkippedCalls);
        Assert.AreEqual(0, fixture.BatchCalls);
        Assert.AreEqual(1, session.Commands.Count(command => command == ProcessingRunLockCommand.Release));
        Assert.AreEqual(1, CountOccurrences(stderr.ToString(), "Run failed:"));
        Assert.IsFalse(stderr.ToString().Contains("database-count-secret", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("asset-update")]
    [DataRow("skipped-state")]
    public async Task ProductionRoot_StorageMutationFailureAddsInfrastructureWithoutRetryOrFalseEffect(
        string caseId)
    {
        using var root = new TemporaryRunOnceRoot();
        var outcomes = new WorkerProcessExitOutcomeAccumulator();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var asset = ExecutorFixture.Asset(1);
        var fixture = new ExecutorFixture()
            .EnableCount(1)
            .EnableSnapshots()
            .EnablePages()
            .EnableAdmin()
            .EnableAirport()
            .EnableDelay();
        fixture.SetPages([asset], []);
        if (caseId == "asset-update")
        {
            fixture.EnableWrite();
            fixture.WriteBehavior = (_, _, _) => Task.FromException(new IOException("database-write-secret"));
        }
        else
        {
            fixture.EnableSkippedInsert();
            fixture.ResolveBehavior = (_, _, _) => Task.FromResult<AdministrativeAreaResolution?>(null);
            fixture.AddSkippedBehavior = _ => Task.FromException(new IOException("skipped-store-secret"));
        }

        var session = new RecordingRunLockSession();
        IHost host = BuildProductionHost(
            root,
            outcomes,
            stdout,
            stderr,
            fixture,
            session,
            retainProductionConfiguration: true);

        int exitCode = await RunOnceApplication.RunHostAsync(host, outcomes).WaitAsync(Bound);

        Assert.AreEqual(WorkerProcessExitCodes.InfrastructureFailure, exitCode);
        Assert.AreEqual(1, fixture.CountCalls);
        Assert.AreEqual(2, fixture.BatchCalls);
        Assert.AreEqual(caseId == "asset-update" ? 1 : 0, fixture.WriteAttempts);
        Assert.AreEqual(caseId == "skipped-state" ? 1 : 0, fixture.SkippedInsertAttempts);
        Assert.AreEqual(0, fixture.Writes.Count);
        Assert.AreEqual(0, fixture.SkippedWrites.Count);
        Assert.AreEqual(1, session.Commands.Count(command => command == ProcessingRunLockCommand.Release));
        Assert.AreEqual(1, CountOccurrences(stdout.ToString(), "Run started."));
        Assert.AreEqual(1, CountOccurrences(stdout.ToString(), "Progress: processed=1 updated=0 skipped=0 failed=1."));
        Assert.AreEqual(1, CountOccurrences(stderr.ToString(), "Processing reported an error message."));
        Assert.IsFalse((stdout + stderr.ToString()).Contains("secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ProductionRoot_FatalLockFailureEscapesAfterProviderCleanupWithoutManagedTerminal()
    {
        using var root = new TemporaryRunOnceRoot();
        var outcomes = new WorkerProcessExitOutcomeAccumulator();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var fixture = new ExecutorFixture();
        var failure = new OutOfMemoryException("controlled fatal lock failure");
        var factory = new RecordingRunLockSessionFactory(new RecordingRunLockSession())
        {
            CreateFailure = failure
        };
        var cleanup = new ProductionRootCleanupProbe();
        IHost host = BuildProductionHost(root, outcomes, stdout, stderr, fixture, factory);
        var services = Assert.IsInstanceOfType<ServiceProviderMarker>(host.Services.GetRequiredService<IServiceCollectionMarker>());
        services.Probe = cleanup;

        OutOfMemoryException thrown = await Assert.ThrowsExactlyAsync<OutOfMemoryException>(
            () => RunOnceApplication.RunHostAsync(host, outcomes).WaitAsync(Bound));

        Assert.AreSame(failure, thrown);
        Assert.AreEqual(1, cleanup.DisposeCalls);
        Assert.IsFalse(outcomes.HasFact);
        Assert.AreEqual(1, CountOccurrences(stdout.ToString(), "Run started."));
        Assert.AreEqual(0, CountOccurrences(stderr.ToString(), "Run failed:"));
    }

    private static IHost BuildProductionHost(
        TemporaryRunOnceRoot root,
        WorkerProcessExitOutcomeAccumulator outcomes,
        TextWriter stdout,
        TextWriter stderr,
        ExecutorFixture fixture,
        RecordingRunLockSession session,
        bool retainProductionConfiguration = false)
    {
        return BuildProductionHost(
            root,
            outcomes,
            stdout,
            stderr,
            fixture,
            new RecordingRunLockSessionFactory(session),
            retainProductionConfiguration);
    }

    private static IHost BuildProductionHost(
        TemporaryRunOnceRoot root,
        WorkerProcessExitOutcomeAccumulator outcomes,
        TextWriter stdout,
        TextWriter stderr,
        ExecutorFixture fixture,
        IProcessingRunLockSessionFactory lockSessionFactory,
        bool retainProductionConfiguration = false)
    {
        var builder = RunOnceApplication.CreateBuilder(
            DeploymentMode.RunOnce,
            [],
            name => name == "DATA_DIR" ? root.DataDirectory : root.ConfigDirectory,
            stdout,
            stderr,
            outcomes);
        builder.Services.RemoveAll<IWorkerStartupInitializer>();
        builder.Services.AddSingleton<IWorkerStartupInitializer, CompletedInitializer>();
        builder.Services.RemoveAll<PostgresqlProcessingRunLock>();
        builder.Services.AddSingleton(_ => new PostgresqlProcessingRunLock(
            lockSessionFactory,
            fixture.TimeProvider,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1)));
        builder.Services.RemoveAll<RunOnceInfrastructureDependencies>();
        builder.Services.AddSingleton(sp => new RunOnceInfrastructureDependencies(
            retainProductionConfiguration
                ? sp.GetRequiredService<ConfigService>()
                : fixture,
            fixture,
            fixture));
        builder.Services.RemoveAll<IProcessingAdministrativeResolver>();
        builder.Services.AddSingleton<IProcessingAdministrativeResolver>(fixture);
        builder.Services.RemoveAll<IProcessingInfrastructureLookup>();
        builder.Services.AddSingleton<IProcessingInfrastructureLookup>(fixture);
        builder.Services.RemoveAll<IProcessingRunDelay>();
        builder.Services.AddSingleton<IProcessingRunDelay>(fixture);
        builder.Services.AddSingleton<IServiceCollectionMarker, ServiceProviderMarker>();
        return builder.Build();
    }

    private static int CountOccurrences(string value, string expected) =>
        value.Split(expected, StringSplitOptions.None).Length - 1;

    private sealed class CompletedInitializer : IWorkerStartupInitializer
    {
        public Task InitialiseAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private interface IServiceCollectionMarker
    {
    }

    private sealed class ServiceProviderMarker : IServiceCollectionMarker, IDisposable
    {
        internal ProductionRootCleanupProbe? Probe { get; set; }

        public void Dispose()
        {
            Probe?.Dispose();
        }
    }

    private sealed class ProductionRootCleanupProbe
    {
        internal int DisposeCalls { get; private set; }

        internal void Dispose()
        {
            DisposeCalls++;
        }
    }

    private sealed class TemporaryRunOnceRoot : IDisposable
    {
        internal TemporaryRunOnceRoot()
        {
            Root = Path.Combine(Path.GetTempPath(), "immich-reversegeo-run-once-root", Guid.NewGuid().ToString("D"));
            DataDirectory = Path.Combine(Root, "data");
            ConfigDirectory = Path.Combine(Root, "config");
            Directory.CreateDirectory(Root);
        }

        private string Root { get; }

        internal string DataDirectory { get; }

        internal string ConfigDirectory { get; }

        public void Dispose()
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
