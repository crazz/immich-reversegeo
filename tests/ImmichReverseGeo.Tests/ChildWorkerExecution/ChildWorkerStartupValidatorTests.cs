using System.Runtime.CompilerServices;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerCommandInvocation;
using ImmichReverseGeo.Web.WorkerFailureRecovery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ImmichReverseGeo.Tests.ChildWorkerExecution;

[TestClass]
[TestCategory("Change38")]
public sealed class ChildWorkerStartupValidatorTests
{
    [TestMethod]
    public async Task ChildWorker_ValidatesLaunchFactsAndRuntimeManifestsWithoutResolvingBackend()
    {
        var fixtureRoot = CreateFixture(includeRuntimeFiles: true);
        try
        {
            var source = new CountingObservationSource(fixtureRoot);
            using var provider = CreateProvider(source);
            var validator = new ChildWorkerStartupValidator(provider);

            await validator.StartingAsync(CancellationToken.None);

            Assert.AreEqual(1, source.ProcessPathCalls, "process-path-captured-once");
            Assert.AreEqual(1, source.EntryAssemblyCalls, "entry-assembly-captured-once");
            Assert.AreEqual(1, source.WorkingDirectoryCalls, "working-directory-captured-once");
            Assert.AreEqual(3, source.TargetCalls, "launch-facts-observed-once");
        }
        finally
        {
            Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task ChildWorker_MissingRuntimeManifestFailsBeforeAnyBackendResolution()
    {
        var fixtureRoot = CreateFixture(includeRuntimeFiles: false);
        try
        {
            var source = new CountingObservationSource(fixtureRoot);
            using var provider = CreateProvider(source);
            var validator = new ChildWorkerStartupValidator(provider);

            var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => validator.StartingAsync(CancellationToken.None));

            Assert.AreEqual(
                "The child worker runtime files are unavailable. Publish the complete Web application artifact and retry startup.",
                failure.Message,
                "safe-actionable-message");
            Assert.AreEqual(3, source.TargetCalls, "launch-facts-checked-before-manifests");
        }
        finally
        {
            Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task ChildWorker_UsesResolvedAssemblyDirectoryWhenWorkingDirectoryDiffers()
    {
        var applicationDirectory = CreateFixture(includeRuntimeFiles: true);
        var workingDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var source = new CountingObservationSource(applicationDirectory, workingDirectory);
            using var provider = CreateProvider(source);
            var validator = new ChildWorkerStartupValidator(provider);

            await validator.StartingAsync(CancellationToken.None);
        }
        finally
        {
            Directory.Delete(applicationDirectory, recursive: true);
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ChildWorker_AppHostLayoutUsesResolvedAssemblyDirectoryWhenWorkingDirectoryDiffers()
    {
        var applicationDirectory = CreateFixture(includeRuntimeFiles: true);
        var workingDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var source = new CountingObservationSource(
                applicationDirectory,
                workingDirectory,
                Path.Combine(applicationDirectory, "ImmichReverseGeo.Web"));
            using var provider = CreateProvider(source);
            var validator = new ChildWorkerStartupValidator(provider);

            await validator.StartingAsync(CancellationToken.None);
        }
        finally
        {
            Directory.Delete(applicationDirectory, recursive: true);
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task MissingChildPrerequisite_PreventsLaterHostedServicesFromStarting()
    {
        var fixtureRoot = CreateFixture(includeRuntimeFiles: false);
        try
        {
            var source = new CountingObservationSource(fixtureRoot);
            var laterService = new StartRecorder();
            using var host = new HostBuilder()
                .ConfigureServices(services =>
                {
                    AddValidatorServices(services, source);
                    services.AddSingleton(sp => new ChildWorkerStartupValidator(sp));
                    services.AddHostedService(sp => sp.GetRequiredService<ChildWorkerStartupValidator>());
                    services.AddSingleton<IHostedService>(laterService);
                })
                .Build();

            var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => host.StartAsync());

            Assert.AreEqual(
                "The child worker runtime files are unavailable. Publish the complete Web application artifact and retry startup.",
                failure.Message,
                "host-startup-diagnostic");
            Assert.AreEqual(0, laterService.StartCalls, "later-hosted-service-not-started");
        }
        finally
        {
            Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    private static ServiceProvider CreateProvider(CountingObservationSource source)
    {
        var services = new ServiceCollection();
        AddValidatorServices(services, source);
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static void AddValidatorServices(
        IServiceCollection services,
        CountingObservationSource source)
    {
        services.AddScoped<IChildProcessingRunBackend>(
            _ => throw new InvalidOperationException("child backend must stay lazy"));
        services.AddSingleton<IWorkerCommandInvocationBuilder>(
            new WorkerCommandInvocationBuilder(new WorkerCommandRuntimeFactsCapture(source)));
        services.AddSingleton(
            typeof(IChildProcessFactory),
            RuntimeHelpers.GetUninitializedObject(typeof(SystemChildProcessFactory)));
        services.AddSingleton(
            typeof(IChildWorkerLauncher),
            RuntimeHelpers.GetUninitializedObject(typeof(ChildWorkerLauncher)));
        services.AddSingleton(
            typeof(WorkerRunControlPlane),
            RuntimeHelpers.GetUninitializedObject(typeof(WorkerRunControlPlane)));
    }

    private static string CreateFixture(bool includeRuntimeFiles)
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
        File.WriteAllText(Path.Combine(fixtureRoot, "ImmichReverseGeo.Web.dll"), string.Empty);
        if (includeRuntimeFiles)
        {
            File.WriteAllText(Path.Combine(fixtureRoot, "ImmichReverseGeo.Web.runtimeconfig.json"), "{}");
            File.WriteAllText(Path.Combine(fixtureRoot, "ImmichReverseGeo.Web.deps.json"), "{}");
        }

        return fixtureRoot;
    }

    private sealed class CountingObservationSource : IWorkerCommandRuntimeObservationSource
    {
        private readonly string _applicationDirectory;
        private readonly string _workingDirectory;
        private readonly string _processPath;

        public CountingObservationSource(
            string applicationDirectory,
            string? workingDirectory = null,
            string? processPath = null)
        {
            _applicationDirectory = applicationDirectory;
            _workingDirectory = workingDirectory ?? applicationDirectory;
            _processPath = processPath ?? "/usr/share/dotnet/dotnet";
        }

        public int ProcessPathCalls { get; private set; }

        public int EntryAssemblyCalls { get; private set; }

        public int WorkingDirectoryCalls { get; private set; }

        public int TargetCalls { get; private set; }

        public string? GetProcessPath()
        {
            ProcessPathCalls++;
            return _processPath;
        }

        public WorkerCommandEntryAssemblyObservation GetEntryAssembly()
        {
            EntryAssemblyCalls++;
            return new WorkerCommandEntryAssemblyObservation(
                "ImmichReverseGeo.Web",
                Path.Combine(_applicationDirectory, "ImmichReverseGeo.Web.dll"));
        }

        public string GetCurrentDirectory()
        {
            WorkingDirectoryCalls++;
            return _workingDirectory;
        }

        public bool IsWindows()
        {
            return false;
        }

        public WorkerTargetObservation ObserveTarget(string path)
        {
            TargetCalls++;
            return path == _processPath
                ? WorkerTargetObservation.File
                : path == _workingDirectory
                    ? WorkerTargetObservation.Directory
                    : WorkerTargetObservation.File;
        }
    }

    private sealed class StartRecorder : IHostedService
    {
        public int StartCalls { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            StartCalls++;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.CompletedTask;
        }
    }
}
