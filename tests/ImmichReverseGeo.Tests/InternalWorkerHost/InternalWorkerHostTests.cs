using ImmichReverseGeo.Web.Composition;
using ImmichReverseGeo.Web.WorkerHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using WorkerHostFactory = ImmichReverseGeo.Web.WorkerHost.InternalWorkerHost;

namespace ImmichReverseGeo.Tests.InternalWorkerHost;

[TestClass]
public sealed class InternalWorkerHostTests
{
    [TestMethod]
    public void ProductionHost_BuildsOneLifecycleAndExcludesWorkerForbiddenServices()
    {
        var fixtureRoot = CreateFixtureRoot();

        try
        {
            using var host = WorkerHostFactory.Build(CreateContext(fixtureRoot));
            var hostedServices = host.Services.GetServices<IHostedService>().ToArray();
            var forbiddenTypes = new[]
            {
                typeof(Microsoft.AspNetCore.Hosting.Server.IServer),
                typeof(Microsoft.AspNetCore.Hosting.IWebHostEnvironment),
                typeof(Microsoft.AspNetCore.Routing.EndpointDataSource),
                typeof(Microsoft.AspNetCore.Routing.LinkGenerator),
                typeof(Microsoft.AspNetCore.Antiforgery.IAntiforgery),
                typeof(Microsoft.AspNetCore.DataProtection.IDataProtectionProvider),
                typeof(Microsoft.Extensions.Options.IConfigureOptions<Microsoft.AspNetCore.Components.Endpoints.RazorComponentsServiceOptions>),
                typeof(Microsoft.Extensions.Options.IPostConfigureOptions<Microsoft.AspNetCore.Components.Endpoints.RazorComponentsServiceOptions>),
                typeof(Microsoft.Extensions.Options.IConfigureOptions<Microsoft.AspNetCore.Components.Server.CircuitOptions>),
                typeof(Microsoft.Extensions.Options.IPostConfigureOptions<Microsoft.AspNetCore.Components.Server.CircuitOptions>),
                typeof(ImmichReverseGeo.Web.Services.ProcessingState),
                typeof(ImmichReverseGeo.Web.Services.ProcessingRunCoordinator),
                typeof(ImmichReverseGeo.Web.Services.ProcessingBackgroundService),
                typeof(ImmichReverseGeo.Web.Services.IManualProcessingRunCoordinator),
                typeof(ImmichReverseGeo.Web.Services.IScheduledRunTrigger)
            };

            Assert.AreEqual(1, hostedServices.Length, "worker-lifecycle-count");
            Assert.IsInstanceOfType<InternalWorkerLifecycleService>(hostedServices[0], "worker-lifecycle-type");
            Assert.IsNotNull(host.Services.GetRequiredService<ImmichReverseGeo.Web.Services.IProcessingRunExecutor>(), "worker-executor-graph");
            var skippedInitializer = host.Services.GetRequiredService<SkippedAssetsWorkerStartupInitializer>();
            Assert.AreSame(skippedInitializer, host.Services.GetRequiredService<IWorkerStartupInitializer>(), "worker-skipped-initializer-alias");

            foreach (var forbiddenType in forbiddenTypes)
            {
                Assert.IsNull(host.Services.GetService(forbiddenType), $"worker-forbidden-{forbiddenType.Name}");
            }

            Assert.IsFalse(Directory.Exists(Path.Combine(fixtureRoot, "localdata")), "worker-build-does-not-initialise-storage");
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    [TestMethod]
    public void CreateBuilder_RegistersOneSingletonOwnerAndAliasForWorkerComposition()
    {
        var fixtureRoot = CreateFixtureRoot();
        try
        {
            var descriptors = WorkerHostFactory.CreateBuilder(CreateContext(fixtureRoot)).Services;
            AssertDescriptor(descriptors, typeof(ImmichReverseGeo.Web.Services.ProcessingInfrastructureLookup), ServiceLifetime.Singleton);
            AssertDescriptor(descriptors, typeof(ImmichReverseGeo.Web.Services.IProcessingInfrastructureLookup), ServiceLifetime.Singleton);
            AssertDescriptor(descriptors, typeof(ImmichReverseGeo.Web.Services.ProcessingRunExecutor), ServiceLifetime.Singleton);
            AssertDescriptor(descriptors, typeof(ImmichReverseGeo.Web.Services.IProcessingRunExecutor), ServiceLifetime.Singleton);
            AssertDescriptor(descriptors, typeof(SkippedAssetsWorkerStartupInitializer), ServiceLifetime.Singleton);
            AssertDescriptor(descriptors, typeof(IWorkerStartupInitializer), ServiceLifetime.Singleton);
            AssertDescriptor(descriptors, typeof(WorkerTransportNotConfigured), ServiceLifetime.Singleton);
            AssertDescriptor(descriptors, typeof(IWorkerTransportAvailability), ServiceLifetime.Singleton);
            AssertDescriptor(descriptors, typeof(TransitionalWorkerPreRequestFinality), ServiceLifetime.Singleton);
            AssertDescriptor(descriptors, typeof(IWorkerPreRequestFinality), ServiceLifetime.Singleton);
            AssertDescriptor(descriptors, typeof(IHostedService), ServiceLifetime.Singleton);
        }
        finally { DeleteFixtureRoot(fixtureRoot); }
    }

    [TestMethod]
    public void BuildAndDescriptorFactory_ProduceTheSameAuthoritativeWorkerGraph()
    {
        var fixtureRoot = CreateFixtureRoot();
        try
        {
            var context = CreateContext(fixtureRoot);
            var descriptors = WorkerHostFactory.CreateBuilder(context).Services;
            using var host = WorkerHostFactory.Build(context);
            foreach (var serviceType in new[]
            {
                typeof(ImmichReverseGeo.Web.Services.IProcessingRunExecutor),
                typeof(IWorkerStartupInitializer),
                typeof(IWorkerTransportAvailability),
                typeof(IWorkerPreRequestFinality),
                typeof(IHostedService)
            })
            {
                Assert.AreEqual(1, descriptors.Count(descriptor => descriptor.ServiceType == serviceType), $"worker-authoritative-descriptor-{serviceType.Name}");
                Assert.IsNotNull(host.Services.GetService(serviceType), $"worker-authoritative-built-{serviceType.Name}");
            }
        }
        finally { DeleteFixtureRoot(fixtureRoot); }
    }

    [TestMethod]
    public void WorkerHostLogging_ConfiguresConsoleStderrThresholdAndLeavesProtocolEmittersAbsent()
    {
        var fixtureRoot = CreateFixtureRoot();

        try
        {
            using var host = WorkerHostFactory.Build(CreateContext(fixtureRoot));
            var consoleOptions = host.Services
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.Extensions.Logging.Console.ConsoleLoggerOptions>>()
                .Value;

            Assert.AreEqual(Microsoft.Extensions.Logging.LogLevel.Trace, consoleOptions.LogToStandardErrorThreshold, "worker-console-stderr-threshold");
            Assert.IsNull(host.Services.GetService<ImmichReverseGeo.Core.Processing.IProcessingEventReporter>(), "worker-stdout-reporter-unregistered");
            Assert.IsNull(host.Services.GetService(typeof(ImmichReverseGeo.Core.WorkerProtocol.WorkerProtocolCodec)), "worker-stdout-protocol-codec-unregistered");
            Assert.IsNull(host.Services.GetService(typeof(ImmichReverseGeo.Core.WorkerProtocol.WorkerProtocolMapper)), "worker-stdout-protocol-mapper-unregistered");
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    [TestMethod]
    public void ClosedInitialAcquisitionRows_PreserveLeaseAndFailureReferences()
    {
        var request = new ImmichReverseGeo.Core.Models.ProcessingRunRequest(
            Guid.Parse("84b94b54-b7c5-49cf-a7dd-2b4a006ba1ae"),
            ImmichReverseGeo.Core.Models.ProcessingRunTrigger.RunOnce);
        var lease = new ReferenceLease(request);
        var failure = WorkerSafeFailure.Acquisition();
        var accepted = InitialProcessingRunAcquisition.Accept(lease);
        var preRequestFailure = InitialProcessingRunAcquisition.Fail(failure);

        Assert.AreSame(lease, accepted.Lease, "worker-accepted-lease-reference");
        Assert.AreSame(request, accepted.Lease.Request, "worker-accepted-request-reference");
        Assert.AreSame(failure, preRequestFailure.Failure, "worker-acquisition-failure-reference");
        Assert.IsInstanceOfType<InitialProcessingRunAcquisition.PreRequestEof>(InitialProcessingRunAcquisition.EndOfInput(), "worker-eof-row");
    }

    [TestMethod]
    public async Task SkippedAssetsInitialisation_PreCancelledTokenStopsBeforeFilesystemOrSqlite()
    {
        var fixtureRoot = CreateFixtureRoot();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var dataDirectory = Path.Combine(fixtureRoot, "worker-data");
        var repository = new ImmichReverseGeo.Web.Services.SkippedAssetsRepository(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ImmichReverseGeo.Web.Services.SkippedAssetsRepository>.Instance,
            dataDirectory);
        var initializer = new SkippedAssetsWorkerStartupInitializer(repository);

        try
        {
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(
                () => initializer.InitialiseAsync(cancellation.Token),
                "worker-skipped-adapter-cancellation");
            Assert.IsFalse(Directory.Exists(dataDirectory), "worker-skipped-adapter-no-directory");
            Assert.IsFalse(File.Exists(Path.Combine(dataDirectory, "skipped.db")), "worker-skipped-adapter-no-sqlite");
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    [TestMethod]
    public async Task TransitionalProductionHost_InitialisesThenCoordinatesOnlyStableUnavailableOutcome()
    {
        var fixtureRoot = CreateFixtureRoot();
        var calls = new List<string>();

        try
        {
            using var host = BuildWithReplacements(
                fixtureRoot,
                services =>
                {
                    ReplaceSingleton<IWorkerStartupInitializer>(services, new RecordingInitializer(calls));
                    ReplaceSingleton<IWorkerPreRequestFinality>(services, new RecordingPreRequestFinality(calls));
                });

            await host.StartAsync();
            await host.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));

            CollectionAssert.AreEqual(
                new[] { "initialise", "worker-transport-not-configured" },
                calls,
                "worker-transitional-order");
            Assert.IsNull(host.Services.GetService<ImmichReverseGeo.Core.Processing.IProcessingEventReporter>(), "worker-reporter-not-registered");
            Assert.IsNull(host.Services.GetService<IWorkerReadinessPublisher>(), "worker-readiness-not-registered");
            Assert.IsNull(host.Services.GetService<IInitialProcessingRunAcquirer>(), "worker-acquirer-not-registered");
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    [TestMethod]
    public async Task PreRequestFinality_StopRaceUsesNonCancelledTokenBeforeScopeDisposalAndLifecycleStop()
    {
        var fixtureRoot = CreateFixtureRoot();
        var finality = new GatedPreRequestFinality();

        try
        {
            var builder = WorkerHostFactory.CreateBuilder(CreateContext(fixtureRoot));
            ReplaceSingleton<IWorkerStartupInitializer>(builder.Services, new CompletedInitializer([]));
            ReplaceSingleton<IWorkerTransportAvailability>(builder.Services, new ConfiguredTransport());
            ReplaceSingleton<IWorkerReadinessPublisher>(builder.Services, new RecordingReadiness([], Task.CompletedTask));
            ReplaceSingleton<IInitialProcessingRunAcquirer>(builder.Services, new RecordingAcquirer([], Task.CompletedTask, InitialProcessingRunAcquisition.EndOfInput()));
            builder.Services.RemoveAll<IWorkerPreRequestFinality>();
            builder.Services.AddScoped<IWorkerPreRequestFinality>(_ => finality);
            using var host = builder.Build();

            await host.StartAsync();
            await finality.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stop = host.StopAsync();
            Assert.IsFalse(stop.IsCompleted, "worker-pre-request-finality-stop-pending");
            Assert.IsFalse(finality.CancellationToken.CanBeCanceled, "worker-pre-request-finality-cleanup-token");
            finality.Release();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(1, finality.CompleteCount, "worker-pre-request-finality-one-outcome");
            Assert.IsTrue(finality.Disposed, "worker-pre-request-finality-scope-disposed-after-completion");
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    [TestMethod]
    public async Task ConfiguredHost_WaitsForStartThenInitialisesReadiesAndAcquiresOnce()
    {
        var fixtureRoot = CreateFixtureRoot();
        var calls = new List<string>();
        var initializer = new GatedInitializer(calls);
        var readiness = new RecordingReadiness(calls, initializer.Completed);
        var acquirer = new RecordingAcquirer(calls, readiness.Completed, InitialProcessingRunAcquisition.EndOfInput());
        var finality = new RecordingPreRequestFinality(calls);

        try
        {
            using var host = BuildConfiguredHost(fixtureRoot, initializer, readiness, acquirer, finality);
            await host.StartAsync();
            await initializer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            CollectionAssert.AreEqual(new[] { "initialise-started" }, calls, "worker-before-initialisation-release");

            initializer.Release();
            await host.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));

            CollectionAssert.AreEqual(
                new[] { "initialise-started", "initialise-completed", "ready", "acquire", "worker-input-closed" },
                calls,
                "worker-configured-order");
            Assert.AreEqual(1, acquirer.CallCount, "worker-one-acquisition");
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    [TestMethod]
    public async Task ConfiguredReadiness_DoesNotResolveLazyProcessingDependencies()
    {
        var fixtureRoot = CreateFixtureRoot();
        var calls = new List<string>();
        var initializer = new CompletedInitializer(calls);
        var readiness = new RecordingReadiness(calls, Task.CompletedTask);
        var acquirer = new RecordingAcquirer(calls, readiness.Completed, InitialProcessingRunAcquisition.EndOfInput());
        var finality = new RecordingPreRequestFinality(calls);

        try
        {
            var builder = WorkerHostFactory.CreateBuilder(CreateContext(fixtureRoot));
            ReplaceSingleton<IWorkerStartupInitializer>(builder.Services, initializer);
            ReplaceSingleton<IWorkerTransportAvailability>(builder.Services, new ConfiguredTransport());
            ReplaceSingleton<IWorkerReadinessPublisher>(builder.Services, readiness);
            ReplaceSingleton<IInitialProcessingRunAcquirer>(builder.Services, acquirer);
            ReplaceSingleton<IWorkerPreRequestFinality>(builder.Services, finality);
            builder.Services.RemoveAll<ImmichReverseGeo.Web.Services.CountryCodeService>();
            builder.Services.AddSingleton<ImmichReverseGeo.Web.Services.CountryCodeService>(_ => throw new AssertFailedException("worker-readiness-country-index"));
            builder.Services.RemoveAll<ImmichReverseGeo.Web.Services.IProcessingRunExecutor>();
            builder.Services.AddSingleton<ImmichReverseGeo.Web.Services.IProcessingRunExecutor>(_ => throw new AssertFailedException("worker-readiness-executor"));
            builder.Services.RemoveAll<ImmichReverseGeo.Web.Services.IProcessingAssetRepository>();
            builder.Services.AddSingleton<ImmichReverseGeo.Web.Services.IProcessingAssetRepository>(_ => throw new AssertFailedException("worker-readiness-postgresql"));
            using var host = builder.Build();

            await host.StartAsync();
            await host.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));

            CollectionAssert.AreEqual(new[] { "initialise", "ready", "acquire", "worker-input-closed" }, calls, "worker-readiness-lazy-order");
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    [TestMethod]
    public async Task StartupFailure_ReportsOneSafeFailureBeforeReadiness()
    {
        var fixtureRoot = CreateFixtureRoot();
        var calls = new List<string>();
        var readiness = new RecordingReadiness(calls, Task.CompletedTask);
        var acquirer = new RecordingAcquirer(calls, readiness.Completed, InitialProcessingRunAcquisition.EndOfInput());
        var finality = new RecordingPreRequestFinality(calls);

        try
        {
            using var host = BuildConfiguredHost(fixtureRoot, new ThrowingInitializer(calls), readiness, acquirer, finality);
            await host.StartAsync();
            await host.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));

            CollectionAssert.AreEqual(new[] { "initialise", "worker-startup-failed" }, calls, "worker-startup-failure-order");
            Assert.AreEqual(0, acquirer.CallCount, "worker-startup-failure-no-acquisition");
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    [TestMethod]
    public async Task ReadinessFailure_ReportsOneSafeFailureAndDoesNotAcquire()
    {
        var fixtureRoot = CreateFixtureRoot();
        var calls = new List<string>();
        var initializer = new CompletedInitializer(calls);
        var readiness = new ThrowingReadiness(calls);
        var acquirer = new RecordingAcquirer(calls, Task.CompletedTask, InitialProcessingRunAcquisition.EndOfInput());
        var finality = new RecordingPreRequestFinality(calls);

        try
        {
            using var host = BuildConfiguredHost(fixtureRoot, initializer, readiness, acquirer, finality);
            await host.StartAsync();
            await host.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));

            CollectionAssert.AreEqual(
                new[] { "initialise", "ready", "worker-readiness-failed" },
                calls,
                "worker-readiness-failure-order");
            Assert.AreEqual(0, acquirer.CallCount, "worker-readiness-failure-no-acquisition");
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    [TestMethod]
    public async Task AcquisitionThrow_ReportsOneSafeFailure()
    {
        var fixtureRoot = CreateFixtureRoot();
        var calls = new List<string>();
        var initializer = new CompletedInitializer(calls);
        var readiness = new RecordingReadiness(calls, Task.CompletedTask);
        var finality = new RecordingPreRequestFinality(calls);

        try
        {
            using var host = BuildConfiguredHost(fixtureRoot, initializer, readiness, new ThrowingAcquirer(calls, readiness.Completed), finality);
            await host.StartAsync();
            await host.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));

            CollectionAssert.AreEqual(new[] { "initialise", "ready", "acquire", "worker-request-acquisition-failed" }, calls, "worker-acquisition-throw-order");
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    [TestMethod]
    public async Task AcquisitionFailure_PreservesTheExactSafeFailureObjectForFinality()
    {
        var fixtureRoot = CreateFixtureRoot();
        var calls = new List<string>();
        var failure = WorkerSafeFailure.Acquisition();
        var initializer = new CompletedInitializer(calls);
        var readiness = new RecordingReadiness(calls, Task.CompletedTask);
        var acquirer = new RecordingAcquirer(calls, readiness.Completed, InitialProcessingRunAcquisition.Fail(failure));
        var finality = new RecordingPreRequestFinality(calls);

        try
        {
            using var host = BuildConfiguredHost(fixtureRoot, initializer, readiness, acquirer, finality);
            await host.StartAsync();
            await host.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(1, finality.Outcomes.Count, "worker-failure-finality-count");
            Assert.AreSame(failure, finality.Outcomes[0].SafeFailure, "worker-failure-reference");
            Assert.AreEqual("worker-request-acquisition-failed", finality.Outcomes[0].Category, "worker-failure-category");
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    [TestMethod]
    public async Task PreRequestEof_DisposesScopeBeforeStopApplication()
    {
        var fixtureRoot = CreateFixtureRoot();
        var calls = new List<string>();
        var finality = new AsyncDisposableFinality(calls);

        try
        {
            var builder = WorkerHostFactory.CreateBuilder(CreateContext(fixtureRoot));
            ReplaceSingleton<IWorkerStartupInitializer>(builder.Services, new CompletedInitializer(calls));
            ReplaceSingleton<IWorkerTransportAvailability>(builder.Services, new ConfiguredTransport());
            ReplaceSingleton<IWorkerReadinessPublisher>(builder.Services, new RecordingReadiness(calls, Task.CompletedTask));
            ReplaceSingleton<IInitialProcessingRunAcquirer>(builder.Services, new RecordingAcquirer(calls, Task.CompletedTask, InitialProcessingRunAcquisition.EndOfInput()));
            builder.Services.RemoveAll<IWorkerPreRequestFinality>();
            builder.Services.AddScoped<IWorkerPreRequestFinality>(_ => finality);
            using var host = builder.Build();
            var stoppingAfterScopeDisposal = false;
            host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(
                () => stoppingAfterScopeDisposal = finality.Disposed);

            await host.StartAsync();
            await host.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.IsTrue(finality.Disposed, "worker-pre-request-scope-disposed");
            Assert.IsTrue(stoppingAfterScopeDisposal, "worker-scope-before-stop");
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    [TestMethod]
    public async Task HostStopDuringReadiness_CancelsWithoutFinalityOrAcquisition()
    {
        var fixtureRoot = CreateFixtureRoot();
        var calls = new List<string>();
        var initializer = new CompletedInitializer(calls);
        var readiness = new BlockingReadiness(calls);
        var acquirer = new RecordingAcquirer(calls, Task.CompletedTask, InitialProcessingRunAcquisition.EndOfInput());
        var finality = new RecordingPreRequestFinality(calls);

        try
        {
            using var host = BuildConfiguredHost(fixtureRoot, initializer, readiness, acquirer, finality);
            await host.StartAsync();
            await readiness.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

            CollectionAssert.AreEqual(new[] { "initialise", "ready-started" }, calls, "worker-stop-readiness-order");
            Assert.AreEqual(0, finality.Outcomes.Count, "worker-stop-readiness-no-finality");
            Assert.AreEqual(0, acquirer.CallCount, "worker-stop-readiness-no-acquisition");
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    [TestMethod]
    public async Task HostStopDuringAcquisition_CancelsWithoutFinality()
    {
        var fixtureRoot = CreateFixtureRoot();
        var calls = new List<string>();
        var initializer = new CompletedInitializer(calls);
        var readiness = new RecordingReadiness(calls, Task.CompletedTask);
        var acquirer = new BlockingAcquirer(calls, readiness.Completed);
        var finality = new RecordingPreRequestFinality(calls);

        try
        {
            using var host = BuildConfiguredHost(fixtureRoot, initializer, readiness, acquirer, finality);
            await host.StartAsync();
            await acquirer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

            CollectionAssert.AreEqual(new[] { "initialise", "ready", "acquire-started" }, calls, "worker-stop-acquire-order");
            Assert.AreEqual(0, finality.Outcomes.Count, "worker-stop-acquire-no-finality");
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    [TestMethod]
    public async Task AcceptedResult_InvokesExecutorAndCompletionFinalityOnceWithExactReferences()
    {
        var fixtureRoot = CreateFixtureRoot();
        var request = CreateRequest();
        var lease = new AcceptedLease(request);
        var executor = new RecordingExecutor((receivedRequest, _, cancellationToken) =>
        {
            Assert.IsFalse(cancellationToken.IsCancellationRequested, "worker-accepted-controls-closed-does-not-cancel");
            return Task.FromResult(CreateResult(receivedRequest, ImmichReverseGeo.Core.Models.ProcessingRunOutcome.Completed));
        });
        var finality = new RecordingAcceptedFinality();
        var reporter = new TestReporter();

        try
        {
            using var host = BuildAcceptedHost(fixtureRoot, lease, executor, finality, reporter: reporter);
            await host.StartAsync();
            await host.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(1, executor.CallCount, "worker-accepted-executor-count");
            Assert.AreSame(request, executor.Request, "worker-accepted-executor-request");
            Assert.AreSame(reporter, executor.Reporter, "worker-accepted-executor-reporter");
            Assert.AreEqual(1, finality.Completed.Count, "worker-accepted-completion-count");
            Assert.AreSame(request, finality.Completed[0].Request, "worker-accepted-finality-request");
            Assert.AreSame(executor.Result, finality.Completed[0].Result, "worker-accepted-finality-result");
            Assert.AreEqual(0, finality.Failures.Count, "worker-accepted-no-failure-finality");
            Assert.AreEqual(1, lease.SettleCount, "worker-accepted-settle-count");
            Assert.AreEqual(1, lease.DisposeCount, "worker-accepted-dispose-count");
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    [TestMethod]
    public async Task PreCancelledLease_CancelledResultCompletesFinalityWithExactReferencesAndCleansUp()
    {
        var fixtureRoot = CreateFixtureRoot();
        var leaseCancellation = new CancellationTokenSource();
        leaseCancellation.Cancel();
        var lease = new AcceptedLease(CreateRequest(), leaseCancellation.Token);
        var executor = new RecordingExecutor((request, _, token) =>
        {
            Assert.IsTrue(token.IsCancellationRequested, "worker-pre-cancelled-token");
            return Task.FromResult(CreateResult(request, ImmichReverseGeo.Core.Models.ProcessingRunOutcome.Cancelled));
        });
        var finality = new RecordingAcceptedFinality();
        var reporter = new TestReporter();

        try
        {
            using var host = BuildAcceptedHost(fixtureRoot, lease, executor, finality, reporter: reporter);
            await host.StartAsync();
            await host.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(1, executor.CallCount, "worker-pre-cancelled-executor-count");
            Assert.AreSame(lease.Request, executor.Request, "worker-pre-cancelled-exact-request");
            Assert.AreSame(reporter, executor.Reporter, "worker-pre-cancelled-exact-reporter");
            Assert.AreEqual(1, finality.Completed.Count, "worker-pre-cancelled-complete-count");
            Assert.AreSame(lease.Request, finality.Completed[0].Request, "worker-pre-cancelled-finality-request");
            Assert.AreSame(executor.Result, finality.Completed[0].Result, "worker-pre-cancelled-finality-result");
            Assert.AreEqual(ImmichReverseGeo.Core.Models.ProcessingRunOutcome.Cancelled, finality.Completed[0].Result.Outcome, "worker-pre-cancelled-result-outcome");
            Assert.AreEqual(0, finality.Failures.Count, "worker-pre-cancelled-no-failure");
            Assert.AreEqual(1, lease.SettleCount, "worker-pre-cancelled-settle");
            Assert.AreEqual(1, lease.DisposeCount, "worker-pre-cancelled-dispose");
        }
        finally
        {
            leaseCancellation.Dispose();
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    [TestMethod]
    public async Task UnrelatedCancellation_ExceptionCoordinatesOneAcceptedFailure()
    {
        var fixtureRoot = CreateFixtureRoot();
        var lease = new AcceptedLease(CreateRequest());
        var executor = new RecordingExecutor((_, _, _) => throw new OperationCanceledException("unrelated"));
        var finality = new RecordingAcceptedFinality();

        try
        {
            using var host = BuildAcceptedHost(fixtureRoot, lease, executor, finality);
            await host.StartAsync();
            await host.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(1, finality.Failures.Count, "worker-unrelated-cancellation-failure-count");
            Assert.AreSame(lease.Request, finality.Failures[0].Request, "worker-unrelated-cancellation-request");
            Assert.AreEqual("worker-accepted-infrastructure-failed", finality.Failures[0].Failure.Category, "worker-unrelated-cancellation-category");
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    [TestMethod]
    public async Task ReporterInfrastructureFault_CoordinatesOneAcceptedFailure()
    {
        var fixtureRoot = CreateFixtureRoot();
        var lease = new AcceptedLease(CreateRequest());
        var finality = new RecordingAcceptedFinality();
        var executor = new RecordingExecutor(async (request, reporter, token) =>
        {
            await reporter.OpenRunAsync(request, DateTimeOffset.UnixEpoch, token);
            return CreateResult(request, ImmichReverseGeo.Core.Models.ProcessingRunOutcome.Completed);
        });

        try
        {
            using var host = BuildAcceptedHost(fixtureRoot, lease, executor, finality);
            await host.StartAsync();
            await host.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(1, finality.Failures.Count, "worker-reporter-fault-one-failure-finality");
            Assert.AreEqual(0, finality.Completed.Count, "worker-reporter-fault-no-completion-finality");
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    [TestMethod]
    public async Task CompletionFinalityFault_DoesNotRetryOrInvokeFailureFinality()
    {
        var fixtureRoot = CreateFixtureRoot();
        var finality = new ThrowingAcceptedFinality(throwOnCompletion: true);
        var lease = new AcceptedLease(CreateRequest());
        var executor = new RecordingExecutor((request, _, _) => Task.FromResult(CreateResult(request, ImmichReverseGeo.Core.Models.ProcessingRunOutcome.Completed)));

        try
        {
            using var host = BuildAcceptedHost(fixtureRoot, lease, executor, finality);
            await host.StartAsync();
            await host.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(1, finality.CompleteCount, "worker-completion-fault-one-call");
            Assert.AreEqual(0, finality.FailureCount, "worker-completion-fault-no-failure-retry");
            Assert.AreEqual(1, lease.SettleCount, "worker-completion-fault-settle");
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    [TestMethod]
    public async Task FailureFinalityFault_DoesNotRetryAndStillReleasesLease()
    {
        var fixtureRoot = CreateFixtureRoot();
        var finality = new ThrowingAcceptedFinality(throwOnCompletion: false);
        var lease = new AcceptedLease(CreateRequest());
        var executor = new RecordingExecutor((_, _, _) => throw new InvalidOperationException("executor failure"));

        try
        {
            using var host = BuildAcceptedHost(fixtureRoot, lease, executor, finality);
            await host.StartAsync();
            await host.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(0, finality.CompleteCount, "worker-failure-fault-no-completion");
            Assert.AreEqual(1, finality.FailureCount, "worker-failure-fault-one-call");
            Assert.AreEqual(1, lease.DisposeCount, "worker-failure-fault-dispose");
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    [TestMethod]
    public async Task CompletionFinality_HostStopRaceUsesNonCancelledTokenAndSettlesBeforeScopeDisposal()
    {
        var fixtureRoot = CreateFixtureRoot();
        var lease = new AcceptedLease(CreateRequest());
        var finality = new GatedAcceptedFinality();
        var executor = new RecordingExecutor((request, _, _) => Task.FromResult(CreateResult(request, ImmichReverseGeo.Core.Models.ProcessingRunOutcome.Completed)));

        try
        {
            var builder = CreateAcceptedBuilder(fixtureRoot, lease, executor, finality);
            builder.Services.RemoveAll<IWorkerAcceptedRunFinality>();
            builder.Services.AddScoped<IWorkerAcceptedRunFinality>(_ => finality);
            using var host = builder.Build();
            await host.StartAsync();
            await finality.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stop = host.StopAsync();
            Assert.IsFalse(stop.IsCompleted, "worker-finality-stop-waits");
            finality.Release();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.IsFalse(finality.CancellationToken.CanBeCanceled, "worker-finality-cleanup-token");
            Assert.AreEqual(1, finality.CompleteCount, "worker-finality-one-completion");
            Assert.AreEqual(1, lease.SettleCount, "worker-finality-settle-after-completion");
            Assert.IsTrue(finality.Disposed, "worker-finality-scope-disposed-after-completion");
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    [TestMethod]
    public async Task LeaseCancellationDuringExecution_PropagatesThroughLinkedTokenWithoutSecondAcquisition()
    {
        var fixtureRoot = CreateFixtureRoot();
        using var leaseCancellation = new CancellationTokenSource();
        var lease = new AcceptedLease(CreateRequest(), leaseCancellation.Token);
        var executor = new BlockingExecutor();
        var acquirer = new RecordingAcquirer([], Task.CompletedTask, InitialProcessingRunAcquisition.Accept(lease));

        try
        {
            using var host = BuildAcceptedHost(fixtureRoot, lease, executor, new RecordingAcceptedFinality(), acquirer);
            await host.StartAsync();
            await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            leaseCancellation.Cancel();
            await host.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.IsTrue(executor.TokenWasCancelled, "worker-lease-cancellation-linked-token");
            Assert.AreEqual(1, acquirer.CallCount, "worker-lease-cancellation-one-acquisition");
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    [TestMethod]
    public async Task HostStopDuringExecution_PropagatesThroughTheLinkedToken()
    {
        var fixtureRoot = CreateFixtureRoot();
        var lease = new AcceptedLease(CreateRequest());
        var executor = new BlockingExecutor();

        try
        {
            using var host = BuildAcceptedHost(fixtureRoot, lease, executor, new RecordingAcceptedFinality());
            await host.StartAsync();
            await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.IsTrue(executor.TokenWasCancelled, "worker-host-stop-linked-token");
            Assert.AreEqual(1, lease.SettleCount, "worker-host-stop-lease-settled");
            Assert.AreEqual(1, lease.DisposeCount, "worker-host-stop-lease-disposed");
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    [TestMethod]
    public async Task Runner_DisposesRealHostProviderOwnedAsyncResource()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<AsyncDisposeSentinel>();
        using var host = builder.Build();
        var sentinel = host.Services.GetRequiredService<AsyncDisposeSentinel>();

        var applicationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.ApplicationStarted.Register(() => applicationStarted.TrySetResult());
        var run = WorkerHostFactory.RunHostAsync(host);
        await applicationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lifetime.StopApplication();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(sentinel.Disposed, "worker-runner-async-provider-disposal");
    }

    private static IHost BuildAcceptedHost(
        string fixtureRoot,
        IProcessingRunLease lease,
        ImmichReverseGeo.Web.Services.IProcessingRunExecutor executor,
        IWorkerAcceptedRunFinality finality,
        IInitialProcessingRunAcquirer? acquirer = null,
        ImmichReverseGeo.Core.Processing.IProcessingEventReporter? reporter = null)
    {
        return CreateAcceptedBuilder(fixtureRoot, lease, executor, finality, acquirer, reporter).Build();
    }

    private static HostApplicationBuilder CreateAcceptedBuilder(
        string fixtureRoot,
        IProcessingRunLease lease,
        ImmichReverseGeo.Web.Services.IProcessingRunExecutor executor,
        IWorkerAcceptedRunFinality finality,
        IInitialProcessingRunAcquirer? acquirer = null,
        ImmichReverseGeo.Core.Processing.IProcessingEventReporter? reporter = null)
    {
        var builder = WorkerHostFactory.CreateBuilder(CreateContext(fixtureRoot));
        ReplaceSingleton<IWorkerStartupInitializer>(builder.Services, new CompletedInitializer([]));
        ReplaceSingleton<IWorkerTransportAvailability>(builder.Services, new ConfiguredTransport());
        ReplaceSingleton<IWorkerReadinessPublisher>(builder.Services, new RecordingReadiness([], Task.CompletedTask));
        ReplaceSingleton<IInitialProcessingRunAcquirer>(builder.Services, acquirer ?? new RecordingAcquirer([], Task.CompletedTask, InitialProcessingRunAcquisition.Accept(lease)));
        ReplaceSingleton<IWorkerPreRequestFinality>(builder.Services, new RecordingPreRequestFinality([]));
        ReplaceSingleton<IWorkerAcceptedRunFinality>(builder.Services, finality);
        ReplaceSingleton<ImmichReverseGeo.Web.Services.IProcessingRunExecutor>(builder.Services, executor);
        ReplaceSingleton<ImmichReverseGeo.Core.Processing.IProcessingEventReporter>(builder.Services, reporter ?? new TestReporter());
        return builder;
    }

    private static ImmichReverseGeo.Core.Models.ProcessingRunRequest CreateRequest()
    {
        return new ImmichReverseGeo.Core.Models.ProcessingRunRequest(
            Guid.Parse("6ed5cbcf-068d-40d5-b36a-badfcdfb0e76"),
            ImmichReverseGeo.Core.Models.ProcessingRunTrigger.RunOnce);
    }

    private static ImmichReverseGeo.Core.Models.ProcessingRunResult CreateResult(
        ImmichReverseGeo.Core.Models.ProcessingRunRequest request,
        ImmichReverseGeo.Core.Models.ProcessingRunOutcome outcome)
    {
        return new ImmichReverseGeo.Core.Models.ProcessingRunResult(
            request,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            0,
            0,
            0,
            0,
            outcome,
            outcome == ImmichReverseGeo.Core.Models.ProcessingRunOutcome.Failed ? "failed" : null);
    }

    private static IHost BuildConfiguredHost(
        string fixtureRoot,
        IWorkerStartupInitializer initializer,
        IWorkerReadinessPublisher readiness,
        IInitialProcessingRunAcquirer acquirer,
        IWorkerPreRequestFinality finality)
    {
        return BuildWithReplacements(
            fixtureRoot,
            services =>
            {
                ReplaceSingleton<IWorkerStartupInitializer>(services, initializer);
                ReplaceSingleton<IWorkerTransportAvailability>(services, new ConfiguredTransport());
                ReplaceSingleton<IWorkerReadinessPublisher>(services, readiness);
                ReplaceSingleton<IInitialProcessingRunAcquirer>(services, acquirer);
                ReplaceSingleton<IWorkerPreRequestFinality>(services, finality);
            });
    }

    private static IHost BuildWithReplacements(string fixtureRoot, Action<IServiceCollection> replace)
    {
        var builder = WorkerHostFactory.CreateBuilder(CreateContext(fixtureRoot));
        replace(builder.Services);
        return builder.Build();
    }

    private static void ReplaceSingleton<TService>(IServiceCollection services, TService instance)
        where TService : class
    {
        services.RemoveAll<TService>();
        services.AddSingleton(instance);
    }

    private static void AssertDescriptor(IServiceCollection descriptors, Type serviceType, ServiceLifetime lifetime)
    {
        var matches = descriptors.Where(descriptor => descriptor.ServiceType == serviceType).ToArray();
        Assert.AreEqual(1, matches.Length, $"worker-descriptor-{serviceType.Name}-count");
        Assert.AreEqual(lifetime, matches[0].Lifetime, $"worker-descriptor-{serviceType.Name}-lifetime");
    }

    private static ApplicationCompositionContext CreateContext(string fixtureRoot)
    {
        return ApplicationCompositionContext.Create(CompositionEnvironment.Development, fixtureRoot, null, null);
    }

    private static string CreateFixtureRoot()
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
        return fixtureRoot;
    }

    private static void DeleteFixtureRoot(string fixtureRoot)
    {
        if (Directory.Exists(fixtureRoot))
        {
            Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    private sealed class AcceptedLease : IProcessingRunLease
    {
        private readonly List<string>? _ledger;

        public AcceptedLease(ImmichReverseGeo.Core.Models.ProcessingRunRequest request, CancellationToken cancellationToken = default, List<string>? ledger = null)
        {
            Request = request;
            CancellationToken = cancellationToken;
            _ledger = ledger;
        }

        public int SettleCount { get; private set; }

        public int DisposeCount { get; private set; }

        public ImmichReverseGeo.Core.Models.ProcessingRunRequest Request { get; }

        public CancellationToken CancellationToken { get; }

        public ValueTask SettleAsync(CancellationToken cancellationToken)
        {
            SettleCount++;
            _ledger?.Add("settle");
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            _ledger?.Add("lease-dispose");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingExecutor(
        Func<ImmichReverseGeo.Core.Models.ProcessingRunRequest, ImmichReverseGeo.Core.Processing.IProcessingEventReporter, CancellationToken, Task<ImmichReverseGeo.Core.Models.ProcessingRunResult>> execute)
        : ImmichReverseGeo.Web.Services.IProcessingRunExecutor
    {
        public int CallCount { get; private set; }

        public ImmichReverseGeo.Core.Models.ProcessingRunRequest? Request { get; private set; }

        public ImmichReverseGeo.Core.Models.ProcessingRunResult? Result { get; private set; }

        public ImmichReverseGeo.Core.Processing.IProcessingEventReporter? Reporter { get; private set; }

        public async Task<ImmichReverseGeo.Core.Models.ProcessingRunResult> ExecuteAsync(
            ImmichReverseGeo.Core.Models.ProcessingRunRequest request,
            ImmichReverseGeo.Core.Processing.IProcessingEventReporter reporter,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Request = request;
            Reporter = reporter;
            Result = await execute(request, reporter, cancellationToken);
            return Result;
        }
    }

    private sealed class BlockingExecutor : ImmichReverseGeo.Web.Services.IProcessingRunExecutor
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool TokenWasCancelled { get; private set; }

        public async Task<ImmichReverseGeo.Core.Models.ProcessingRunResult> ExecuteAsync(
            ImmichReverseGeo.Core.Models.ProcessingRunRequest request,
            ImmichReverseGeo.Core.Processing.IProcessingEventReporter reporter,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();

            try
            {
                await _completion.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TokenWasCancelled = true;
                throw;
            }

            return CreateResult(request, ImmichReverseGeo.Core.Models.ProcessingRunOutcome.Completed);
        }
    }

    private sealed class RecordingAcceptedFinality(List<string>? ledger = null) : IWorkerAcceptedRunFinality
    {
        public List<(ImmichReverseGeo.Core.Models.ProcessingRunRequest Request, ImmichReverseGeo.Core.Models.ProcessingRunResult Result)> Completed { get; } = [];

        public List<(ImmichReverseGeo.Core.Models.ProcessingRunRequest Request, WorkerSafeFailure Failure)> Failures { get; } = [];

        public Task CompleteAsync(ImmichReverseGeo.Core.Models.ProcessingRunRequest request, ImmichReverseGeo.Core.Models.ProcessingRunResult result, CancellationToken cancellationToken)
        {
            Completed.Add((request, result));
            return Task.CompletedTask;
        }

        public Task FailAsync(ImmichReverseGeo.Core.Models.ProcessingRunRequest request, WorkerSafeFailure failure, CancellationToken cancellationToken)
        {
            Failures.Add((request, failure));
            ledger?.Add("failure-finality");
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingAcceptedFinality(bool throwOnCompletion) : IWorkerAcceptedRunFinality
    {
        public int CompleteCount { get; private set; }

        public int FailureCount { get; private set; }

        public Task CompleteAsync(ImmichReverseGeo.Core.Models.ProcessingRunRequest request, ImmichReverseGeo.Core.Models.ProcessingRunResult result, CancellationToken cancellationToken)
        {
            CompleteCount++;

            if (throwOnCompletion)
            {
                throw new InvalidOperationException("completion finality failure");
            }

            return Task.CompletedTask;
        }

        public Task FailAsync(ImmichReverseGeo.Core.Models.ProcessingRunRequest request, WorkerSafeFailure failure, CancellationToken cancellationToken)
        {
            FailureCount++;

            if (!throwOnCompletion)
            {
                throw new InvalidOperationException("failure finality failure");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class GatedAcceptedFinality : IWorkerAcceptedRunFinality, IAsyncDisposable
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CompleteCount { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public bool Disposed { get; private set; }

        public async Task CompleteAsync(ImmichReverseGeo.Core.Models.ProcessingRunRequest request, ImmichReverseGeo.Core.Models.ProcessingRunResult result, CancellationToken cancellationToken)
        {
            CompleteCount++;
            CancellationToken = cancellationToken;
            Started.TrySetResult();
            await _release.Task;
        }

        public Task FailAsync(ImmichReverseGeo.Core.Models.ProcessingRunRequest request, WorkerSafeFailure failure, CancellationToken cancellationToken)
        {
            throw new AssertFailedException("worker-gated-finality-failure-not-expected");
        }

        public void Release()
        {
            _release.TrySetResult();
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestReporter : ImmichReverseGeo.Core.Processing.IProcessingEventReporter
    {
        public ValueTask<ImmichReverseGeo.Core.Processing.IProcessingRunEventSession> OpenRunAsync(
            ImmichReverseGeo.Core.Models.ProcessingRunRequest request,
            DateTimeOffset startedAtUtc,
            CancellationToken cancellationToken = default)
        {
            throw new AssertFailedException("worker-test-reporter-must-not-be-used-by-fake-executor");
        }
    }

    private sealed class AcceptedScopeSentinel : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class GatedPreRequestFinality : IWorkerPreRequestFinality, IAsyncDisposable
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CompleteCount { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public bool Disposed { get; private set; }

        public async Task CompleteAsync(WorkerPreRequestOutcome outcome, CancellationToken cancellationToken)
        {
            CompleteCount++;
            CancellationToken = cancellationToken;
            Started.TrySetResult();
            await _release.Task;
        }

        public void Release()
        {
            _release.TrySetResult();
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ReferenceLease(ImmichReverseGeo.Core.Models.ProcessingRunRequest request) : IProcessingRunLease
    {
        public ImmichReverseGeo.Core.Models.ProcessingRunRequest Request { get; } = request;

        public CancellationToken CancellationToken => CancellationToken.None;

        public ValueTask SettleAsync(CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ConfiguredTransport : IWorkerTransportAvailability
    {
        public bool IsConfigured => true;
    }

    private sealed class CompletedInitializer(List<string> calls) : IWorkerStartupInitializer
    {
        public Task InitialiseAsync(CancellationToken cancellationToken)
        {
            calls.Add("initialise");
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingInitializer(List<string> calls) : IWorkerStartupInitializer
    {
        public Task InitialiseAsync(CancellationToken cancellationToken)
        {
            calls.Add("initialise");
            throw new InvalidOperationException("test startup failure");
        }
    }

    private sealed class GatedInitializer(List<string> calls) : IWorkerStartupInitializer
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Completed => _release.Task;

        public async Task InitialiseAsync(CancellationToken cancellationToken)
        {
            calls.Add("initialise-started");
            Started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            calls.Add("initialise-completed");
        }

        public void Release()
        {
            _release.TrySetResult();
        }
    }

    private sealed class RecordingReadiness(List<string> calls, Task initializerCompleted) : IWorkerReadinessPublisher
    {
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Completed => _completed.Task;

        public async Task PublishAsync(CancellationToken cancellationToken)
        {
            Assert.IsTrue(initializerCompleted.IsCompletedSuccessfully, "worker-init-before-ready");
            calls.Add("ready");
            _completed.TrySetResult();
            await Task.CompletedTask;
        }
    }

    private sealed class ThrowingReadiness(List<string> calls) : IWorkerReadinessPublisher
    {
        public Task PublishAsync(CancellationToken cancellationToken)
        {
            calls.Add("ready");
            throw new InvalidOperationException("test readiness failure");
        }
    }

    private sealed class BlockingReadiness(List<string> calls) : IWorkerReadinessPublisher
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task PublishAsync(CancellationToken cancellationToken)
        {
            calls.Add("ready-started");
            Started.TrySetResult();
            await _completion.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class RecordingAcquirer(List<string> calls, Task readinessCompleted, InitialProcessingRunAcquisition outcome) : IInitialProcessingRunAcquirer
    {
        public TaskCompletionSource Acquired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount { get; private set; }

        public Task<InitialProcessingRunAcquisition> AcquireAsync(CancellationToken cancellationToken)
        {
            Assert.IsTrue(readinessCompleted.IsCompletedSuccessfully, "worker-ready-before-acquire");
            CallCount++;
            calls.Add("acquire");
            Acquired.TrySetResult();
            return Task.FromResult(outcome);
        }
    }

    private sealed class ThrowingAcquirer(List<string> calls, Task readinessCompleted) : IInitialProcessingRunAcquirer
    {
        public Task<InitialProcessingRunAcquisition> AcquireAsync(CancellationToken cancellationToken)
        {
            Assert.IsTrue(readinessCompleted.IsCompletedSuccessfully, "worker-ready-before-acquire");
            calls.Add("acquire");
            throw new InvalidOperationException("test acquisition failure");
        }
    }

    private sealed class BlockingAcquirer(List<string> calls, Task readinessCompleted) : IInitialProcessingRunAcquirer
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<InitialProcessingRunAcquisition> AcquireAsync(CancellationToken cancellationToken)
        {
            Assert.IsTrue(readinessCompleted.IsCompletedSuccessfully, "worker-ready-before-acquire");
            calls.Add("acquire-started");
            Started.TrySetResult();
            await _completion.Task.WaitAsync(cancellationToken);
            throw new AssertFailedException("worker-acquisition-should-cancel");
        }
    }

    private sealed class RecordingPreRequestFinality(List<string> calls) : IWorkerPreRequestFinality
    {
        public List<WorkerPreRequestOutcome> Outcomes { get; } = [];

        public Task CompleteAsync(WorkerPreRequestOutcome outcome, CancellationToken cancellationToken)
        {
            Outcomes.Add(outcome);
            calls.Add(outcome.Category);
            return Task.CompletedTask;
        }
    }

    private sealed class AsyncDisposableFinality(List<string> calls) : IWorkerPreRequestFinality, IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public Task CompleteAsync(WorkerPreRequestOutcome outcome, CancellationToken cancellationToken)
        {
            calls.Add(outcome.Category);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingInitializer(List<string> calls) : IWorkerStartupInitializer
    {
        public Task InitialiseAsync(CancellationToken cancellationToken)
        {
            calls.Add("initialise");
            return Task.CompletedTask;
        }
    }

    private sealed class AsyncDisposeSentinel : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    [TestMethod]
    public async Task DirectLifecycle_WaitsForApplicationStartedThenCreatesAndDisposesOneScopeAndStopsOnce()
    {
        var calls = new List<string>();
        var lifetime = new ControllableApplicationLifetime();
        await using var fixture = DirectLifecycleFixture.Create(
            lifetime,
            calls,
            InitialProcessingRunAcquisition.EndOfInput());

        await fixture.Service.StartAsync(CancellationToken.None);
        Assert.AreEqual(0, fixture.ScopeFactory.CreateCount, "worker-direct-before-start-no-scope");
        Assert.AreEqual(0, fixture.Initializer.CallCount, "worker-direct-before-start-no-initializer");

        lifetime.SignalStarted();
        await fixture.Initializer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Initializer.Release();
        await fixture.Acquirer.Acquired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        CollectionAssert.AreEqual(new[] { "initialise", "ready", "acquire", "worker-input-closed", "scope-dispose" }, calls, "worker-direct-eof-ledger");
        Assert.AreEqual(1, fixture.ScopeFactory.CreateCount, "worker-direct-one-scope");
        Assert.AreEqual(1, fixture.ScopeFactory.DisposeCount, "worker-direct-one-scope-dispose");
        Assert.AreEqual(1, lifetime.StopCount, "worker-direct-one-stop");
    }

    [TestMethod]
    public async Task DirectLifecycle_StopDuringInitialization_CancelsWithoutFinalityOrAcquisition()
    {
        var calls = new List<string>();
        var lifetime = new ControllableApplicationLifetime();
        await using var fixture = DirectLifecycleFixture.Create(lifetime, calls, InitialProcessingRunAcquisition.EndOfInput());

        await fixture.Service.StartAsync(CancellationToken.None);
        lifetime.SignalStarted();
        await fixture.Initializer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(1, fixture.ScopeFactory.CreateCount, "worker-direct-stop-during-initializer-one-scope");
        Assert.AreEqual(1, fixture.Initializer.CallCount, "worker-direct-stop-during-initializer-one-initializer");
        Assert.AreEqual(0, fixture.Finality.Outcomes.Count, "worker-direct-stop-during-initializer-no-finality");
        Assert.AreEqual(0, fixture.Acquirer.CallCount, "worker-direct-stop-during-initializer-no-acquire");
        Assert.AreEqual(1, lifetime.StopCount, "worker-direct-stop-during-initializer-one-stop");
    }

    [TestMethod]
    public async Task DirectScopeCreationFailure_PreservesIdentityAndDoesNoWork()
    {
        var scopePrimary = new InvalidOperationException("scope-create-primary");
        var scopeLifetime = new ControllableApplicationLifetime();
        await using var fixture = DirectLifecycleFixture.Create(scopeLifetime, [], InitialProcessingRunAcquisition.EndOfInput());
        fixture.ScopeFactory.CreateFailure = scopePrimary;
        await fixture.Service.StartAsync(CancellationToken.None);
        scopeLifetime.SignalStarted();
        var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5)), "worker-scope-create-primary");
        Assert.AreSame(scopePrimary, thrown, "worker-scope-create-reference");
        Assert.AreEqual(0, fixture.Initializer.CallCount, "worker-scope-create-no-initializer");
        Assert.AreEqual(0, fixture.Finality.Outcomes.Count, "worker-scope-create-no-finality");
        Assert.AreEqual(0, fixture.ScopeFactory.DisposeCount, "worker-scope-create-no-dispose");
        Assert.AreEqual(1, scopeLifetime.StopCount, "worker-scope-create-one-stop");
    }

    [TestMethod]
    public async Task DirectPreFinalityResolutionFailure_PreservesIdentityAndDisposesScope()
    {
        var finalityPrimary = new InvalidOperationException("pre-finality-primary");
        var finalityLifetime = new ControllableApplicationLifetime();
        var services = new ServiceCollection();
        services.AddSingleton<IWorkerPreRequestFinality>(_ => throw finalityPrimary);
        await using var provider = services.BuildServiceProvider();
        var factory = new CountingScopeFactory(provider, []);
        var service = new InternalWorkerLifecycleService(factory, finalityLifetime, Microsoft.Extensions.Logging.Abstractions.NullLogger<InternalWorkerLifecycleService>.Instance);
        await service.StartAsync(CancellationToken.None);
        finalityLifetime.SignalStarted();
        var finalityThrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5)), "worker-pre-finality-primary");
        Assert.AreSame(finalityPrimary, finalityThrown, "worker-pre-finality-reference");
        Assert.AreEqual(1, factory.DisposeCount, "worker-pre-finality-scope-dispose");
        Assert.AreEqual(1, finalityLifetime.StopCount, "worker-pre-finality-one-stop");
    }

    [TestMethod]
    [DataRow("initializer")]
    [DataRow("availability")]
    public async Task PreRequestInitializerAndAvailabilityResolutionFailures_ReportOneStartupFailure(string fault)
    {
        var fixtureRoot = CreateFixtureRoot();
        try
        {
                var calls = new List<string>();
                var finality = new RecordingPreRequestFinality(calls);
                var builder = WorkerHostFactory.CreateBuilder(CreateContext(fixtureRoot));
                ReplaceSingleton<IWorkerPreRequestFinality>(builder.Services, finality);
                if (fault == "initializer")
                {
                    builder.Services.RemoveAll<IWorkerStartupInitializer>();
                    builder.Services.AddSingleton<IWorkerStartupInitializer>(_ => throw new InvalidOperationException("initializer-resolution"));
                }
                else
                {
                    ReplaceSingleton<IWorkerStartupInitializer>(builder.Services, new CompletedInitializer(calls));
                    builder.Services.RemoveAll<IWorkerTransportAvailability>();
                    builder.Services.AddSingleton<IWorkerTransportAvailability>(_ => throw new InvalidOperationException("availability-resolution"));
                }

                using var host = builder.Build();
                await host.StartAsync();
                await host.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.AreEqual(1, finality.Outcomes.Count, $"worker-{fault}-resolution-finality-count");
                Assert.AreEqual("worker-startup-failed", finality.Outcomes[0].Category, $"worker-{fault}-resolution-category");
        }
        finally { DeleteFixtureRoot(fixtureRoot); }
    }

    [TestMethod]
    public async Task PreRequestFinalityResolutionFailure_StartsNoWorkAndStops()
    {
        var fixtureRoot = CreateFixtureRoot();
        var calls = new List<string>();
        try
        {
            var builder = WorkerHostFactory.CreateBuilder(CreateContext(fixtureRoot));
            builder.Services.RemoveAll<IWorkerPreRequestFinality>();
            builder.Services.AddSingleton<IWorkerPreRequestFinality>(_ => throw new InvalidOperationException("pre-finality-resolution"));
            ReplaceSingleton<IWorkerStartupInitializer>(builder.Services, new CompletedInitializer(calls));
            using var host = builder.Build();
            await host.StartAsync();
            await host.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(0, calls.Count, "worker-pre-finality-resolution-no-initializer");
        }
        finally { DeleteFixtureRoot(fixtureRoot); }
    }

    [TestMethod]
    public async Task AcquirerResolutionFailure_ReportsAcquisitionFailureWithoutResolvingReporter()
    {
        var fixtureRoot = CreateFixtureRoot();
        var calls = new List<string>();
        var finality = new RecordingPreRequestFinality(calls);
        var reporterResolutionCount = 0;
        try
        {
            var builder = WorkerHostFactory.CreateBuilder(CreateContext(fixtureRoot));
            ReplaceSingleton<IWorkerStartupInitializer>(builder.Services, new CompletedInitializer(calls));
            ReplaceSingleton<IWorkerTransportAvailability>(builder.Services, new ConfiguredTransport());
            ReplaceSingleton<IWorkerReadinessPublisher>(builder.Services, new RecordingReadiness(calls, Task.CompletedTask));
            builder.Services.RemoveAll<IInitialProcessingRunAcquirer>();
            builder.Services.AddSingleton<IInitialProcessingRunAcquirer>(_ => throw new InvalidOperationException("acquirer-resolution"));
            ReplaceSingleton<IWorkerPreRequestFinality>(builder.Services, finality);
            builder.Services.RemoveAll<ImmichReverseGeo.Core.Processing.IProcessingEventReporter>();
            builder.Services.AddSingleton<ImmichReverseGeo.Core.Processing.IProcessingEventReporter>(_ =>
            {
                reporterResolutionCount++;
                throw new AssertFailedException("worker-reporter-premature-resolution");
            });
            using var host = builder.Build();
            await host.StartAsync();
            await host.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(1, finality.Outcomes.Count, "worker-acquirer-resolution-finality-count");
            Assert.AreEqual("worker-request-acquisition-failed", finality.Outcomes[0].Category, "worker-acquirer-resolution-category");
            Assert.AreEqual(0, reporterResolutionCount, "worker-acquirer-resolution-no-reporter-resolution");
        }
        finally { DeleteFixtureRoot(fixtureRoot); }
    }

    [TestMethod]
    [DataRow("finality")]
    [DataRow("executor")]
    [DataRow("reporter")]
    public async Task AcceptedResolutionFailures_UseExactFailureOrCleanUpWithoutFabricatedHook(string failureKind)
    {
        var fixtureRoot = CreateFixtureRoot();
        try
        {
                var ledger = new List<string>();
                var lease = new AcceptedLease(CreateRequest(), ledger: ledger);
                var finality = new RecordingAcceptedFinality(ledger);
                var builder = CreateAcceptedBuilder(fixtureRoot, lease, new RecordingExecutor((request, _, _) => Task.FromResult(CreateResult(request, ImmichReverseGeo.Core.Models.ProcessingRunOutcome.Completed))), finality);
                if (failureKind == "finality")
                {
                    builder.Services.RemoveAll<IWorkerAcceptedRunFinality>();
                    builder.Services.AddSingleton<IWorkerAcceptedRunFinality>(_ => throw new InvalidOperationException("finality-resolution"));
                }
                else if (failureKind == "executor")
                {
                    builder.Services.RemoveAll<ImmichReverseGeo.Web.Services.IProcessingRunExecutor>();
                    builder.Services.AddSingleton<ImmichReverseGeo.Web.Services.IProcessingRunExecutor>(_ => throw new InvalidOperationException("executor-resolution"));
                }
                else
                {
                    builder.Services.RemoveAll<ImmichReverseGeo.Core.Processing.IProcessingEventReporter>();
                    builder.Services.AddSingleton<ImmichReverseGeo.Core.Processing.IProcessingEventReporter>(_ => throw new InvalidOperationException("reporter-resolution"));
                }

                using var host = builder.Build();
                await host.StartAsync();
                await host.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.AreEqual(1, lease.SettleCount, $"worker-{failureKind}-resolution-settle");
                Assert.AreEqual(1, lease.DisposeCount, $"worker-{failureKind}-resolution-dispose");
                Assert.AreEqual(failureKind == "finality" ? 0 : 1, finality.Failures.Count, $"worker-{failureKind}-resolution-failure-hook");
                Assert.AreEqual(0, finality.Completed.Count, $"worker-{failureKind}-resolution-no-complete");
                if (failureKind != "finality")
                {
                    Assert.AreSame(lease.Request, finality.Failures[0].Request, $"worker-{failureKind}-resolution-request");
                    Assert.IsNotNull(finality.Failures[0].Failure, $"worker-{failureKind}-resolution-failure-object");
                    Assert.AreEqual("worker-accepted-infrastructure-failed", finality.Failures[0].Failure.Category, $"worker-{failureKind}-resolution-failure-category");
                    CollectionAssert.AreEqual(new[] { "failure-finality", "settle", "lease-dispose" }, ledger, $"worker-{failureKind}-resolution-ledger");
                }
        }
        finally { DeleteFixtureRoot(fixtureRoot); }
    }

    [TestMethod]
    public async Task AcceptedFinalityLedger_DoesNotCleanBeforeGateAndOrdersCleanup()
    {
        var fixtureRoot = CreateFixtureRoot();
        var ledger = new List<string>();
        var lease = new LedgerLease(CreateRequest(), ledger);
        var finality = new LedgerFinality(ledger);
        try
        {
            var builder = CreateAcceptedBuilder(fixtureRoot, lease, new RecordingExecutor((request, _, _) => Task.FromResult(CreateResult(request, ImmichReverseGeo.Core.Models.ProcessingRunOutcome.Completed))), finality);
            builder.Services.RemoveAll<IWorkerAcceptedRunFinality>();
            builder.Services.AddScoped<IWorkerAcceptedRunFinality>(_ => finality);
            using var host = builder.Build();
            host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() => ledger.Add("stop"));
            await host.StartAsync();
            await finality.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            CollectionAssert.AreEqual(new[] { "complete" }, ledger, "worker-ledger-no-cleanup-while-gated");
            finality.Release();
            await host.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
            CollectionAssert.AreEqual(new[] { "complete", "settle", "lease-dispose", "scope-dispose", "stop" }, ledger, "worker-ledger-order");
        }
        finally { DeleteFixtureRoot(fixtureRoot); }
    }

    [TestMethod]
    [DataRow(true, "with-earlier-primary")]
    [DataRow(false, "cleanup-only")]
    public async Task StopApplicationFailure_PreservesEarlierPrimaryOrBecomesCleanupFailure(bool earlierPrimary, string label)
    {
            var calls = new List<string>();
            var lifetime = new ControllableApplicationLifetime { StopFailure = new InvalidOperationException("stop-secondary") };
            await using var fixture = DirectLifecycleFixture.Create(lifetime, calls, InitialProcessingRunAcquisition.EndOfInput());
            if (earlierPrimary)
            {
                fixture.ScopeFactory.CreateFailure = new InvalidOperationException("scope-primary");
            }
            await fixture.Service.StartAsync(CancellationToken.None);
            lifetime.SignalStarted();
            if (!earlierPrimary)
            {
                await fixture.Initializer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                fixture.Initializer.Release();
            }

            var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5)), $"worker-stop-failure-{label}");
            Assert.AreEqual(earlierPrimary ? "scope-primary" : "worker-cleanup-failed", thrown.Message, $"worker-stop-failure-category-{earlierPrimary}");
            Assert.AreEqual(1, lifetime.StopCount, $"worker-stop-failure-one-stop-{label}");
    }

    [TestMethod]
    public async Task DirectLifecycle_StopBeforeApplicationStarted_NeverCreatesScopeOrWork()
    {
        var calls = new List<string>();
        var lifetime = new ControllableApplicationLifetime();
        await using var fixture = DirectLifecycleFixture.Create(lifetime, calls, InitialProcessingRunAcquisition.EndOfInput());
        await fixture.Service.StartAsync(CancellationToken.None);
        await lifetime.ApplicationStartedObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(0, fixture.ScopeFactory.CreateCount, "worker-stop-before-start-no-scope");
        Assert.AreEqual(0, fixture.Initializer.CallCount, "worker-stop-before-start-no-initializer");
        Assert.AreEqual(0, fixture.Acquirer.CallCount, "worker-stop-before-start-no-acquisition");
        Assert.AreEqual(0, fixture.Finality.Outcomes.Count, "worker-stop-before-start-no-finality");
        Assert.AreEqual(1, lifetime.StopCount, "worker-stop-before-start-one-stop");
    }

    private sealed class ControllableApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();
        public TaskCompletionSource ApplicationStartedObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? StopFailure { get; set; }
        public int StopCount { get; private set; }
        public CancellationToken ApplicationStarted
        {
            get
            {
                ApplicationStartedObserved.TrySetResult();
                return _started.Token;
            }
        }
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;
        public void SignalStarted() => _started.Cancel();
        public void StopApplication()
        {
            StopCount++;
            _stopping.Cancel();
            _stopped.Cancel();
            if (StopFailure is not null)
            {
                throw StopFailure;
            }
        }
    }

    private sealed class DirectLifecycleFixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private DirectLifecycleFixture(ServiceProvider provider, InternalWorkerLifecycleService service, CountingScopeFactory scopeFactory, CountingInitializer initializer, RecordingAcquirer acquirer, RecordingPreRequestFinality finality)
        {
            _provider = provider;
            Service = service;
            ScopeFactory = scopeFactory;
            Initializer = initializer;
            Acquirer = acquirer;
            Finality = finality;
        }
        public InternalWorkerLifecycleService Service { get; }
        public CountingScopeFactory ScopeFactory { get; }
        public CountingInitializer Initializer { get; }
        public RecordingAcquirer Acquirer { get; }
        public RecordingPreRequestFinality Finality { get; }
        public static DirectLifecycleFixture Create(ControllableApplicationLifetime lifetime, List<string> calls, InitialProcessingRunAcquisition acquisition)
        {
            var services = new ServiceCollection();
            var initializer = new CountingInitializer(calls);
            var readiness = new RecordingReadiness(calls, Task.CompletedTask);
            var acquirer = new RecordingAcquirer(calls, readiness.Completed, acquisition);
            var finality = new RecordingPreRequestFinality(calls);
            services.AddSingleton<IWorkerStartupInitializer>(initializer);
            services.AddSingleton<IWorkerTransportAvailability>(new ConfiguredTransport());
            services.AddSingleton<IWorkerReadinessPublisher>(readiness);
            services.AddSingleton<IInitialProcessingRunAcquirer>(acquirer);
            services.AddSingleton<IWorkerPreRequestFinality>(finality);
            var provider = services.BuildServiceProvider();
            var factory = new CountingScopeFactory(provider, calls);
            return new DirectLifecycleFixture(provider, new InternalWorkerLifecycleService(factory, lifetime, Microsoft.Extensions.Logging.Abstractions.NullLogger<InternalWorkerLifecycleService>.Instance), factory, initializer, acquirer, finality);
        }
        public ValueTask DisposeAsync() => _provider.DisposeAsync();
    }

    private sealed class CountingScopeFactory(IServiceProvider provider, List<string> calls) : IServiceScopeFactory
    {
        public int CreateCount { get; private set; }
        public int DisposeCount { get; private set; }
        public Exception? CreateFailure { get; set; }
        public Exception? DisposeFailure { get; set; }
        public IServiceScope CreateScope()
        {
            CreateCount++;
            if (CreateFailure is not null)
            {
                throw CreateFailure;
            }
            return new CountingScope(provider, this, calls);
        }
        private sealed class CountingScope(IServiceProvider serviceProvider, CountingScopeFactory owner, List<string> calls) : IServiceScope, IAsyncDisposable
        {
            public IServiceProvider ServiceProvider { get; } = serviceProvider;
            public void Dispose() { }
            public ValueTask DisposeAsync()
            {
                owner.DisposeCount++;
                calls.Add("scope-dispose");
                return owner.DisposeFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(owner.DisposeFailure);
            }
        }
    }

    private sealed class CountingInitializer(List<string> calls) : IWorkerStartupInitializer
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount { get; private set; }
        public async Task InitialiseAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            calls.Add("initialise");
            Started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }
        public void Release() => _release.TrySetResult();
    }

    private sealed class LedgerLease(ImmichReverseGeo.Core.Models.ProcessingRunRequest request, List<string> ledger) : IProcessingRunLease
    {
        public ImmichReverseGeo.Core.Models.ProcessingRunRequest Request { get; } = request;
        public CancellationToken CancellationToken => CancellationToken.None;
        public ValueTask SettleAsync(CancellationToken cancellationToken) { ledger.Add("settle"); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() { ledger.Add("lease-dispose"); return ValueTask.CompletedTask; }
    }

    private sealed class LedgerFinality(List<string> ledger) : IWorkerAcceptedRunFinality, IAsyncDisposable
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task CompleteAsync(ImmichReverseGeo.Core.Models.ProcessingRunRequest request, ImmichReverseGeo.Core.Models.ProcessingRunResult result, CancellationToken cancellationToken)
        {
            ledger.Add("complete");
            Started.TrySetResult();
            await _release.Task;
        }
        public Task FailAsync(ImmichReverseGeo.Core.Models.ProcessingRunRequest request, WorkerSafeFailure failure, CancellationToken cancellationToken) => throw new AssertFailedException("worker-ledger-failure-unexpected");
        public void Release() => _release.TrySetResult();
        public ValueTask DisposeAsync() { ledger.Add("scope-dispose"); return ValueTask.CompletedTask; }
    }

    [TestMethod]
    [DataRow("failure-finality")]
    [DataRow("settle")]
    [DataRow("dispose")]
    [DataRow("scope")]
    public async Task AcceptedPrimaryAndEachSecondaryCleanupFault_PreservesPrimaryLogsOnceAndContinues(string secondary)
    {
            var calls = new List<string>();
            var lifetime = new ControllableApplicationLifetime();
            var logs = new CapturingLoggerProvider();
            var primary = new InvalidOperationException($"executor-primary-{secondary}");
            var services = new ServiceCollection();
            services.AddSingleton<IWorkerStartupInitializer>(new CompletedInitializer(calls));
            services.AddSingleton<IWorkerTransportAvailability>(new ConfiguredTransport());
            services.AddSingleton<IWorkerReadinessPublisher>(new RecordingReadiness(calls, Task.CompletedTask));
            var lease = new FaultingLease(CreateRequest(), secondary == "settle", secondary == "dispose");
            services.AddSingleton<IInitialProcessingRunAcquirer>(new RecordingAcquirer(calls, Task.CompletedTask, InitialProcessingRunAcquisition.Accept(lease)));
            services.AddSingleton<IWorkerPreRequestFinality>(new RecordingPreRequestFinality(calls));
            var finalityPrimary = secondary != "failure-finality";
            var finality = new FaultingAcceptedFinality(secondary == "failure-finality", finalityPrimary ? primary : null);
            services.AddSingleton<IWorkerAcceptedRunFinality>(finality);
            services.AddSingleton<ImmichReverseGeo.Web.Services.IProcessingRunExecutor>(new RecordingExecutor((request, _, _) => finalityPrimary
                ? Task.FromResult(CreateResult(request, ImmichReverseGeo.Core.Models.ProcessingRunOutcome.Completed))
                : throw primary));
            services.AddSingleton<ImmichReverseGeo.Core.Processing.IProcessingEventReporter>(new TestReporter());
            await using var provider = services.BuildServiceProvider();
            var scopeFactory = new CountingScopeFactory(provider, calls) { DisposeFailure = secondary == "scope" ? new InvalidOperationException("scope-secondary") : null };
            var logger = new CapturingLogger<InternalWorkerLifecycleService>(logs.Entries);
            var service = new InternalWorkerLifecycleService(scopeFactory, lifetime, logger);
            await service.StartAsync(CancellationToken.None);
            lifetime.SignalStarted();
            var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5)), $"worker-{secondary}-primary");

            Assert.AreSame(primary, thrown, $"worker-{secondary}-primary-reference");
            Assert.AreEqual(1, finalityPrimary ? finality.CompleteCount : finality.FailCount, $"worker-{secondary}-one-terminal-hook");
            Assert.AreEqual(1, lease.SettleCount, $"worker-{secondary}-settle-once");
            Assert.AreEqual(1, lease.DisposeCount, $"worker-{secondary}-dispose-once");
            Assert.AreEqual(1, scopeFactory.DisposeCount, $"worker-{secondary}-scope-once");
            Assert.AreEqual(1, lifetime.StopCount, $"worker-{secondary}-stop-once");
            var category = secondary == "failure-finality" ? "worker-accepted-finality-failed" : "worker-cleanup-failed";
            Assert.AreEqual(1, logs.Entries.Count(entry => entry.Message == category), $"worker-{secondary}-fixed-log-once");
            Assert.IsTrue(logs.Entries.Where(entry => entry.Message == category).All(entry => entry.Exception is null), $"worker-{secondary}-safe-log-no-exception");
    }

    [TestMethod]
    [DataRow("settle")]
    [DataRow("dispose")]
    [DataRow("scope")]
    public async Task CleanupOnlyAcceptedFaults_PropagateFixedFailureLogOnceAndContinueCleanup(string fault)
    {
            var calls = new List<string>();
            var logs = new CapturingLoggerProvider();
            var lifetime = new ControllableApplicationLifetime();
            var lease = new FaultingLease(CreateRequest(), fault == "settle", fault == "dispose");
            var finality = new FaultingAcceptedFinality(false, null);
            var services = new ServiceCollection();
            services.AddSingleton<IWorkerStartupInitializer>(new CompletedInitializer(calls));
            services.AddSingleton<IWorkerTransportAvailability>(new ConfiguredTransport());
            services.AddSingleton<IWorkerReadinessPublisher>(new RecordingReadiness(calls, Task.CompletedTask));
            services.AddSingleton<IInitialProcessingRunAcquirer>(new RecordingAcquirer(calls, Task.CompletedTask, InitialProcessingRunAcquisition.Accept(lease)));
            services.AddSingleton<IWorkerPreRequestFinality>(new RecordingPreRequestFinality(calls));
            services.AddSingleton<IWorkerAcceptedRunFinality>(finality);
            services.AddSingleton<ImmichReverseGeo.Web.Services.IProcessingRunExecutor>(new RecordingExecutor((request, _, _) => Task.FromResult(CreateResult(request, ImmichReverseGeo.Core.Models.ProcessingRunOutcome.Completed))));
            services.AddSingleton<ImmichReverseGeo.Core.Processing.IProcessingEventReporter>(new TestReporter());
            await using var provider = services.BuildServiceProvider();
            var scopeFactory = new CountingScopeFactory(provider, calls) { DisposeFailure = fault == "scope" ? new InvalidOperationException("scope-only") : null };
            var service = new InternalWorkerLifecycleService(scopeFactory, lifetime, new CapturingLogger<InternalWorkerLifecycleService>(logs.Entries));
            await service.StartAsync(CancellationToken.None);
            lifetime.SignalStarted();
            var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5)), $"worker-cleanup-only-{fault}");
            Assert.AreEqual("worker-cleanup-failed", thrown.Message, $"worker-cleanup-only-{fault}-category");
            Assert.AreEqual(1, finality.CompleteCount, $"worker-cleanup-only-{fault}-no-terminal-retry");
            Assert.AreEqual(1, lease.SettleCount, $"worker-cleanup-only-{fault}-settle");
            Assert.AreEqual(1, lease.DisposeCount, $"worker-cleanup-only-{fault}-lease-dispose");
            Assert.AreEqual(1, scopeFactory.DisposeCount, $"worker-cleanup-only-{fault}-scope-dispose");
            Assert.AreEqual(1, lifetime.StopCount, $"worker-cleanup-only-{fault}-stop");
            Assert.AreEqual(1, logs.Entries.Count(entry => entry.Message == "worker-cleanup-failed"), $"worker-cleanup-only-{fault}-log");
    }

    [TestMethod]
    public async Task ThrowingCleanupLogger_DoesNotReplaceExecutorPrimaryOrSkipLaterCleanup()
    {
        var calls = new List<string>();
        var lifetime = new ControllableApplicationLifetime();
        var primary = new InvalidOperationException("executor-primary-throwing-logger");
        var services = new ServiceCollection();
        services.AddSingleton<IWorkerStartupInitializer>(new CompletedInitializer(calls));
        services.AddSingleton<IWorkerTransportAvailability>(new ConfiguredTransport());
        services.AddSingleton<IWorkerReadinessPublisher>(new RecordingReadiness(calls, Task.CompletedTask));
        var lease = new FaultingLease(CreateRequest(), settleFault: true, disposeFault: false);
        services.AddSingleton<IInitialProcessingRunAcquirer>(new RecordingAcquirer(calls, Task.CompletedTask, InitialProcessingRunAcquisition.Accept(lease)));
        services.AddSingleton<IWorkerPreRequestFinality>(new RecordingPreRequestFinality(calls));
        services.AddSingleton<IWorkerAcceptedRunFinality>(new FaultingAcceptedFinality(false, null));
        services.AddSingleton<ImmichReverseGeo.Web.Services.IProcessingRunExecutor>(new RecordingExecutor((_, _, _) => throw primary));
        services.AddSingleton<ImmichReverseGeo.Core.Processing.IProcessingEventReporter>(new TestReporter());
        await using var provider = services.BuildServiceProvider();
        var scopeFactory = new CountingScopeFactory(provider, calls);
        var service = new InternalWorkerLifecycleService(scopeFactory, lifetime, new ThrowingLogger<InternalWorkerLifecycleService>());
        await service.StartAsync(CancellationToken.None);
        lifetime.SignalStarted();

        var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5)), "worker-throwing-logger-primary");

        Assert.AreSame(primary, thrown, "worker-throwing-logger-primary-reference");
        Assert.AreEqual(1, lease.SettleCount, "worker-throwing-logger-settle-once");
        Assert.AreEqual(1, lease.DisposeCount, "worker-throwing-logger-dispose-once");
        Assert.AreEqual(1, scopeFactory.DisposeCount, "worker-throwing-logger-scope-once");
        Assert.AreEqual(1, lifetime.StopCount, "worker-throwing-logger-stop-once");
    }

    [TestMethod]
    public async Task Runner_StartPrimaryAndAsyncDisposeFault_PreservesPrimaryAndLogsFixedCategoryOnce()
    {
        var logs = new CapturingLoggerProvider();
        using var services = new ServiceCollection()
            .AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(_ => Microsoft.Extensions.Logging.LoggerFactory.Create(builder => builder.Services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(logs)))
            .BuildServiceProvider();
        var primary = new InvalidOperationException("runner-start-primary");
        var host = new FaultingHost(services, primary, new InvalidOperationException("runner-dispose-secondary"));

        var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => WorkerHostFactory.RunHostAsync(host),
            "worker-runner-primary-preserved");

        Assert.AreSame(primary, thrown, "worker-runner-primary-reference");
        Assert.AreEqual(1, host.DisposeAsyncCount, "worker-runner-dispose-attempted-once");
        Assert.AreEqual(1, logs.Entries.Count(entry => entry.Message == "worker-host-dispose-failed"), "worker-runner-dispose-log-once");
        Assert.IsTrue(logs.Entries.Where(entry => entry.Message == "worker-host-dispose-failed").All(entry => entry.Exception is null), "worker-runner-safe-log-no-exception");
    }

    [TestMethod]
    public async Task Runner_DisposeOnlyFault_PropagatesFixedCleanupFailureAndLogsOnce()
    {
        var logs = new CapturingLoggerProvider();
        var lifetime = new ControllableApplicationLifetime();
        lifetime.StopApplication();
        using var services = new ServiceCollection()
            .AddSingleton<IHostApplicationLifetime>(lifetime)
            .AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(_ => Microsoft.Extensions.Logging.LoggerFactory.Create(builder => builder.Services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(logs)))
            .BuildServiceProvider();
        var host = new FaultingHost(services, null, new InvalidOperationException("dispose-only"));

        var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => WorkerHostFactory.RunHostAsync(host), "worker-runner-dispose-only");

        Assert.AreEqual("worker-cleanup-failed", thrown.Message, "worker-runner-dispose-only-category");
        Assert.AreEqual(1, host.DisposeAsyncCount, "worker-runner-dispose-only-once");
        Assert.AreEqual(1, logs.Entries.Count(entry => entry.Message == "worker-host-dispose-failed"), "worker-runner-dispose-only-log-once");
    }

    [TestMethod]
    public async Task Runner_ThrowingLoggerDoesNotReplaceStartPrimaryOrSkipDispose()
    {
        var primary = new InvalidOperationException("runner-primary-throwing-logger");
        var host = new FaultingHost(new LoggerFactoryServiceProvider(new ThrowingLoggerFactory()), primary, new InvalidOperationException("dispose-secondary"));
        var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => WorkerHostFactory.RunHostAsync(host), "worker-runner-throwing-logger-primary");
        Assert.AreSame(primary, thrown, "worker-runner-throwing-logger-reference");
        Assert.AreEqual(1, host.DisposeAsyncCount, "worker-runner-throwing-logger-dispose-once");
    }

    [TestMethod]
    public async Task Runner_LoggerResolutionPrimaryAndDisposeFault_PreservesResolutionFailure()
    {
        var primary = new InvalidOperationException("logger-resolution-primary");
        var host = new FaultingHost(new ThrowingServiceProvider(primary), new InvalidOperationException("start-should-not-run"), new InvalidOperationException("dispose-secondary"));

        var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => WorkerHostFactory.RunHostAsync(host),
            "worker-runner-logger-resolution-primary");

        Assert.AreSame(primary, thrown, "worker-runner-logger-resolution-reference");
        Assert.AreEqual(1, host.DisposeAsyncCount, "worker-runner-logger-resolution-dispose-once");
    }

    private sealed class ThrowingServiceProvider(Exception exception) : IServiceProvider
    {
        public object? GetService(Type serviceType) => throw exception;
    }

    private sealed class LoggerFactoryServiceProvider(Microsoft.Extensions.Logging.ILoggerFactory factory) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(Microsoft.Extensions.Logging.ILoggerFactory) ? factory : null;
    }

    private sealed class ThrowingLoggerFactory : Microsoft.Extensions.Logging.ILoggerFactory
    {
        public void AddProvider(Microsoft.Extensions.Logging.ILoggerProvider provider) { }
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new ThrowingLogger();
        public void Dispose() { }
        private sealed class ThrowingLogger : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => throw new InvalidOperationException("logger-fault");
        }
    }

    private sealed class FaultingHost(IServiceProvider services, Exception? startFailure, Exception disposeFailure) : IHost, IAsyncDisposable
    {
        public IServiceProvider Services { get; } = services;
        public int DisposeAsyncCount { get; private set; }
        public Task StartAsync(CancellationToken cancellationToken = default) => startFailure is null ? Task.CompletedTask : Task.FromException(startFailure);
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Dispose() { }
        public ValueTask DisposeAsync()
        {
            DisposeAsyncCount++;
            return ValueTask.FromException(disposeFailure);
        }
    }

    private sealed class FaultingLease(ImmichReverseGeo.Core.Models.ProcessingRunRequest request, bool settleFault, bool disposeFault) : IProcessingRunLease
    {
        public int SettleCount { get; private set; }
        public int DisposeCount { get; private set; }
        public ImmichReverseGeo.Core.Models.ProcessingRunRequest Request { get; } = request;
        public CancellationToken CancellationToken => CancellationToken.None;
        public ValueTask SettleAsync(CancellationToken cancellationToken)
        {
            SettleCount++;
            return settleFault ? ValueTask.FromException(new InvalidOperationException("settle-secondary")) : ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return disposeFault ? ValueTask.FromException(new InvalidOperationException("dispose-secondary")) : ValueTask.CompletedTask;
        }
    }

    private sealed class FaultingAcceptedFinality(bool failureFault, Exception? completionFault) : IWorkerAcceptedRunFinality
    {
        public int CompleteCount { get; private set; }
        public int FailCount { get; private set; }
        public Task CompleteAsync(ImmichReverseGeo.Core.Models.ProcessingRunRequest request, ImmichReverseGeo.Core.Models.ProcessingRunResult result, CancellationToken cancellationToken)
        {
            CompleteCount++;
            return completionFault is null ? Task.CompletedTask : Task.FromException(completionFault);
        }
        public Task FailAsync(ImmichReverseGeo.Core.Models.ProcessingRunRequest request, WorkerSafeFailure failure, CancellationToken cancellationToken)
        {
            FailCount++;
            return failureFault ? Task.FromException(new InvalidOperationException("failure-finality-secondary")) : Task.CompletedTask;
        }
    }

    private sealed class ThrowingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => throw new InvalidOperationException("logger-secondary");
    }

    private sealed class CapturingLogger<T>(List<(string Message, Exception? Exception)> entries) : Microsoft.Extensions.Logging.ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => entries.Add((formatter(state, exception), exception));
    }

    private sealed class CapturingLoggerProvider : Microsoft.Extensions.Logging.ILoggerProvider
    {
        public List<(string Message, Exception? Exception)> Entries { get; } = [];
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new CapturingLogger(Entries);
        public void Dispose() { }
        private sealed class CapturingLogger(List<(string Message, Exception? Exception)> entries) : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                entries.Add((formatter(state, exception), exception));
            }
        }
    }
}
