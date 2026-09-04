using ImmichReverseGeo.Web.Composition;
using ImmichReverseGeo.Web.WorkerHost;
using ImmichReverseGeo.Web.WorkerHost.WorkerNdjsonOutput;
using ImmichReverseGeo.Web.WorkerHost.WorkerStdinRequestLoop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
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
            using var host = BuildWorkerHost(CreateContext(fixtureRoot));
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
            var stdinSource = host.Services.GetRequiredService<WorkerStdinRequestSource>();
            Assert.AreSame(stdinSource, host.Services.GetRequiredService<IInitialProcessingRunAcquirer>(), "worker-stdin-source-alias");
            var stdinFinality = host.Services.GetRequiredService<WorkerStdinAcceptedRunFinality>();
            Assert.AreSame(stdinFinality, host.Services.GetRequiredService<IWorkerAcceptedRunFinality>(), "worker-stdin-finality-alias");

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
        var registrationFactory = new CountingOutputStreamFactory();
        var registrationServices = new ServiceCollection();
        registrationServices.AddInternalWorkerHostServices(
            registrationFactory,
            new ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator());
        Assert.AreEqual(0, registrationFactory.OpenCount, "worker-ndjson-registration-lazy");

        var fixtureRoot = CreateFixtureRoot();
        try
        {
            var descriptors = CreateWorkerBuilder(CreateContext(fixtureRoot)).Services;
            AssertDescriptor(descriptors, typeof(ImmichReverseGeo.Web.Services.ProcessingInfrastructureLookup), ServiceLifetime.Singleton);
            AssertDescriptor(descriptors, typeof(ImmichReverseGeo.Web.Services.IProcessingInfrastructureLookup), ServiceLifetime.Singleton);
            AssertDescriptor(descriptors, typeof(ImmichReverseGeo.Web.Services.ProcessingRunExecutor), ServiceLifetime.Singleton);
            AssertDescriptor(descriptors, typeof(ImmichReverseGeo.Web.Services.IProcessingRunExecutor), ServiceLifetime.Singleton);
            AssertDescriptor(descriptors, typeof(SkippedAssetsWorkerStartupInitializer), ServiceLifetime.Singleton);
            AssertDescriptor(descriptors, typeof(IWorkerStartupInitializer), ServiceLifetime.Singleton);
            AssertDescriptor(descriptors, typeof(WorkerStdinTransportConfigured), ServiceLifetime.Singleton);
            AssertDescriptor(descriptors, typeof(IWorkerTransportAvailability), ServiceLifetime.Singleton);
            AssertDescriptor(descriptors, typeof(IWorkerStandardInputStreamFactory), ServiceLifetime.Singleton);
            AssertDescriptor(descriptors, typeof(WorkerStdinRequestSource), ServiceLifetime.Singleton);
            AssertDescriptor(descriptors, typeof(IInitialProcessingRunAcquirer), ServiceLifetime.Singleton);
            AssertDescriptor(descriptors, typeof(WorkerStdinAcceptedRunFinality), ServiceLifetime.Singleton);
            AssertDescriptor(descriptors, typeof(IWorkerAcceptedRunFinality), ServiceLifetime.Singleton);
            AssertDescriptor(descriptors, typeof(TransitionalWorkerPreRequestFinality), ServiceLifetime.Singleton);
            AssertDescriptor(descriptors, typeof(IWorkerPreRequestFinality), ServiceLifetime.Singleton);
            AssertDescriptor(descriptors, typeof(WorkerNdjsonEmitter), ServiceLifetime.Singleton);
            AssertDescriptor(descriptors, typeof(IWorkerReadinessPublisher), ServiceLifetime.Singleton);
            AssertDescriptor(descriptors, typeof(WorkerNdjsonProcessingEventReporter), ServiceLifetime.Singleton);
            AssertDescriptor(descriptors, typeof(ImmichReverseGeo.Core.Processing.IProcessingEventReporter), ServiceLifetime.Singleton);
            AssertDescriptor(descriptors, typeof(IHostedService), ServiceLifetime.Singleton);
        }
        finally { DeleteFixtureRoot(fixtureRoot); }
    }

    [TestMethod]
    [TestCategory("Change23")]
    public void HostRegistration_UsesTheExactPreconstructedOutcomeAccumulator()
    {
        var outcomes = new ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddInternalWorkerHostServices(new CountingOutputStreamFactory(), outcomes);

        using var host = builder.Build();

        Assert.AreSame(outcomes, host.Services.GetRequiredService<ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator>(), "worker-prehost-accumulator-reference");
        Assert.AreSame(
            host.Services.GetRequiredService<InternalWorkerLifecycleService>(),
            host.Services.GetServices<IHostedService>().Single(),
            "worker-lifecycle-owner-alias-reference");
    }

    [TestMethod]
    [TestCategory("Change23")]
    public async Task Runner_MismatchedAccumulatorSentinelReturnsInfrastructureWithoutStartingHost()
    {
        var registered = new ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator();
        var supplied = new ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator();
        using var services = new ServiceCollection()
            .AddSingleton(registered)
            .BuildServiceProvider();
        var host = new FaultingHost(services, new AssertFailedException("MISMATCH_MUST_NOT_START"), new InvalidOperationException("cleanup"));

        var exitCode = await WorkerHostFactory.RunHostAsync(host, supplied);

        Assert.AreEqual(5, exitCode, "worker-mismatched-accumulator-code");
        Assert.AreEqual(5, supplied.Fact.ExitCode, "worker-mismatched-accumulator-fact");
        Assert.AreEqual(0, host.StartAsyncCount, "worker-mismatched-accumulator-does-not-start");
        Assert.AreEqual(1, host.DisposeAsyncCount, "worker-mismatched-accumulator-disposes-once");
    }

    [TestMethod]
    public async Task StdinRegistrationBuildAvailabilityAndAliasResolutionAreSideEffectFree()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInternalWorkerHostServices(
            new CountingOutputStreamFactory(),
            new ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator());
        var inputFactory = new CountingNeverOpenInputFactory();
        services.RemoveAll<IWorkerStandardInputStreamFactory>();
        services.AddSingleton<IWorkerStandardInputStreamFactory>(inputFactory);

        await using var provider = services.BuildServiceProvider();
        Assert.AreEqual(0, inputFactory.OpenCount, "worker-stdin-registration-zero-open");
        Assert.AreEqual(0, inputFactory.ReadCount, "worker-stdin-registration-zero-read");
        var availability = provider.GetRequiredService<IWorkerTransportAvailability>();
        Assert.IsTrue(availability.IsConfigured, "worker-stdin-production-availability-true");
        Assert.AreEqual(0, inputFactory.OpenCount, "worker-stdin-availability-zero-open");
        var source = provider.GetRequiredService<WorkerStdinRequestSource>();
        Assert.AreSame(source, provider.GetRequiredService<IInitialProcessingRunAcquirer>(), "worker-stdin-provider-source-alias");
        Assert.AreSame(
            provider.GetRequiredService<WorkerStdinAcceptedRunFinality>(),
            provider.GetRequiredService<IWorkerAcceptedRunFinality>(),
            "worker-stdin-provider-finality-alias");
        Assert.AreEqual(0, inputFactory.OpenCount, "worker-stdin-resolution-zero-open");
        Assert.AreEqual(0, inputFactory.ReadCount, "worker-stdin-resolution-zero-read");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FalseOrFaultingAvailabilityNeverOpensOrReadsStdin(bool faultAvailability)
    {
        var fixtureRoot = CreateFixtureRoot();
        var inputFactory = new CountingNeverOpenInputFactory();
        try
        {
            var builder = CreateWorkerBuilder(CreateContext(fixtureRoot));
            ReplaceSingleton<IWorkerStartupInitializer>(builder.Services, new CompletedInitializer([]));
            ReplaceSingleton<IWorkerPreRequestFinality>(builder.Services, new RecordingPreRequestFinality([]));
            builder.Services.RemoveAll<IWorkerStandardInputStreamFactory>();
            builder.Services.AddSingleton<IWorkerStandardInputStreamFactory>(inputFactory);
            builder.Services.RemoveAll<IWorkerTransportAvailability>();
            if (faultAvailability)
            {
                builder.Services.AddSingleton<IWorkerTransportAvailability>(_ => throw new InvalidOperationException("AVAILABILITY_RAW_SENTINEL"));
            }
            else
            {
                builder.Services.AddSingleton<IWorkerTransportAvailability>(new UnconfiguredTransport());
            }

            using var host = builder.Build();
            Assert.AreEqual(0, inputFactory.OpenCount, "worker-stdin-availability-build-zero-open-" + faultAvailability);
            await host.StartAsync();
            await host.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(0, inputFactory.OpenCount, "worker-stdin-availability-run-zero-open-" + faultAvailability);
            Assert.AreEqual(0, inputFactory.ReadCount, "worker-stdin-availability-run-zero-read-" + faultAvailability);
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    [TestMethod]
    public async Task WorkerNdjsonRegistration_IsLazyAndAliasesTheExactSingletonEmitterAndReporter()
    {
        var fixtureRoot = CreateFixtureRoot();
        try
        {
            var builder = CreateWorkerBuilder(CreateContext(fixtureRoot));
            ReplaceSingleton<IWorkerTransportAvailability>(builder.Services, new UnconfiguredTransport());
            AssertDescriptor(builder.Services, typeof(IWorkerNdjsonOutputStreamFactory), ServiceLifetime.Singleton);
            AssertDescriptor(builder.Services, typeof(WorkerNdjsonEmitter), ServiceLifetime.Singleton);
            AssertDescriptor(builder.Services, typeof(IWorkerReadinessPublisher), ServiceLifetime.Singleton);
            AssertDescriptor(builder.Services, typeof(WorkerNdjsonProcessingEventReporter), ServiceLifetime.Singleton);
            AssertDescriptor(builder.Services, typeof(ImmichReverseGeo.Core.Processing.IProcessingEventReporter), ServiceLifetime.Singleton);
            var stdoutFactory = new CountingOutputStreamFactory();
            builder.Services.RemoveAll<IWorkerNdjsonOutputStreamFactory>();
            builder.Services.AddSingleton<IWorkerNdjsonOutputStreamFactory>(stdoutFactory);
            var host = builder.Build();

            Assert.AreEqual(0, stdoutFactory.OpenCount, "worker-ndjson-provider-build-lazy");
            await host.StartAsync();
            await host.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(0, stdoutFactory.OpenCount, "worker-ndjson-unavailable-startup-lazy");

            var reporter = host.Services.GetRequiredService<ImmichReverseGeo.Core.Processing.IProcessingEventReporter>();
            Assert.AreEqual(1, stdoutFactory.OpenCount, "worker-ndjson-reporter-resolution-opens-owner-once");
            Assert.AreSame(reporter, host.Services.GetRequiredService<WorkerNdjsonProcessingEventReporter>(), "worker-ndjson-reporter-alias-identity");

            var emitter = host.Services.GetRequiredService<WorkerNdjsonEmitter>();
            Assert.AreEqual(1, stdoutFactory.OpenCount, "worker-ndjson-concrete-resolution-opens-once");
            Assert.AreSame(emitter, host.Services.GetRequiredService<IWorkerReadinessPublisher>(), "worker-ndjson-readiness-alias-identity");
            Assert.AreEqual(1, stdoutFactory.OpenCount, "worker-ndjson-alias-resolution-does-not-reopen");
            var disposalFailure = Assert.ThrowsExactly<WorkerNdjsonTransportException>(host.Dispose, "worker-ndjson-unfinished-owner-disposal-failure");
            Assert.AreEqual(WorkerNdjsonFailureStage.Disposal, disposalFailure.Stage, "worker-ndjson-unfinished-owner-disposal-stage");
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    [TestMethod]
    public async Task WorkerNdjsonRegistration_StdoutOpenFailureCachesOneSafeBrokenOwner()
    {
        var fixtureRoot = CreateFixtureRoot();
        try
        {
            var builder = CreateWorkerBuilder(CreateContext(fixtureRoot));
            ReplaceSingleton<IWorkerTransportAvailability>(builder.Services, new UnconfiguredTransport());
            var stdoutFactory = new ThrowingCountingOutputStreamFactory();
            builder.Services.RemoveAll<IWorkerNdjsonOutputStreamFactory>();
            builder.Services.AddSingleton<IWorkerNdjsonOutputStreamFactory>(stdoutFactory);
            var host = builder.Build();

            Assert.AreEqual(0, stdoutFactory.OpenCount, "worker-ndjson-open-failure-provider-build-lazy");
            await host.StartAsync();
            await host.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(0, stdoutFactory.OpenCount, "worker-ndjson-open-failure-unavailable-lazy");

            var emitter = host.Services.GetRequiredService<WorkerNdjsonEmitter>();
            Assert.AreEqual(1, stdoutFactory.OpenCount, "worker-ndjson-open-failure-first-open");
            Assert.AreSame(emitter, host.Services.GetRequiredService<IWorkerReadinessPublisher>(), "worker-ndjson-open-failure-alias-identity");
            Assert.AreEqual(1, stdoutFactory.OpenCount, "worker-ndjson-open-failure-alias-no-retry");

            var first = emitter.PublishAsync(CancellationToken.None);
            var second = host.Services.GetRequiredService<IWorkerReadinessPublisher>().PublishAsync(CancellationToken.None);
            var firstFailure = await Assert.ThrowsExactlyAsync<WorkerNdjsonTransportException>(async () => await first, "worker-ndjson-open-failure-first-safe");
            var secondFailure = await Assert.ThrowsExactlyAsync<WorkerNdjsonTransportException>(async () => await second, "worker-ndjson-open-failure-concurrent-safe");
            var futureFailure = await Assert.ThrowsExactlyAsync<WorkerNdjsonTransportException>(async () => await emitter.PublishAsync(CancellationToken.None), "worker-ndjson-open-failure-future-safe");

            Assert.AreSame(firstFailure, secondFailure, "worker-ndjson-open-failure-concurrent-identity");
            Assert.AreSame(firstFailure, futureFailure, "worker-ndjson-open-failure-future-identity");
            Assert.AreEqual("worker-ndjson-output-failed", firstFailure.Message, "worker-ndjson-open-failure-safe-message");
            Assert.AreEqual(WorkerNdjsonFailureStage.OpenStandardOutput, firstFailure.Stage, "worker-ndjson-open-failure-stage");
            Assert.IsFalse(firstFailure.Message.Contains("OPEN_SECRET_SENTINEL", StringComparison.Ordinal), "worker-ndjson-open-failure-no-raw-message");
            Assert.AreEqual(1, stdoutFactory.OpenCount, "worker-ndjson-open-failure-no-later-open");
            var disposalFailure = Assert.ThrowsExactly<WorkerNdjsonTransportException>(host.Dispose, "worker-ndjson-open-failure-owner-disposal");
            Assert.AreSame(firstFailure, disposalFailure, "worker-ndjson-open-failure-owner-identity");
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    [TestMethod]
    public void BuildAndDescriptorFactory_ProduceTheSameAuthoritativeWorkerGraph()
    {
        var fixtureRoot = CreateFixtureRoot();
        try
        {
            var context = CreateContext(fixtureRoot);
            var descriptors = CreateWorkerBuilder(context).Services;
            using var host = BuildWorkerHost(context);
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
            using var host = BuildWorkerHost(CreateContext(fixtureRoot));
            var consoleOptions = host.Services
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.Extensions.Logging.Console.ConsoleLoggerOptions>>()
                .Value;

            Assert.AreEqual(Microsoft.Extensions.Logging.LogLevel.Trace, consoleOptions.LogToStandardErrorThreshold, "worker-console-stderr-threshold");
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
    [TestCategory("Change23")]
    public void InputFailureMatrix_PreservesEveryExactSafeFailureReference()
    {
        foreach (var code in Enum.GetValues<ImmichReverseGeo.Core.WorkerProtocol.WorkerProtocolFailureCode>())
        {
            var failure = WorkerSafeFailure.Input(code);
            var acquisition = InitialProcessingRunAcquisition.Fail(failure);
            var inputFinality = WorkerInputPumpFinality.InputFailure(failure);

            Assert.AreSame(failure, acquisition.Failure, $"worker-input-acquisition-reference-{code}");
            Assert.AreSame(
                failure,
                ((WorkerInputPumpFinality.InputFailureFinality)inputFinality).Failure,
                $"worker-input-finality-reference-{code}");
            Assert.AreEqual(WorkerSafeFailureKind.InputProtocol, failure.Kind, $"worker-input-kind-{code}");
        }
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
    public async Task ActivatedProductionHost_InitialisesThenCoordinatesCleanInputEof()
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
                    ReplaceSingleton<IWorkerStandardInputStreamFactory>(services, new EmptyInputFactory());
                });

            await host.StartAsync();
            await host.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));

            CollectionAssert.AreEqual(
                new[] { "initialise", "worker-input-closed" },
                calls,
                "worker-activated-eof-order");
            Assert.IsNotNull(host.Services.GetService<IInitialProcessingRunAcquirer>(), "worker-acquirer-registered");
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
            var builder = CreateWorkerBuilder(CreateContext(fixtureRoot));
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
            var builder = CreateWorkerBuilder(CreateContext(fixtureRoot));
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
    public async Task ReadinessFailure_WithProductionStdinRegistrationPerformsZeroOpenAndRead()
    {
        var fixtureRoot = CreateFixtureRoot();
        var calls = new List<string>();
        var inputFactory = new CountingNeverOpenInputFactory();

        try
        {
            var builder = CreateWorkerBuilder(CreateContext(fixtureRoot));
            ReplaceSingleton<IWorkerStartupInitializer>(builder.Services, new CompletedInitializer(calls));
            ReplaceSingleton<IWorkerTransportAvailability>(builder.Services, new ConfiguredTransport());
            ReplaceSingleton<IWorkerReadinessPublisher>(builder.Services, new ThrowingReadiness(calls));
            ReplaceSingleton<IWorkerPreRequestFinality>(builder.Services, new RecordingPreRequestFinality(calls));
            builder.Services.RemoveAll<IWorkerStandardInputStreamFactory>();
            builder.Services.AddSingleton<IWorkerStandardInputStreamFactory>(inputFactory);
            using var host = builder.Build();

            Assert.AreEqual(0, inputFactory.OpenCount, "worker-ready-failure-provider-side-effect-free");
            await host.StartAsync();
            await host.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(0, inputFactory.OpenCount, "worker-ready-failure-zero-stdin-open");
            Assert.AreEqual(0, inputFactory.ReadCount, "worker-ready-failure-zero-stdin-read");
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
            var builder = CreateWorkerBuilder(CreateContext(fixtureRoot));
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
    public async Task ConfiguredProductionHost_FlushesReadyBeforeAcceptanceAndUsesRegisteredReporterOnce()
    {
        var fixtureRoot = CreateFixtureRoot();
        var request = CreateRequest();
        var lease = new AcceptedLease(request);
        var outputFactory = new CapturingOutputStreamFactory();
        var clock = new FixedTimeProvider(DateTimeOffset.UnixEpoch);
        var acquirer = new FlushAwareAcceptedAcquirer(outputFactory.Output, lease);
        var finality = new RecordingAcceptedFinality();
        var executor = new RecordingExecutor(async (receivedRequest, receivedReporter, cancellationToken) =>
        {
            Assert.AreSame(request, receivedRequest, "worker-production-executor-request-reference");
            Assert.AreEqual(1, outputFactory.Output.FlushCount, "worker-production-ready-flushed-before-execution");
            var startedAtUtc = clock.GetUtcNow();
            var session = await receivedReporter.OpenRunAsync(receivedRequest, startedAtUtc, cancellationToken);
            var result = new ImmichReverseGeo.Core.Models.ProcessingRunResult(
                receivedRequest,
                startedAtUtc,
                startedAtUtc,
                0,
                0,
                0,
                0,
                ImmichReverseGeo.Core.Models.ProcessingRunOutcome.Cancelled,
                null);
            await session.FinishAsync(result);
            return result;
        });

        try
        {
            var builder = CreateWorkerBuilder(CreateContext(fixtureRoot));
            builder.Services.RemoveAll<TimeProvider>();
            builder.Services.AddSingleton<TimeProvider>(clock);
            builder.Services.RemoveAll<IWorkerNdjsonOutputStreamFactory>();
            builder.Services.AddSingleton<IWorkerNdjsonOutputStreamFactory>(outputFactory);
            ReplaceSingleton<IWorkerStartupInitializer>(builder.Services, new CompletedInitializer([]));
            ReplaceSingleton<IWorkerTransportAvailability>(builder.Services, new ConfiguredTransport());
            ReplaceSingleton<IInitialProcessingRunAcquirer>(builder.Services, acquirer);
            ReplaceSingleton<IWorkerPreRequestFinality>(builder.Services, new RecordingPreRequestFinality([]));
            ReplaceSingleton<IWorkerAcceptedRunFinality>(builder.Services, finality);
            ReplaceSingleton<ImmichReverseGeo.Web.Services.IProcessingRunExecutor>(builder.Services, executor);
            using var host = builder.Build();

            await host.StartAsync();
            await host.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));

            var registeredReporter = host.Services.GetRequiredService<ImmichReverseGeo.Core.Processing.IProcessingEventReporter>();
            Assert.AreSame(registeredReporter, host.Services.GetRequiredService<WorkerNdjsonProcessingEventReporter>(), "worker-production-registered-reporter-alias-identity");
            Assert.AreEqual(1, acquirer.CallCount, "worker-production-one-acquisition");
            Assert.AreEqual(1, executor.CallCount, "worker-production-one-execution");
            Assert.AreSame(request, executor.Request, "worker-production-executor-exact-request");
            Assert.AreSame(registeredReporter, executor.Reporter, "worker-production-executor-registered-reporter");
            Assert.AreEqual(1, finality.Completed.Count, "worker-production-one-completion-finality");
            Assert.AreEqual(0, finality.Failures.Count, "worker-production-no-synthetic-failure-finality");
            Assert.AreEqual(1, lease.NotifyExecutionStartingCount, "worker-production-one-execution-starting");
            Assert.AreEqual(1, lease.SettleCount, "worker-production-one-lease-settle");
            Assert.AreEqual(1, lease.DisposeCount, "worker-production-one-lease-dispose");

            var frames = System.Text.Encoding.UTF8.GetString(outputFactory.Output.ToArray())
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.AreEqual(3, frames.Length, "worker-production-ready-run-terminal-frame-count");
            Assert.IsTrue(frames[0].Contains("\"type\":\"ready\"", StringComparison.Ordinal), "worker-production-ready-first");
            Assert.IsTrue(frames[1].Contains("\"type\":\"run-started\"", StringComparison.Ordinal), "worker-production-run-started-second");
            Assert.IsTrue(frames[2].Contains("\"category\":\"terminal\"", StringComparison.Ordinal), "worker-production-terminal-last");
            Assert.AreEqual(1, frames.Count(frame => frame.Contains("\"category\":\"terminal\"", StringComparison.Ordinal)), "worker-production-no-duplicate-synthetic-terminal");
            Assert.AreEqual(3, outputFactory.Output.FlushCount, "worker-production-one-flush-per-frame");
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
            Assert.AreEqual(1, lease.NotifyExecutionStartingCount, "worker-accepted-execution-starting-count");
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
    [TestCategory("Change23")]
    [DataRow("startup", 5, "startup")]
    [DataRow("readiness", 5, "transport")]
    [DataRow("ready-output", 6, "output")]
    [DataRow("clean-eof", 2, "input")]
    [DataRow("partial-request", 2, "input")]
    [DataRow("invalid-request", 2, "input")]
    [DataRow("stdin-io", 5, "input")]
    [DataRow("acquisition", 5, "input")]
    public async Task PreRequestOutcomes_ReturnExactCodeAndSafePhaseWithoutRunTerminal(
        string scenario,
        int expectedExitCode,
        string expectedPhase)
    {
        var fixtureRoot = CreateFixtureRoot();
        using var errorWriter = new StringWriter();
        var finality = new RecordingPreRequestFinality([]);

        try
        {
            var builder = CreateWorkerBuilder(CreateContext(fixtureRoot));
            ReplaceSingleton<IWorkerStartupInitializer>(
                builder.Services,
                scenario == "startup"
                    ? new ThrowingInitializer([])
                    : new CompletedInitializer([]));
            ReplaceSingleton<IWorkerTransportAvailability>(builder.Services, new ConfiguredTransport());
            var readiness = new RecordingReadiness(
                [],
                scenario == "readiness"
                    ? Task.FromException(new InvalidOperationException("generic-readiness-failure"))
                    : Task.CompletedTask);
            if (scenario == "ready-output")
            {
                builder.Services.RemoveAll<IWorkerNdjsonOutputStreamFactory>();
                builder.Services.AddSingleton<IWorkerNdjsonOutputStreamFactory>(new FaultingOutputStreamFactory("flush"));
            }
            else
            {
                ReplaceSingleton<IWorkerReadinessPublisher>(builder.Services, readiness);
            }

            IInitialProcessingRunAcquirer acquirer = scenario switch
            {
                "clean-eof" => new StaticAcquirer(InitialProcessingRunAcquisition.EndOfInput()),
                "partial-request" => new StaticAcquirer(InitialProcessingRunAcquisition.Fail(WorkerSafeFailure.Input(ImmichReverseGeo.Core.WorkerProtocol.WorkerProtocolFailureCode.InvalidFraming))),
                "invalid-request" => new StaticAcquirer(InitialProcessingRunAcquisition.Fail(WorkerSafeFailure.Input(ImmichReverseGeo.Core.WorkerProtocol.WorkerProtocolFailureCode.MalformedJson))),
                "stdin-io" => new StaticAcquirer(InitialProcessingRunAcquisition.Fail(WorkerSafeFailure.Reader())),
                "acquisition" => new ThrowingAcquirer([], readiness.Completed),
                _ => new StaticAcquirer(InitialProcessingRunAcquisition.EndOfInput())
            };
            ReplaceSingleton<IInitialProcessingRunAcquirer>(builder.Services, acquirer);
            ReplaceSingleton<IWorkerPreRequestFinality>(builder.Services, finality);

            var exitCode = await RunToBoundaryAsync(builder.Build(), errorWriter);

            Assert.AreEqual(expectedExitCode, exitCode, $"worker-pre-request-code-{scenario}");
            Assert.AreEqual(1, finality.Outcomes.Count, $"worker-pre-request-finality-{scenario}");
            Assert.AreEqual(0, finality.Outcomes.Count(outcome => outcome.Category.Contains("terminal", StringComparison.Ordinal)), $"worker-pre-request-no-terminal-{scenario}");
            Assert.AreEqual(1, errorWriter.ToString().Split(ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitDiagnostic.FinalSummaryMarker, StringSplitOptions.None).Length - 1, $"worker-pre-request-summary-once-{scenario}");
            Assert.IsTrue(errorWriter.ToString().Contains($"phase={expectedPhase}", StringComparison.Ordinal), $"worker-pre-request-summary-phase-{scenario}");
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    [TestMethod]
    [TestCategory("Change23")]
    [DataRow(ImmichReverseGeo.Core.Models.ProcessingRunOutcome.Completed, 0, "")]
    [DataRow(ImmichReverseGeo.Core.Models.ProcessingRunOutcome.Failed, 4, "outcome=executor-failure phase=execution")]
    [DataRow(ImmichReverseGeo.Core.Models.ProcessingRunOutcome.Cancelled, 130, "outcome=cancelled phase=shutdown")]
    public async Task AcceptedResults_ReturnAfterOneTerminalFinalityWithExactSummary(
        ImmichReverseGeo.Core.Models.ProcessingRunOutcome outcome,
        int expectedExitCode,
        string expectedSummary)
    {
        var fixtureRoot = CreateFixtureRoot();
        using var errorWriter = new StringWriter();
        var lease = new AcceptedLease(CreateRequest());
        var finality = new RecordingAcceptedFinality();
        var executor = new RecordingExecutor((request, _, _) => Task.FromResult(CreateResult(request, outcome)));

        try
        {
            var exitCode = await RunToBoundaryAsync(
                BuildAcceptedHost(fixtureRoot, lease, executor, finality),
                errorWriter);

            Assert.AreEqual(expectedExitCode, exitCode, $"worker-accepted-code-{outcome}");
            Assert.AreEqual(1, executor.CallCount, $"worker-accepted-executor-once-{outcome}");
            Assert.AreEqual(1, finality.Completed.Count, $"worker-accepted-terminal-finality-once-{outcome}");
            Assert.AreEqual(0, finality.Failures.Count, $"worker-accepted-no-synthetic-terminal-{outcome}");
            Assert.AreSame(lease.Request, executor.Request, $"worker-accepted-executor-request-reference-{outcome}");
            Assert.AreSame(lease.Request, finality.Completed[0].Request, $"worker-accepted-finality-request-reference-{outcome}");
            Assert.AreSame(executor.Result, finality.Completed[0].Result, $"worker-accepted-finality-result-reference-{outcome}");
            if (expectedExitCode == 0)
            {
                Assert.AreEqual(string.Empty, errorWriter.ToString(), "worker-completed-no-nonzero-summary");
            }
            else
            {
                Assert.IsTrue(errorWriter.ToString().Contains(expectedSummary, StringComparison.Ordinal), $"worker-accepted-summary-{outcome}");
            }
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    [TestMethod]
    [TestCategory("Change23")]
    public async Task AdjudicatedBusyExecutor_EmitsExistingFailedTerminalWithoutDomainWork()
    {
        var fixtureRoot = CreateFixtureRoot();
        var outputFactory = new CapturingOutputStreamFactory();
        var lease = new AcceptedLease(CreateRequest());
        var finality = new RecordingAcceptedFinality();

        try
        {
            var builder = CreateAcceptedBuilder(
                fixtureRoot,
                lease,
                new RecordingExecutor((request, _, _) => Task.FromResult(CreateResult(request, ImmichReverseGeo.Core.Models.ProcessingRunOutcome.Completed))),
                finality);
            builder.Services.RemoveAll<TimeProvider>();
            builder.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(DateTimeOffset.UnixEpoch));
            builder.Services.RemoveAll<IWorkerNdjsonOutputStreamFactory>();
            builder.Services.AddSingleton<IWorkerNdjsonOutputStreamFactory>(outputFactory);
            builder.Services.RemoveAll<IWorkerReadinessPublisher>();
            builder.Services.AddSingleton<IWorkerReadinessPublisher>(sp => sp.GetRequiredService<WorkerNdjsonEmitter>());
            builder.Services.RemoveAll<ImmichReverseGeo.Core.Processing.IProcessingEventReporter>();
            builder.Services.AddSingleton<ImmichReverseGeo.Core.Processing.IProcessingEventReporter>(sp =>
                sp.GetRequiredService<WorkerNdjsonProcessingEventReporter>());
            builder.Services.RemoveAll<ImmichReverseGeo.Web.Services.IProcessingRunExecutor>();
            builder.Services.AddSingleton<ImmichReverseGeo.Web.Services.IProcessingRunExecutor>(sp => new AdjudicatedBusyExecutor(
                sp.GetRequiredService<ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator>()));
            var host = builder.Build();
            var outcomes = host.Services.GetRequiredService<ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator>();

            await host.StartAsync();
            await host.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(3, outcomes.Fact.ExitCode, "worker-busy-adjudicated-exit-code");
            Assert.AreEqual(1, lease.NotifyExecutionStartingCount, "worker-busy-execution-entry");
            Assert.AreEqual(1, finality.Completed.Count, "worker-busy-terminal-notification-once");
            var frames = System.Text.Encoding.UTF8.GetString(outputFactory.Output.ToArray())
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.AreEqual(3, frames.Length, "worker-busy-ready-started-terminal-count");
            Assert.IsTrue(frames[1].Contains("\"type\":\"run-started\"", StringComparison.Ordinal), "worker-busy-started-before-failure");
            Assert.IsTrue(frames[2].Contains("\"category\":\"terminal\"", StringComparison.Ordinal), "worker-busy-existing-terminal");
            Assert.IsTrue(frames[2].Contains("worker advisory lock is busy", StringComparison.Ordinal), "worker-busy-safe-detail");
            host.Dispose();
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    [TestMethod]
    [TestCategory("Change23")]
    public async Task AcceptedReporterTerminalFlushFailure_OverridesPostAcceptanceInputWithoutSyntheticTerminal()
    {
        var fixtureRoot = CreateFixtureRoot();
        var outputFactory = new TerminalFlushFailingOutputStreamFactory();
        var lease = new AcceptedLease(
            CreateRequest(),
            finality: WorkerInputPumpFinality.InputFailure(WorkerSafeFailure.Input(ImmichReverseGeo.Core.WorkerProtocol.WorkerProtocolFailureCode.InvalidSequence)));
        var finality = new RecordingAcceptedFinality();

        try
        {
            var builder = CreateAcceptedBuilder(
                fixtureRoot,
                lease,
                new RecordingExecutor((request, _, _) => Task.FromResult(CreateResult(request, ImmichReverseGeo.Core.Models.ProcessingRunOutcome.Completed))),
                finality);
            builder.Services.RemoveAll<TimeProvider>();
            builder.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(DateTimeOffset.UnixEpoch));
            builder.Services.RemoveAll<IWorkerNdjsonOutputStreamFactory>();
            builder.Services.AddSingleton<IWorkerNdjsonOutputStreamFactory>(outputFactory);
            builder.Services.RemoveAll<IWorkerReadinessPublisher>();
            builder.Services.AddSingleton<IWorkerReadinessPublisher>(sp => sp.GetRequiredService<WorkerNdjsonEmitter>());
            builder.Services.RemoveAll<ImmichReverseGeo.Core.Processing.IProcessingEventReporter>();
            builder.Services.AddSingleton<ImmichReverseGeo.Core.Processing.IProcessingEventReporter>(sp =>
                sp.GetRequiredService<WorkerNdjsonProcessingEventReporter>());
            builder.Services.RemoveAll<ImmichReverseGeo.Web.Services.IProcessingRunExecutor>();
            builder.Services.AddSingleton<ImmichReverseGeo.Web.Services.IProcessingRunExecutor>(new TerminalFinishingExecutor());
            var host = builder.Build();
            var outcomes = host.Services.GetRequiredService<ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator>();

            await host.StartAsync();
            await host.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(6, outcomes.Fact.ExitCode, "worker-terminal-flush-output-precedence");
            Assert.AreEqual(1, finality.Failures.Count, "worker-terminal-flush-failure-notification-once");
            Assert.AreEqual(0, finality.Completed.Count, "worker-terminal-flush-no-completion-notification");
            Assert.AreEqual(4, outputFactory.Output.FlushCount, "worker-terminal-flush-one-terminal-attempt");
            Assert.AreEqual(4, outputFactory.Output.ToArray().Count(value => value == (byte)'\n'), "worker-terminal-flush-no-synthetic-terminal");
            Assert.ThrowsExactly<WorkerNdjsonTransportException>(host.Dispose, "worker-terminal-flush-emitter-disposal");
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    [TestMethod]
    [TestCategory("Change23")]
    public async Task ShutdownBeforeAcceptance_ReturnsCancelledWithoutTerminal()
    {
        var fixtureRoot = CreateFixtureRoot();
        var readiness = new BlockingReadiness([]);
        var finality = new RecordingPreRequestFinality([]);

        try
        {
            var host = BuildConfiguredHost(
                fixtureRoot,
                new CompletedInitializer([]),
                readiness,
                new StaticAcquirer(InitialProcessingRunAcquisition.EndOfInput()),
                finality);
            var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
            var run = WorkerHostFactory.RunHostAsync(host);
            await readiness.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            lifetime.StopApplication();

            var exitCode = await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(130, exitCode, "worker-shutdown-before-request-code");
            Assert.AreEqual(0, finality.Outcomes.Count, "worker-shutdown-before-request-no-terminal");
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    [TestMethod]
    [TestCategory("Change23")]
    public async Task ShutdownDuringAcceptedRun_AttemptsCancelledTerminalAndReturns130()
    {
        var fixtureRoot = CreateFixtureRoot();
        var lease = new AcceptedLease(CreateRequest());
        var executor = new CancellationResultExecutor();
        var finality = new RecordingAcceptedFinality();

        try
        {
            var host = BuildAcceptedHost(fixtureRoot, lease, executor, finality);
            var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
            var run = WorkerHostFactory.RunHostAsync(host);
            await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            lifetime.StopApplication();

            var exitCode = await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(130, exitCode, "worker-shutdown-during-request-code");
            Assert.AreEqual(1, finality.Completed.Count, "worker-shutdown-during-request-terminal-once");
            Assert.AreEqual(ImmichReverseGeo.Core.Models.ProcessingRunOutcome.Cancelled, finality.Completed[0].Result.Outcome, "worker-shutdown-during-request-cancelled-terminal");
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    [TestMethod]
    [TestCategory("Change23")]
    public async Task ShutdownAfterTerminalStarts_DoesNotRetroactivelyReplaceCompletion()
    {
        var fixtureRoot = CreateFixtureRoot();
        var lease = new AcceptedLease(CreateRequest());
        var finality = new GatedAcceptedFinality();
        var executor = new RecordingExecutor((request, _, _) => Task.FromResult(CreateResult(request, ImmichReverseGeo.Core.Models.ProcessingRunOutcome.Completed)));

        try
        {
            var host = BuildAcceptedHost(fixtureRoot, lease, executor, finality);
            var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
            var run = WorkerHostFactory.RunHostAsync(host);
            await finality.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            lifetime.StopApplication();
            finality.Release();

            var exitCode = await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(0, exitCode, "worker-shutdown-after-terminal-code");
            Assert.AreEqual(1, finality.CompleteCount, "worker-shutdown-after-terminal-once");
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    [TestMethod]
    [TestCategory("Change23")]
    [DataRow(ImmichReverseGeo.Core.Models.ProcessingRunOutcome.Completed, "neutral", 0)]
    [DataRow(ImmichReverseGeo.Core.Models.ProcessingRunOutcome.Failed, "neutral", 4)]
    [DataRow(ImmichReverseGeo.Core.Models.ProcessingRunOutcome.Cancelled, "neutral", 130)]
    [DataRow(ImmichReverseGeo.Core.Models.ProcessingRunOutcome.Completed, "input", 2)]
    [DataRow(ImmichReverseGeo.Core.Models.ProcessingRunOutcome.Failed, "input", 2)]
    [DataRow(ImmichReverseGeo.Core.Models.ProcessingRunOutcome.Cancelled, "input", 2)]
    [DataRow(ImmichReverseGeo.Core.Models.ProcessingRunOutcome.Completed, "reader", 5)]
    [DataRow(ImmichReverseGeo.Core.Models.ProcessingRunOutcome.Failed, "reader", 5)]
    [DataRow(ImmichReverseGeo.Core.Models.ProcessingRunOutcome.Cancelled, "reader", 5)]
    public async Task AcceptedResultAndPostAcceptanceInputFinality_ShareAccumulator(
        ImmichReverseGeo.Core.Models.ProcessingRunOutcome runOutcome,
        string inputOutcome,
        int expectedExitCode)
    {
        var fixtureRoot = CreateFixtureRoot();
        var inputFinality = inputOutcome switch
        {
            "input" => WorkerInputPumpFinality.InputFailure(WorkerSafeFailure.Input(ImmichReverseGeo.Core.WorkerProtocol.WorkerProtocolFailureCode.InvalidSequence)),
            "reader" => WorkerInputPumpFinality.ReaderFailure(),
            _ => WorkerInputPumpFinality.ExpectedShutdown()
        };
        var lease = new AcceptedLease(CreateRequest(), finality: inputFinality);
        var executor = new RecordingExecutor((request, _, _) => Task.FromResult(CreateResult(request, runOutcome)));
        var finality = new RecordingAcceptedFinality();

        try
        {
            using var host = BuildAcceptedHost(fixtureRoot, lease, executor, finality);
            var outcomes = host.Services.GetRequiredService<ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator>();
            await host.StartAsync();
            await host.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(expectedExitCode, outcomes.Fact.ExitCode, $"worker-accepted-shared-outcome-{runOutcome}-{inputOutcome}");
            Assert.AreEqual(1, lease.SettleCount, "worker-accepted-input-finality-once");
            Assert.AreEqual(1, finality.Completed.Count, "worker-accepted-input-terminal-once");
            Assert.AreEqual(0, finality.Failures.Count, "worker-accepted-input-no-replacement-failure-terminal");
            Assert.AreSame(lease.Request, finality.Completed[0].Request, "worker-accepted-input-request-reference");
            Assert.AreSame(executor.Result, finality.Completed[0].Result, "worker-accepted-input-result-reference");
            Assert.AreEqual(runOutcome, finality.Completed[0].Result.Outcome, "worker-accepted-input-terminal-outcome-preserved");
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    [TestMethod]
    [TestCategory("Change23")]
    public async Task GenericExecutorTypedTransportExceptionReturnsInfrastructureFive()
    {
        var fixtureRoot = CreateFixtureRoot();
        var lease = new AcceptedLease(CreateRequest());
        var finality = new RecordingAcceptedFinality();
        var executor = new RecordingExecutor((_, _, _) => throw new WorkerNdjsonTransportException(WorkerNdjsonFailureStage.Write));

        try
        {
            using var host = BuildAcceptedHost(fixtureRoot, lease, executor, finality);
            var outcomes = host.Services.GetRequiredService<ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator>();
            await host.StartAsync();
            await host.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(5, outcomes.Fact.ExitCode, "worker-generic-executor-typed-transport-infrastructure");
            Assert.AreEqual(1, finality.Failures.Count, "worker-generic-executor-failure-finality-once");
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    [TestMethod]
    [TestCategory("Change23")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task GenericAcceptedFinalityTransportException_FromCompleteOrFailReturnsInfrastructureFive(
        bool completionHook)
    {
        var fixtureRoot = CreateFixtureRoot();
        var lease = new AcceptedLease(CreateRequest());
        var finality = new TransportThrowingAcceptedFinality(completionHook);
        var executor = new RecordingExecutor((request, _, _) => completionHook
            ? Task.FromResult(CreateResult(request, ImmichReverseGeo.Core.Models.ProcessingRunOutcome.Completed))
            : throw new InvalidOperationException("executor-failure-before-generic-fail-hook"));

        try
        {
            using var errorWriter = new StringWriter();
            var exitCode = await RunToBoundaryAsync(
                BuildAcceptedHost(fixtureRoot, lease, executor, finality),
                errorWriter);

            Assert.AreEqual(5, exitCode, "worker-generic-finality-exception-infrastructure-code");
            Assert.AreEqual(completionHook ? 1 : 0, finality.CompleteCount, "worker-generic-complete-hook-count");
            Assert.AreEqual(completionHook ? 0 : 1, finality.FailCount, "worker-generic-fail-hook-count");
            Assert.AreEqual(
                "worker-exit-summary outcome=infrastructure-failure phase=execution message=worker infrastructure failed" + Environment.NewLine,
                errorWriter.ToString(),
                "worker-generic-finality-exception-summary");
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    [TestMethod]
    [TestCategory("Change23")]
    [DataRow("write")]
    [DataRow("partial-write")]
    [DataRow("flush")]
    [DataRow("broken-pipe")]
    public async Task InjectedStdoutFailure_ReturnsSixWithoutRetryOrTerminal(string failureMode)
    {
        var fixtureRoot = CreateFixtureRoot();
        using var errorWriter = new StringWriter();
        var outputFactory = new FaultingOutputStreamFactory(failureMode);

        try
        {
            var builder = CreateWorkerBuilder(CreateContext(fixtureRoot));
            ReplaceSingleton<IWorkerStartupInitializer>(builder.Services, new CompletedInitializer([]));
            ReplaceSingleton<IWorkerTransportAvailability>(builder.Services, new ConfiguredTransport());
            builder.Services.RemoveAll<IWorkerNdjsonOutputStreamFactory>();
            builder.Services.AddSingleton<IWorkerNdjsonOutputStreamFactory>(outputFactory);
            builder.Services.RemoveAll<IWorkerStandardInputStreamFactory>();
            builder.Services.AddSingleton<IWorkerStandardInputStreamFactory>(new EmptyInputFactory());

            var exitCode = await RunToBoundaryAsync(builder.Build(), errorWriter);

            Assert.AreEqual(6, exitCode, $"worker-stdout-code-{failureMode}");
            Assert.AreEqual(1, outputFactory.OpenCount, $"worker-stdout-open-once-{failureMode}");
            Assert.AreEqual(1, outputFactory.Output.WriteCount, $"worker-stdout-write-once-{failureMode}");
            CollectionAssert.AreEqual(
                failureMode == "flush" ? new[] { "open", "write", "flush" } : new[] { "open", "write" },
                outputFactory.Ledger,
                $"worker-stdout-exact-ledger-{failureMode}");
            Assert.AreEqual(
                failureMode == "flush" ? 1 : 0,
                outputFactory.Output.ToArray().Count(value => value == (byte)'\n'),
                $"worker-stdout-no-synthetic-terminal-{failureMode}");
            Assert.AreEqual(
                "worker-exit-summary outcome=output-transport-failure phase=output message=worker output transport failed" + Environment.NewLine,
                errorWriter.ToString(),
                $"worker-stdout-summary-{failureMode}");
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
    [TestCategory("Change23")]
    public async Task Runner_DisposesRealHostProviderOwnedAsyncResource()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<AsyncDisposeSentinel>();
        builder.Services.AddSingleton<ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator>();
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

    [TestMethod]
    [TestCategory("Change23")]
    public async Task Runner_ReturnsOnlyAfterProviderCleanupCompletes()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<GatedAsyncDisposeSentinel>();
        builder.Services.AddSingleton<ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator>();
        using var host = builder.Build();
        var sentinel = host.Services.GetRequiredService<GatedAsyncDisposeSentinel>();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var applicationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lifetime.ApplicationStarted.Register(() => applicationStarted.TrySetResult());

        var run = WorkerHostFactory.RunHostAsync(host);
        await applicationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lifetime.StopApplication();
        await sentinel.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(run.IsCompleted, "worker-result-waits-for-provider-cleanup");
        sentinel.Release();

        var exitCode = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, exitCode, "worker-cleanup-completed-exit-code");
        Assert.IsTrue(sentinel.Disposed, "worker-provider-cleanup-completed-before-return");
    }

    [TestMethod]
    [TestCategory("Change23")]
    [DataRow(false, 5)]
    [DataRow(true, 5)]
    public async Task Runner_GenericLateDisposalFaultOverridesLowerExecutorOutcomeAsInfrastructure(
        bool outputFault,
        int expectedExitCode)
    {
        var lifetime = new ControllableApplicationLifetime();
        lifetime.StopApplication();
        var outcomes = new ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator();
        outcomes.Add(ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitFact.ExecutionFailure());
        using var services = new ServiceCollection()
            .AddSingleton<IHostApplicationLifetime>(lifetime)
            .AddSingleton(outcomes)
            .BuildServiceProvider();
        var disposalFailure = outputFault
            ? new WorkerNdjsonTransportException(WorkerNdjsonFailureStage.Disposal)
            : new InvalidOperationException("LATE_DISPOSAL_SECRET");
        var host = new FaultingHost(services, null, disposalFailure);

        var exitCode = await WorkerHostFactory.RunHostAsync(host);

        Assert.AreEqual(expectedExitCode, exitCode, "worker-late-disposal-precedence");
        Assert.AreEqual(1, host.DisposeAsyncCount, "worker-late-disposal-once");
    }

    [TestMethod]
    [TestCategory("Change23")]
    public async Task Runner_LateOutputDisposalAfterFlushedTerminalReturnsSixAndPreservesTerminal()
    {
        var fixtureRoot = CreateFixtureRoot();
        var ledger = new List<string>();
        var output = new LateDisposalFaultStream(ledger);
        var lease = new AcceptedLease(CreateRequest(), ledger: ledger);
        var finality = new RecordingAcceptedFinality(ledger);
        using var errorWriter = new LedgerTextWriter(ledger);

        try
        {
            var builder = CreateAcceptedBuilder(
                fixtureRoot,
                lease,
                new TerminalFinishingExecutor(),
                finality);
            builder.Services.RemoveAll<TimeProvider>();
            builder.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(DateTimeOffset.UnixEpoch));
            builder.Services.RemoveAll<WorkerNdjsonEmitter>();
            builder.Services.AddSingleton(sp => new WorkerNdjsonEmitter(
                output,
                WorkerNdjsonOutputStreamOwnership.Owned,
                sp.GetRequiredService<TimeProvider>(),
                NullLogger<WorkerNdjsonEmitter>.Instance,
                sp.GetRequiredService<ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator>()));
            builder.Services.RemoveAll<IWorkerReadinessPublisher>();
            builder.Services.AddSingleton<IWorkerReadinessPublisher>(sp => sp.GetRequiredService<WorkerNdjsonEmitter>());
            builder.Services.RemoveAll<ImmichReverseGeo.Core.Processing.IProcessingEventReporter>();
            builder.Services.AddSingleton<ImmichReverseGeo.Core.Processing.IProcessingEventReporter>(sp =>
                new WorkerNdjsonProcessingEventReporter(sp.GetRequiredService<WorkerNdjsonEmitter>()));
            var host = builder.Build();
            var outcomes = host.Services.GetRequiredService<ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator>();

            var exitCode = await RunToBoundaryAsync(host, errorWriter);
            var frames = System.Text.Encoding.UTF8.GetString(output.ToArray())
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);

            Assert.AreEqual(6, exitCode, "worker-late-output-after-terminal-code");
            Assert.AreEqual(4, frames.Length, "worker-late-output-ready-started-eligibility-terminal");
            Assert.AreEqual(
                "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"terminal\",\"type\":\"completed\",\"sequence\":4,\"timestampUtc\":\"1970-01-01T00:00:00.0000000Z\",\"runId\":\"6ed5cbcf-068d-40d5-b36a-badfcdfb0e76\",\"payload\":{\"trigger\":\"run-once\",\"startedAtUtc\":\"1970-01-01T00:00:00.0000000Z\",\"endedAtUtc\":\"1970-01-01T00:00:00.0000000Z\",\"processedCount\":0,\"updatedCount\":0,\"skippedCount\":0,\"failedCount\":0,\"failureMessage\":null}}",
                frames[^1],
                "worker-late-output-terminal-full-frame");
            using var terminal = System.Text.Json.JsonDocument.Parse(frames[^1]);
            var terminalRoot = terminal.RootElement;
            Assert.AreEqual("terminal", terminalRoot.GetProperty("category").GetString(), "worker-late-output-terminal-category");
            Assert.AreEqual("completed", terminalRoot.GetProperty("type").GetString(), "worker-late-output-terminal-type");
            Assert.AreEqual(lease.Request.RunId.ToString(), terminalRoot.GetProperty("runId").GetString(), "worker-late-output-terminal-run-id");
            Assert.AreEqual(1, finality.Completed.Count, "worker-late-output-finality-once");
            Assert.AreEqual(1, output.DisposeCount, "worker-late-output-real-emitter-disposal-once");
            Assert.AreEqual("output", outcomes.Fact.Diagnostic.Phase, "worker-late-output-stage");
            CollectionAssert.AreEqual(
                new[] { "flush-1", "execution-starting", "flush-2", "flush-3", "flush-4", "complete", "settle", "lease-dispose", "stdout-dispose", "stderr-summary", "stderr-flush" },
                ledger,
                "worker-late-output-cleanup-and-top-level-summary-ledger: " + string.Join(",", ledger));
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    [TestMethod]
    [TestCategory("Change23")]
    public async Task Runner_StderrFailureIsBestEffortOnceAndDoesNotChangeClassification()
    {
        var lifetime = new ControllableApplicationLifetime();
        lifetime.StopApplication();
        using var services = new ServiceCollection()
            .AddSingleton<IHostApplicationLifetime>(lifetime)
            .AddSingleton<ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator>()
            .BuildServiceProvider();
        var host = new FaultingHost(services, new InvalidOperationException("START_SECRET"), new InvalidOperationException("DISPOSE_SECRET"));
        var errorWriter = new ThrowingCountingTextWriter();

        var exitCode = await RunToBoundaryAsync(host, errorWriter);

        Assert.AreEqual(5, exitCode, "worker-stderr-failure-keeps-code");
        Assert.AreEqual(1, errorWriter.WriteLineCount, "worker-stderr-failure-no-retry");
        Assert.AreEqual(0, errorWriter.FlushCount, "worker-stderr-failure-stops-after-write");
    }

    [TestMethod]
    [TestCategory("Change23")]
    public async Task Runner_FinalSummaryIsExactlyOnceAfterEarlierLogsAndContainsNoRawData()
    {
        var forbiddenSentinels = new[]
        {
            "--raw-argument-sentinel",
            "stdin-request-bytes-sentinel",
            "protocol-payload-sentinel",
            "configuration-value-sentinel",
            "Password=CREDENTIAL_SENTINEL",
            "Server=CONNECTION_STRING_SENTINEL",
            "SELECT SQL_SENTINEL",
            "exception-message-sentinel",
            "stack-trace-sentinel"
        };
        var lifetime = new ControllableApplicationLifetime();
        lifetime.StopApplication();
        using var services = new ServiceCollection()
            .AddSingleton<IHostApplicationLifetime>(lifetime)
            .AddSingleton<ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator>()
            .BuildServiceProvider();
        var host = new FaultingHost(
            services,
            new InvalidOperationException(string.Join('|', forbiddenSentinels[..5])),
            new InvalidOperationException(string.Join('|', forbiddenSentinels[5..])));
        using var errorWriter = new StringWriter();
        errorWriter.WriteLine("earlier-safe-log");

        var exitCode = await RunToBoundaryAsync(host, errorWriter);
        errorWriter.Write("writer-still-alive");
        var stderr = errorWriter.ToString();

        Assert.AreEqual(5, exitCode, "worker-safe-summary-code");
        Assert.IsTrue(stderr.StartsWith("earlier-safe-log" + Environment.NewLine, StringComparison.Ordinal), "worker-summary-after-earlier-log");
        Assert.AreEqual(1, stderr.Split(ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitDiagnostic.FinalSummaryMarker, StringSplitOptions.None).Length - 1, "worker-summary-marker-once");
        Assert.IsTrue(stderr.Contains("worker-exit-summary outcome=infrastructure-failure phase=cleanup message=worker infrastructure failed", StringComparison.Ordinal), "worker-safe-summary-exact");
        foreach (var sentinel in forbiddenSentinels)
        {
            Assert.IsFalse(stderr.Contains(sentinel, StringComparison.Ordinal), $"worker-safe-summary-excludes-{sentinel}");
        }
        Assert.IsFalse(stderr.Contains("{\"type\"", StringComparison.Ordinal), "worker-safe-summary-no-stdout-frame");
        Assert.IsTrue(stderr.EndsWith("writer-still-alive", StringComparison.Ordinal), "worker-error-writer-alive-after-provider-disposal");
    }

    [TestMethod]
    [TestCategory("Change23")]
    public async Task TransportUnavailable_UsesSharedInfrastructureFactAfterProviderCleanup()
    {
        var fixtureRoot = CreateFixtureRoot();
        using var errorWriter = new StringWriter();

        try
        {
            var builder = CreateWorkerBuilder(CreateContext(fixtureRoot));
            ReplaceSingleton<IWorkerStartupInitializer>(builder.Services, new CompletedInitializer([]));
            ReplaceSingleton<IWorkerTransportAvailability>(builder.Services, new UnconfiguredTransport());
            builder.Services.AddSingleton<GatedAsyncDisposeSentinel>();
            using var host = builder.Build();
            var sentinel = host.Services.GetRequiredService<GatedAsyncDisposeSentinel>();

            var run = RunToBoundaryAsync(host, errorWriter);
            await sentinel.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(run.IsCompleted, "worker-transport-result-waits-for-cleanup");
            Assert.AreEqual(string.Empty, errorWriter.ToString(), "worker-transport-no-summary-before-cleanup");
            sentinel.Release();

            var exitCode = await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(5, exitCode, "worker-transport-infrastructure-code");
            Assert.AreEqual(
                "worker-exit-summary outcome=infrastructure-failure phase=transport message=worker infrastructure failed" + Environment.NewLine,
                errorWriter.ToString(),
                "worker-transport-final-summary");
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    [TestMethod]
    [TestCategory("Change23")]
    public async Task StdinSource_OpensAndReadsOnlyAfterReadyCompletion()
    {
        var fixtureRoot = CreateFixtureRoot();
        var calls = new List<string>();
        var input = new GatedReadStream(calls);
        var factory = new RecordingStandardInputFactory(input, calls);
        var readiness = new GatedReadiness(calls);

        try
        {
            var builder = CreateWorkerBuilder(CreateContext(fixtureRoot));
            ReplaceSingleton<IWorkerStartupInitializer>(builder.Services, new CompletedInitializer(calls));
            ReplaceSingleton<IWorkerTransportAvailability>(builder.Services, new ConfiguredTransport());
            ReplaceSingleton<IWorkerReadinessPublisher>(builder.Services, readiness);
            ReplaceSingleton<IWorkerPreRequestFinality>(builder.Services, new RecordingPreRequestFinality(calls));
            builder.Services.RemoveAll<IWorkerStandardInputStreamFactory>();
            builder.Services.AddSingleton<IWorkerStandardInputStreamFactory>(factory);
            using var host = builder.Build();

            await host.StartAsync();
            await readiness.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(0, factory.OpenCount, "worker-stdin-pending-ready-no-open");
            Assert.AreEqual(0, input.ReadCount, "worker-stdin-pending-ready-no-read");
            readiness.Release();
            await input.FirstRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
            CollectionAssert.AreEqual(new[] { "initialise", "ready-started", "ready-completed", "stdin-open", "stdin-read" }, calls, "worker-stdin-ready-before-open-read");
            input.Complete();
            await host.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    private static HostApplicationBuilder CreateWorkerBuilder(ApplicationCompositionContext context)
    {
        return WorkerHostFactory.CreateBuilder(
            context,
            new ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator());
    }

    private static IHost BuildWorkerHost(ApplicationCompositionContext context)
    {
        return WorkerHostFactory.Build(
            context,
            new ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator());
    }

    private static async Task<int> RunToBoundaryAsync(IHost host, TextWriter errorWriter)
    {
        var outcomes = host.Services.GetRequiredService<ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator>();
        await WorkerHostFactory.RunHostAsync(host, outcomes);
        return InternalWorkerProcessExitBoundary.Complete(outcomes.Fact, errorWriter);
    }

    private sealed class CountingNeverOpenInputFactory : IWorkerStandardInputStreamFactory
    {
        internal int OpenCount { get; private set; }

        internal int ReadCount { get; private set; }

        public Stream OpenStandardInput()
        {
            OpenCount++;
            return new NeverReadStream(this);
        }

        private sealed class NeverReadStream(CountingNeverOpenInputFactory owner) : Stream
        {
            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => 0;

            public override long Position
            {
                get => 0;
                set => throw new NotSupportedException();
            }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                owner.ReadCount++;
                return ValueTask.FromResult(0);
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
        }
    }

    private sealed class EmptyInputFactory : IWorkerStandardInputStreamFactory
    {
        public Stream OpenStandardInput()
        {
            return new MemoryStream();
        }
    }

    private sealed class GatedReadiness(List<string> calls) : IWorkerReadinessPublisher
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task PublishAsync(CancellationToken cancellationToken)
        {
            calls.Add("ready-started");
            Started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            calls.Add("ready-completed");
        }

        internal void Release()
        {
            _release.TrySetResult();
        }
    }

    private sealed class RecordingStandardInputFactory(GatedReadStream input, List<string> calls) : IWorkerStandardInputStreamFactory
    {
        internal int OpenCount { get; private set; }

        public Stream OpenStandardInput()
        {
            OpenCount++;
            calls.Add("stdin-open");
            return input;
        }
    }

    private sealed class GatedReadStream(List<string> calls) : Stream
    {
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource FirstRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ReadCount { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            calls.Add("stdin-read");
            FirstRead.TrySetResult();
            await _completed.Task.WaitAsync(cancellationToken);
            return 0;
        }

        internal void Complete()
        {
            _completed.TrySetResult();
        }
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
        var builder = CreateWorkerBuilder(CreateContext(fixtureRoot));
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
        var builder = CreateWorkerBuilder(CreateContext(fixtureRoot));
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
        private readonly WorkerInputPumpFinality _finality;

        public AcceptedLease(
            ImmichReverseGeo.Core.Models.ProcessingRunRequest request,
            CancellationToken cancellationToken = default,
            List<string>? ledger = null,
            WorkerInputPumpFinality? finality = null)
        {
            Request = request;
            CancellationToken = cancellationToken;
            _ledger = ledger;
            _finality = finality ?? WorkerInputPumpFinality.ExpectedShutdown();
        }

        public int NotifyExecutionStartingCount { get; private set; }

        public int SettleCount { get; private set; }

        public int DisposeCount { get; private set; }

        public ImmichReverseGeo.Core.Models.ProcessingRunRequest Request { get; }

        public CancellationToken CancellationToken { get; }

        public void NotifyExecutionStarting()
        {
            NotifyExecutionStartingCount++;
            _ledger?.Add("execution-starting");
        }

        public ValueTask<WorkerInputPumpFinality> SettleAsync(CancellationToken cancellationToken)
        {
            SettleCount++;
            _ledger?.Add("settle");
            return ValueTask.FromResult(_finality);
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

    private sealed class AdjudicatedBusyExecutor(
        ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator outcomes)
        : ImmichReverseGeo.Web.Services.IProcessingRunExecutor
    {
        public async Task<ImmichReverseGeo.Core.Models.ProcessingRunResult> ExecuteAsync(
            ImmichReverseGeo.Core.Models.ProcessingRunRequest request,
            ImmichReverseGeo.Core.Processing.IProcessingEventReporter reporter,
            CancellationToken cancellationToken)
        {
            var startedAtUtc = DateTimeOffset.UnixEpoch;
            var session = await reporter.OpenRunAsync(request, startedAtUtc, cancellationToken);
            outcomes.Add(ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitFact.Busy());
            var result = new ImmichReverseGeo.Core.Models.ProcessingRunResult(
                request,
                startedAtUtc,
                startedAtUtc,
                0,
                0,
                0,
                0,
                ImmichReverseGeo.Core.Models.ProcessingRunOutcome.Failed,
                "worker advisory lock is busy");
            await session.FinishAsync(result);
            return result;
        }
    }

    private sealed class TerminalFinishingExecutor : ImmichReverseGeo.Web.Services.IProcessingRunExecutor
    {
        public async Task<ImmichReverseGeo.Core.Models.ProcessingRunResult> ExecuteAsync(
            ImmichReverseGeo.Core.Models.ProcessingRunRequest request,
            ImmichReverseGeo.Core.Processing.IProcessingEventReporter reporter,
            CancellationToken cancellationToken)
        {
            var startedAtUtc = DateTimeOffset.UnixEpoch;
            var session = await reporter.OpenRunAsync(request, startedAtUtc, cancellationToken);
            await session.DetermineEligibilityAsync(0, cancellationToken);
            var result = CreateResult(request, ImmichReverseGeo.Core.Models.ProcessingRunOutcome.Completed);
            await session.FinishAsync(result);
            return result;
        }
    }

    private sealed class CancellationResultExecutor : ImmichReverseGeo.Web.Services.IProcessingRunExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ImmichReverseGeo.Core.Models.ProcessingRunResult> ExecuteAsync(
            ImmichReverseGeo.Core.Models.ProcessingRunRequest request,
            ImmichReverseGeo.Core.Processing.IProcessingEventReporter reporter,
            CancellationToken cancellationToken)
        {
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(() => cancelled.TrySetResult());
            Started.TrySetResult();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return CreateResult(request, ImmichReverseGeo.Core.Models.ProcessingRunOutcome.Cancelled);
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
            ledger?.Add("complete");
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

        public void NotifyExecutionStarting()
        {
        }

        public ValueTask<WorkerInputPumpFinality> SettleAsync(CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(WorkerInputPumpFinality.ExpectedShutdown());
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class UnconfiguredTransport : IWorkerTransportAvailability
    {
        public bool IsConfigured => false;
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

    private sealed class StaticAcquirer(InitialProcessingRunAcquisition outcome) : IInitialProcessingRunAcquirer
    {
        public Task<InitialProcessingRunAcquisition> AcquireAsync(CancellationToken cancellationToken)
        {
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

    private sealed class GatedAsyncDisposeSentinel : IAsyncDisposable
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource DisposeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool Disposed { get; private set; }

        internal void Release()
        {
            _release.TrySetResult();
        }

        public async ValueTask DisposeAsync()
        {
            DisposeStarted.TrySetResult();
            await _release.Task;
            Disposed = true;
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
        var service = new InternalWorkerLifecycleService(factory, finalityLifetime, Microsoft.Extensions.Logging.Abstractions.NullLogger<InternalWorkerLifecycleService>.Instance, new ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator());
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
                var builder = CreateWorkerBuilder(CreateContext(fixtureRoot));
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
            var builder = CreateWorkerBuilder(CreateContext(fixtureRoot));
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
            var builder = CreateWorkerBuilder(CreateContext(fixtureRoot));
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
    [TestCategory("Change23")]
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
            CollectionAssert.AreEqual(new[] { "execution-starting", "complete" }, ledger, "worker-ledger-no-cleanup-while-gated");
            finality.Release();
            await host.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
            CollectionAssert.AreEqual(new[] { "execution-starting", "complete", "settle", "lease-dispose", "scope-dispose", "stop" }, ledger, "worker-ledger-order");
        }
        finally { DeleteFixtureRoot(fixtureRoot); }
    }

    [TestMethod]
    [TestCategory("Change23")]
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
            Assert.AreEqual(5, fixture.Outcomes.Fact.ExitCode, $"worker-stop-failure-accumulator-code-{label}");
            Assert.AreEqual("cleanup", fixture.Outcomes.Fact.Diagnostic.Phase, $"worker-stop-failure-accumulator-phase-{label}");
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
        private DirectLifecycleFixture(
            ServiceProvider provider,
            InternalWorkerLifecycleService service,
            CountingScopeFactory scopeFactory,
            CountingInitializer initializer,
            RecordingAcquirer acquirer,
            RecordingPreRequestFinality finality,
            ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator outcomes)
        {
            _provider = provider;
            Service = service;
            ScopeFactory = scopeFactory;
            Initializer = initializer;
            Acquirer = acquirer;
            Finality = finality;
            Outcomes = outcomes;
        }
        public InternalWorkerLifecycleService Service { get; }
        public CountingScopeFactory ScopeFactory { get; }
        public CountingInitializer Initializer { get; }
        public RecordingAcquirer Acquirer { get; }
        public RecordingPreRequestFinality Finality { get; }
        public ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator Outcomes { get; }
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
            var outcomes = new ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator();
            return new DirectLifecycleFixture(
                provider,
                new InternalWorkerLifecycleService(
                    factory,
                    lifetime,
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<InternalWorkerLifecycleService>.Instance,
                    outcomes),
                factory,
                initializer,
                acquirer,
                finality,
                outcomes);
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

        public void NotifyExecutionStarting()
        {
            ledger.Add("execution-starting");
        }

        public ValueTask<WorkerInputPumpFinality> SettleAsync(CancellationToken cancellationToken)
        {
            ledger.Add("settle");
            return ValueTask.FromResult(WorkerInputPumpFinality.ExpectedShutdown());
        }

        public ValueTask DisposeAsync()
        {
            ledger.Add("lease-dispose");
            return ValueTask.CompletedTask;
        }
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
    [TestCategory("Change23")]
    [DataRow("settle")]
    [DataRow("dispose")]
    public async Task AcceptedCleanup_RawOutOfMemoryEscapesOnlyAfterRemainingOwnedCleanup(string boundary)
    {
        var calls = new List<string>();
        var lifetime = new ControllableApplicationLifetime();
        var rawFailure = new OutOfMemoryException("controlled-cleanup-oom-" + boundary);
        var lease = new RawOutOfMemoryLease(CreateRequest(), boundary, rawFailure);
        var services = new ServiceCollection();
        services.AddSingleton<IWorkerStartupInitializer>(new CompletedInitializer(calls));
        services.AddSingleton<IWorkerTransportAvailability>(new ConfiguredTransport());
        services.AddSingleton<IWorkerReadinessPublisher>(new RecordingReadiness(calls, Task.CompletedTask));
        services.AddSingleton<IInitialProcessingRunAcquirer>(new RecordingAcquirer(calls, Task.CompletedTask, InitialProcessingRunAcquisition.Accept(lease)));
        services.AddSingleton<IWorkerPreRequestFinality>(new RecordingPreRequestFinality(calls));
        services.AddSingleton<IWorkerAcceptedRunFinality>(new RecordingAcceptedFinality());
        services.AddSingleton<ImmichReverseGeo.Web.Services.IProcessingRunExecutor>(new RecordingExecutor((request, _, _) => Task.FromResult(CreateResult(request, ImmichReverseGeo.Core.Models.ProcessingRunOutcome.Completed))));
        services.AddSingleton<ImmichReverseGeo.Core.Processing.IProcessingEventReporter>(new TestReporter());
        await using var provider = services.BuildServiceProvider();
        var scopeFactory = new CountingScopeFactory(provider, calls);
        var outcomes = new ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator();
        var service = new InternalWorkerLifecycleService(
            scopeFactory,
            lifetime,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<InternalWorkerLifecycleService>.Instance,
            outcomes);
        await service.StartAsync(CancellationToken.None);
        lifetime.SignalStarted();

        var thrown = await Assert.ThrowsExactlyAsync<OutOfMemoryException>(() => service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.AreSame(rawFailure, thrown, "worker-cleanup-oom-reference");
        Assert.AreEqual(1, lease.SettleCount, "worker-cleanup-oom-settle-once");
        Assert.AreEqual(1, lease.DisposeCount, "worker-cleanup-oom-lease-dispose-once");
        Assert.AreEqual(1, scopeFactory.DisposeCount, "worker-cleanup-oom-scope-dispose-once");
        Assert.AreEqual(1, lifetime.StopCount, "worker-cleanup-oom-stop-once");
        Assert.AreEqual(0, outcomes.Fact.ExitCode, "worker-cleanup-oom-does-not-map-raw-oom");
    }

    [TestMethod]
    [TestCategory("Change23")]
    public async Task AcceptedCleanup_MultipleRawOutOfMemoryFailuresPreserveFirstFatalReference()
    {
        var calls = new List<string>();
        var lifetime = new ControllableApplicationLifetime();
        var firstFatal = new OutOfMemoryException("controlled-first-settle-oom");
        var laterFatal = new OutOfMemoryException("controlled-later-dispose-oom");
        var lease = new MultipleRawOutOfMemoryLease(CreateRequest(), firstFatal, laterFatal);
        var services = new ServiceCollection();
        services.AddSingleton<IWorkerStartupInitializer>(new CompletedInitializer(calls));
        services.AddSingleton<IWorkerTransportAvailability>(new ConfiguredTransport());
        services.AddSingleton<IWorkerReadinessPublisher>(new RecordingReadiness(calls, Task.CompletedTask));
        services.AddSingleton<IInitialProcessingRunAcquirer>(new RecordingAcquirer(calls, Task.CompletedTask, InitialProcessingRunAcquisition.Accept(lease)));
        services.AddSingleton<IWorkerPreRequestFinality>(new RecordingPreRequestFinality(calls));
        services.AddSingleton<IWorkerAcceptedRunFinality>(new RecordingAcceptedFinality());
        services.AddSingleton<ImmichReverseGeo.Web.Services.IProcessingRunExecutor>(new RecordingExecutor((request, _, _) => Task.FromResult(CreateResult(request, ImmichReverseGeo.Core.Models.ProcessingRunOutcome.Completed))));
        services.AddSingleton<ImmichReverseGeo.Core.Processing.IProcessingEventReporter>(new TestReporter());
        await using var provider = services.BuildServiceProvider();
        var scopeFactory = new CountingScopeFactory(provider, calls);
        var service = new InternalWorkerLifecycleService(
            scopeFactory,
            lifetime,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<InternalWorkerLifecycleService>.Instance,
            new ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator());
        await service.StartAsync(CancellationToken.None);
        lifetime.SignalStarted();

        var thrown = await Assert.ThrowsExactlyAsync<OutOfMemoryException>(() => service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.AreSame(firstFatal, thrown, "worker-accepted-first-fatal-reference");
        Assert.AreEqual(1, lease.SettleCount, "worker-accepted-first-fatal-settle-once");
        Assert.AreEqual(1, lease.DisposeCount, "worker-accepted-first-fatal-dispose-once");
        Assert.AreEqual(1, scopeFactory.DisposeCount, "worker-accepted-first-fatal-scope-dispose-once");
        Assert.AreEqual(1, lifetime.StopCount, "worker-accepted-first-fatal-stop-once");
    }

    [TestMethod]
    [TestCategory("Change23")]
    public async Task LifecycleCleanup_MultipleRawOutOfMemoryFailuresPreserveFirstFatalReference()
    {
        var calls = new List<string>();
        var lifetime = new ControllableApplicationLifetime();
        var firstFatal = new OutOfMemoryException("controlled-first-scope-oom");
        var laterFatal = new OutOfMemoryException("controlled-later-stop-oom");
        await using var fixture = DirectLifecycleFixture.Create(lifetime, calls, InitialProcessingRunAcquisition.EndOfInput());
        fixture.ScopeFactory.DisposeFailure = firstFatal;
        lifetime.StopFailure = laterFatal;
        await fixture.Service.StartAsync(CancellationToken.None);
        lifetime.SignalStarted();
        await fixture.Initializer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Initializer.Release();

        var thrown = await Assert.ThrowsExactlyAsync<OutOfMemoryException>(() => fixture.Service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.AreSame(firstFatal, thrown, "worker-lifecycle-first-fatal-reference");
        Assert.AreEqual(1, fixture.ScopeFactory.DisposeCount, "worker-lifecycle-first-fatal-scope-dispose-once");
        Assert.AreEqual(1, lifetime.StopCount, "worker-lifecycle-first-fatal-stop-once");
    }

    [TestMethod]
    [TestCategory("Change23")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task AcceptedFinality_RawOutOfMemoryEscapesAfterOwnedCleanup(bool completionPath)
    {
        var calls = new List<string>();
        var lifetime = new ControllableApplicationLifetime();
        var rawFailure = new OutOfMemoryException("controlled-finality-oom");
        var lease = new FaultingLease(CreateRequest(), settleFault: false, disposeFault: false);
        var finality = new OutOfMemoryAcceptedFinality(rawFailure, completionPath);
        var services = new ServiceCollection();
        services.AddSingleton<IWorkerStartupInitializer>(new CompletedInitializer(calls));
        services.AddSingleton<IWorkerTransportAvailability>(new ConfiguredTransport());
        services.AddSingleton<IWorkerReadinessPublisher>(new RecordingReadiness(calls, Task.CompletedTask));
        services.AddSingleton<IInitialProcessingRunAcquirer>(new RecordingAcquirer(calls, Task.CompletedTask, InitialProcessingRunAcquisition.Accept(lease)));
        services.AddSingleton<IWorkerPreRequestFinality>(new RecordingPreRequestFinality(calls));
        services.AddSingleton<IWorkerAcceptedRunFinality>(finality);
        services.AddSingleton<ImmichReverseGeo.Web.Services.IProcessingRunExecutor>(new RecordingExecutor((request, _, _) => completionPath
            ? Task.FromResult(CreateResult(request, ImmichReverseGeo.Core.Models.ProcessingRunOutcome.Completed))
            : Task.FromException<ImmichReverseGeo.Core.Models.ProcessingRunResult>(new InvalidOperationException("executor-primary"))));
        services.AddSingleton<ImmichReverseGeo.Core.Processing.IProcessingEventReporter>(new TestReporter());
        await using var provider = services.BuildServiceProvider();
        var scopeFactory = new CountingScopeFactory(provider, calls);
        var outcomes = new ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator();
        var service = new InternalWorkerLifecycleService(
            scopeFactory,
            lifetime,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<InternalWorkerLifecycleService>.Instance,
            outcomes);
        await service.StartAsync(CancellationToken.None);
        lifetime.SignalStarted();

        var thrown = await Assert.ThrowsExactlyAsync<OutOfMemoryException>(() => service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.AreSame(rawFailure, thrown, "worker-finality-oom-reference");
        Assert.AreEqual(completionPath ? 1 : 0, finality.CompleteCount, "worker-finality-oom-complete-count");
        Assert.AreEqual(completionPath ? 0 : 1, finality.FailCount, "worker-finality-oom-fail-count");
        Assert.AreEqual(1, lease.SettleCount, "worker-finality-oom-settle-once");
        Assert.AreEqual(1, lease.DisposeCount, "worker-finality-oom-lease-dispose-once");
        Assert.AreEqual(1, scopeFactory.DisposeCount, "worker-finality-oom-scope-dispose-once");
        Assert.AreEqual(!completionPath, outcomes.HasFact, "worker-finality-oom-preclassification-state");
        Assert.AreEqual(completionPath ? 0 : 5, outcomes.Fact.ExitCode, "worker-finality-oom-sentinel-or-prior-infrastructure");
    }

    [TestMethod]
    [TestCategory("Change23")]
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
            var finality = new FaultingAcceptedFinality(secondary == "failure-finality", null);
            services.AddSingleton<IWorkerAcceptedRunFinality>(finality);
            services.AddSingleton<ImmichReverseGeo.Web.Services.IProcessingRunExecutor>(new RecordingExecutor((_, _, _) => throw primary));
            services.AddSingleton<ImmichReverseGeo.Core.Processing.IProcessingEventReporter>(new TestReporter());
            await using var provider = services.BuildServiceProvider();
            var scopeFactory = new CountingScopeFactory(provider, calls) { DisposeFailure = secondary == "scope" ? new InvalidOperationException("scope-secondary") : null };
            var logger = new CapturingLogger<InternalWorkerLifecycleService>(logs.Entries);
            var outcomes = new ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator();
            var service = new InternalWorkerLifecycleService(scopeFactory, lifetime, logger, outcomes);
            await service.StartAsync(CancellationToken.None);
            lifetime.SignalStarted();
            var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5)), $"worker-{secondary}-primary");

            Assert.AreSame(primary, thrown, $"worker-{secondary}-primary-reference");
            Assert.AreEqual(0, finality.CompleteCount, $"worker-{secondary}-no-completion-hook");
            Assert.AreEqual(1, finality.FailCount, $"worker-{secondary}-one-failure-hook");
            Assert.AreEqual(1, lease.SettleCount, $"worker-{secondary}-settle-once");
            Assert.AreEqual(1, lease.DisposeCount, $"worker-{secondary}-dispose-once");
            Assert.AreEqual(1, scopeFactory.DisposeCount, $"worker-{secondary}-scope-once");
            Assert.AreEqual(1, lifetime.StopCount, $"worker-{secondary}-stop-once");
            var category = secondary == "failure-finality" ? "worker-accepted-finality-failed" : "worker-cleanup-failed";
            Assert.AreEqual(5, outcomes.Fact.ExitCode, $"worker-{secondary}-infrastructure-code");
            Assert.AreEqual(secondary == "failure-finality" ? "execution" : "cleanup", outcomes.Fact.Diagnostic.Phase, $"worker-{secondary}-diagnostic-stage");
            Assert.AreEqual(1, logs.Entries.Count(entry => entry.Message == category), $"worker-{secondary}-fixed-log-once");
            Assert.IsTrue(logs.Entries.Where(entry => entry.Message == category).All(entry => entry.Exception is null), $"worker-{secondary}-safe-log-no-exception");
    }

    [TestMethod]
    [TestCategory("Change23")]
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
            var outcomes = new ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator();
            var service = new InternalWorkerLifecycleService(scopeFactory, lifetime, new CapturingLogger<InternalWorkerLifecycleService>(logs.Entries), outcomes);
            await service.StartAsync(CancellationToken.None);
            lifetime.SignalStarted();
            var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5)), $"worker-cleanup-only-{fault}");
            Assert.AreEqual("worker-cleanup-failed", thrown.Message, $"worker-cleanup-only-{fault}-category");
            Assert.AreEqual(1, finality.CompleteCount, $"worker-cleanup-only-{fault}-no-terminal-retry");
            Assert.AreEqual(1, lease.SettleCount, $"worker-cleanup-only-{fault}-settle");
            Assert.AreEqual(1, lease.DisposeCount, $"worker-cleanup-only-{fault}-lease-dispose");
            Assert.AreEqual(1, scopeFactory.DisposeCount, $"worker-cleanup-only-{fault}-scope-dispose");
            Assert.AreEqual(1, lifetime.StopCount, $"worker-cleanup-only-{fault}-stop");
            Assert.AreEqual(5, outcomes.Fact.ExitCode, $"worker-cleanup-only-{fault}-code");
            Assert.AreEqual("cleanup", outcomes.Fact.Diagnostic.Phase, $"worker-cleanup-only-{fault}-diagnostic-stage");
            Assert.AreEqual(1, logs.Entries.Count(entry => entry.Message == "worker-cleanup-failed"), $"worker-cleanup-only-{fault}-log-category");
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
        var service = new InternalWorkerLifecycleService(scopeFactory, lifetime, new ThrowingLogger<InternalWorkerLifecycleService>(), new ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator());
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
    [TestCategory("Change23")]
    public async Task CleanupBoundaryLoggerOutOfMemory_IsSwallowedAndCannotSkipOwnedCleanup()
    {
        var calls = new List<string>();
        var lifetime = new ControllableApplicationLifetime();
        var primary = new InvalidOperationException("executor-primary-oom-logger");
        var lease = new FaultingLease(CreateRequest(), settleFault: true, disposeFault: false);
        var services = new ServiceCollection();
        services.AddSingleton<IWorkerStartupInitializer>(new CompletedInitializer(calls));
        services.AddSingleton<IWorkerTransportAvailability>(new ConfiguredTransport());
        services.AddSingleton<IWorkerReadinessPublisher>(new RecordingReadiness(calls, Task.CompletedTask));
        services.AddSingleton<IInitialProcessingRunAcquirer>(new RecordingAcquirer(calls, Task.CompletedTask, InitialProcessingRunAcquisition.Accept(lease)));
        services.AddSingleton<IWorkerPreRequestFinality>(new RecordingPreRequestFinality(calls));
        services.AddSingleton<IWorkerAcceptedRunFinality>(new FaultingAcceptedFinality(false, null));
        services.AddSingleton<ImmichReverseGeo.Web.Services.IProcessingRunExecutor>(new RecordingExecutor((_, _, _) => throw primary));
        services.AddSingleton<ImmichReverseGeo.Core.Processing.IProcessingEventReporter>(new TestReporter());
        await using var provider = services.BuildServiceProvider();
        var scopeFactory = new CountingScopeFactory(provider, calls);
        var service = new InternalWorkerLifecycleService(
            scopeFactory,
            lifetime,
            new OutOfMemoryThrowingLogger<InternalWorkerLifecycleService>(),
            new ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator());
        await service.StartAsync(CancellationToken.None);
        lifetime.SignalStarted();

        var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.AreSame(primary, thrown, "worker-oom-logger-primary-reference");
        Assert.AreEqual(1, lease.SettleCount, "worker-oom-logger-settle-once");
        Assert.AreEqual(1, lease.DisposeCount, "worker-oom-logger-lease-dispose-once");
        Assert.AreEqual(1, scopeFactory.DisposeCount, "worker-oom-logger-scope-dispose-once");
        Assert.AreEqual(1, lifetime.StopCount, "worker-oom-logger-stop-once");
    }

    [TestMethod]
    public async Task Runner_StartPrimaryAndAsyncDisposeFault_PreservesPrimaryAndLogsFixedCategoryOnce()
    {
        var logs = new CapturingLoggerProvider();
        using var services = new ServiceCollection()
            .AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(_ => Microsoft.Extensions.Logging.LoggerFactory.Create(builder => builder.Services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(logs)))
            .AddSingleton<ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator>()
            .BuildServiceProvider();
        var primary = new InvalidOperationException("runner-start-primary");
        var host = new FaultingHost(services, primary, new InvalidOperationException("runner-dispose-secondary"));

        var exitCode = await WorkerHostFactory.RunHostAsync(host);

        Assert.AreEqual(5, exitCode, "worker-runner-primary-infrastructure-code");
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
            .AddSingleton<ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator>()
            .BuildServiceProvider();
        var host = new FaultingHost(services, null, new InvalidOperationException("dispose-only"));

        var exitCode = await WorkerHostFactory.RunHostAsync(host);

        Assert.AreEqual(5, exitCode, "worker-runner-dispose-only-category");
        Assert.AreEqual(1, host.DisposeAsyncCount, "worker-runner-dispose-only-once");
        Assert.AreEqual(1, logs.Entries.Count(entry => entry.Message == "worker-host-dispose-failed"), "worker-runner-dispose-only-log-once");
    }

    [TestMethod]
    public async Task Runner_ThrowingLoggerDoesNotReplaceStartPrimaryOrSkipDispose()
    {
        var primary = new InvalidOperationException("runner-primary-throwing-logger");
        var host = new FaultingHost(new LoggerFactoryServiceProvider(new ThrowingLoggerFactory()), primary, new InvalidOperationException("dispose-secondary"));
        var exitCode = await WorkerHostFactory.RunHostAsync(host);
        Assert.AreEqual(5, exitCode, "worker-runner-throwing-logger-infrastructure-code");
        Assert.AreEqual(1, host.DisposeAsyncCount, "worker-runner-throwing-logger-dispose-once");
    }

    [TestMethod]
    [TestCategory("Change23")]
    public void FinalExitBoundary_SwallowsOutOfMemoryFromBestEffortStderrAndPreservesClassification()
    {
        var diagnosticFailure = new OutOfMemoryException("controlled-stderr-oom");
        var writer = new OutOfMemoryTextWriter(diagnosticFailure);

        var exitCode = InternalWorkerProcessExitBoundary.Complete(
            ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitFact.InputInvalid(),
            writer);

        Assert.AreEqual(2, exitCode, "worker-stderr-oom-preserves-exit");
        Assert.AreEqual(1, writer.WriteLineCount, "worker-stderr-oom-one-best-effort-attempt");
        Assert.AreEqual(0, writer.FlushCount, "worker-stderr-oom-does-not-retry-or-flush-after-write-failure");
    }

    [TestMethod]
    [TestCategory("Change23")]
    public async Task ActualProcessingExecutor_ManagedOutOfMemoryProducesFailedTerminalAndExitFour()
    {
        var fixtureRoot = CreateFixtureRoot();
        var execution = new global::ImmichReverseGeo.Tests.ExecutorFixture().EnableReporter();
        var managedFailure = new OutOfMemoryException("MANAGED_EXECUTION_OOM_SENTINEL");
        execution.CountBehavior = _ => Task.FromException<long>(managedFailure);
        var lease = new AcceptedLease(execution.Request);
        var finality = new RecordingAcceptedFinality();
        using var errorWriter = new StringWriter();

        try
        {
            var host = BuildAcceptedHost(
                fixtureRoot,
                lease,
                execution.Executor,
                finality,
                reporter: execution.Reporter);

            var exitCode = await RunToBoundaryAsync(host, errorWriter);

            Assert.AreEqual(4, exitCode, "worker-managed-oom-exit-code");
            Assert.AreEqual(1, finality.Completed.Count, "worker-managed-oom-terminal-once");
            Assert.AreSame(execution.Request, finality.Completed[0].Request, "worker-managed-oom-request-reference");
            Assert.AreEqual(ImmichReverseGeo.Core.Models.ProcessingRunOutcome.Failed, finality.Completed[0].Result.Outcome, "worker-managed-oom-failed-terminal");
            Assert.AreEqual(managedFailure.Message, finality.Completed[0].Result.FailureMessage, "worker-managed-oom-failure-result");
            Assert.IsTrue(execution.Logger.Entries.Any(entry => ReferenceEquals(entry.Exception, managedFailure)), "worker-managed-oom-caught-by-real-executor");
            Assert.AreEqual(
                "worker-exit-summary outcome=executor-failure phase=execution message=worker executor failed" + Environment.NewLine,
                errorWriter.ToString(),
                "worker-managed-oom-safe-summary");
            Assert.IsFalse(errorWriter.ToString().Contains(managedFailure.Message, StringComparison.Ordinal), "worker-managed-oom-summary-redacted");
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    [TestMethod]
    [TestCategory("Change23")]
    public async Task Runner_RawOutOfMemoryBypassesOrderlyExitMappingOnlyAfterHostDisposal()
    {
        var lifetime = new ControllableApplicationLifetime();
        lifetime.StopApplication();
        var outcomes = new ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator();
        using var services = new ServiceCollection()
            .AddSingleton<IHostApplicationLifetime>(lifetime)
            .AddSingleton(outcomes)
            .BuildServiceProvider();
        var rawFailure = new OutOfMemoryException("controlled-raw-oom");
        var host = new FaultingHost(services, rawFailure, null);
        using var errorWriter = new StringWriter();

        var thrown = await Assert.ThrowsExactlyAsync<OutOfMemoryException>(
            () => RunToBoundaryAsync(host, errorWriter),
            "worker-raw-oom-bypasses-mapped-return");
        Assert.AreSame(rawFailure, thrown, "worker-raw-oom-reference");
        Assert.AreEqual(string.Empty, errorWriter.ToString(), "worker-raw-oom-no-summary");
        Assert.IsFalse(outcomes.HasFact, "worker-raw-oom-no-mapped-fact");
        Assert.AreEqual(1, host.DisposeAsyncCount, "worker-raw-oom-orderly-disposal-once");
    }

    [TestMethod]
    [TestCategory("Change23")]
    public async Task Runner_StartAndDisposeOutOfMemory_PreservesFirstFatalReferenceAfterCleanup()
    {
        var outcomes = new ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator();
        using var services = new ServiceCollection().AddSingleton(outcomes).BuildServiceProvider();
        var firstFatal = new OutOfMemoryException("controlled-start-oom");
        var laterFatal = new OutOfMemoryException("controlled-dispose-oom");
        var host = new FaultingHost(services, firstFatal, laterFatal);

        var thrown = await Assert.ThrowsExactlyAsync<OutOfMemoryException>(
            () => WorkerHostFactory.RunHostAsync(host, outcomes));

        Assert.AreSame(firstFatal, thrown, "worker-first-fatal-reference");
        Assert.AreEqual(1, host.DisposeAsyncCount, "worker-first-fatal-disposal-once");
        Assert.IsFalse(outcomes.HasFact, "worker-first-fatal-unmapped");
    }

    [TestMethod]
    [TestCategory("Change23")]
    public async Task Runner_ServiceResolutionAndDisposeOutOfMemory_PreservesFirstFatalReferenceAfterCleanup()
    {
        var firstFatal = new OutOfMemoryException("controlled-resolution-oom");
        var laterFatal = new OutOfMemoryException("controlled-dispose-oom");
        var services = new ThrowingServiceProvider(firstFatal);
        var host = new FaultingHost(services, new InvalidOperationException("start-must-not-run"), laterFatal);

        var thrown = await Assert.ThrowsExactlyAsync<OutOfMemoryException>(
            () => WorkerHostFactory.RunHostAsync(host));

        Assert.AreSame(firstFatal, thrown, "worker-resolution-first-fatal-reference");
        Assert.AreEqual(0, host.StartAsyncCount, "worker-resolution-fatal-no-start");
        Assert.AreEqual(1, host.DisposeAsyncCount, "worker-resolution-fatal-disposal-once");
        Assert.IsFalse(services.Outcomes.HasFact, "worker-resolution-fatal-unmapped");
    }

    [TestMethod]
    [TestCategory("Change23")]
    public async Task Runner_DisposeOutOfMemoryWithoutEarlierFatal_RethrowsDisposeReference()
    {
        var outcomes = new ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator();
        using var services = new ServiceCollection().AddSingleton(outcomes).BuildServiceProvider();
        var disposeFatal = new OutOfMemoryException("controlled-dispose-only-oom");
        var host = new FaultingHost(services, new InvalidOperationException("mapped-start-failure"), disposeFatal);

        var thrown = await Assert.ThrowsExactlyAsync<OutOfMemoryException>(
            () => WorkerHostFactory.RunHostAsync(host, outcomes));

        Assert.AreSame(disposeFatal, thrown, "worker-dispose-fatal-reference");
        Assert.AreEqual(1, host.DisposeAsyncCount, "worker-dispose-fatal-once");
        Assert.AreEqual(5, outcomes.Fact.ExitCode, "worker-earlier-orderly-fact-retained-but-not-returned");
    }

    [TestMethod]
    [TestCategory("Change23")]
    public void ProcessBoundary_MandatoryRunDelegatePreservesFatalReferenceWithoutSummary()
    {
        var fatal = new OutOfMemoryException("controlled-top-level-delegate-oom");
        var calls = 0;
        ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator? suppliedOutcomes = null;
        using var errorWriter = new StringWriter();

        var thrown = Assert.ThrowsExactly<OutOfMemoryException>(() => InternalWorkerProcess.Run(
            [],
            errorWriter,
            outcomes =>
            {
                calls++;
                suppliedOutcomes = outcomes;
                return Task.FromException<int>(fatal);
            }));

        Assert.AreSame(fatal, thrown, "worker-top-level-fatal-reference");
        Assert.AreEqual(1, calls, "worker-top-level-delegate-once");
        Assert.IsNotNull(suppliedOutcomes, "worker-top-level-accumulator-supplied");
        Assert.IsFalse(suppliedOutcomes.HasFact, "worker-top-level-fatal-unmapped");
        Assert.AreEqual(string.Empty, errorWriter.ToString(), "worker-top-level-fatal-no-summary");
    }

    [TestMethod]
    [TestCategory("Change23")]
    public void ProcessBoundary_NonOutOfMemoryDelegateFailureMapsStartupAndWritesExactSummary()
    {
        using var errorWriter = new StringWriter();

        var exitCode = InternalWorkerProcess.Run(
            [],
            errorWriter,
            _ => Task.FromException<int>(new InvalidOperationException("raw-worker-delegate-failure")));

        Assert.AreEqual(5, exitCode, "worker-top-level-non-oom-infrastructure-code");
        Assert.AreEqual(
            "worker-exit-summary outcome=infrastructure-failure phase=startup message=worker infrastructure failed" + Environment.NewLine,
            errorWriter.ToString(),
            "worker-top-level-non-oom-exact-summary");
    }

    [TestMethod]
    [TestCategory("Change23")]
    public void ProcessBoundary_MissingOutcomeMapsStartupAndWritesExactSummary()
    {
        using var errorWriter = new StringWriter();

        var exitCode = InternalWorkerProcess.Run(
            [],
            errorWriter,
            _ => Task.FromResult(0));

        Assert.AreEqual(5, exitCode, "worker-top-level-missing-fact-infrastructure-code");
        Assert.AreEqual(
            "worker-exit-summary outcome=infrastructure-failure phase=startup message=worker infrastructure failed" + Environment.NewLine,
            errorWriter.ToString(),
            "worker-top-level-missing-fact-exact-summary");
    }

    [TestMethod]
    [TestCategory("Change23")]
    public void ProcessBoundary_CompletedDelegateUsesSameAccumulatorAndWritesNoSummary()
    {
        using var errorWriter = new StringWriter();
        var ledger = new List<string>();
        ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator? supplied = null;

        var exitCode = InternalWorkerProcess.Run(
            [],
            errorWriter,
            outcomes =>
            {
                supplied = outcomes;
                ledger.Add("runner");
                outcomes.Add(ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitFact.Completed());
                return Task.FromResult(0);
            });

        Assert.AreEqual(0, exitCode, "worker-top-level-completed-code");
        Assert.IsNotNull(supplied, "worker-top-level-completed-accumulator");
        Assert.AreEqual(0, supplied.Fact.ExitCode, "worker-top-level-completed-same-accumulator-fact");
        CollectionAssert.AreEqual(new[] { "runner" }, ledger, "worker-top-level-completed-runner-before-return");
        Assert.AreEqual(string.Empty, errorWriter.ToString(), "worker-top-level-completed-no-summary");
    }

    [TestMethod]
    [TestCategory("Change23")]
    public void ProcessBoundary_CompleteInvalidInvocationWritesExactInputSummary()
    {
        using var errorWriter = new StringWriter();

        var exitCode = InternalWorkerProcess.CompleteInvalidInvocation(errorWriter);

        Assert.AreEqual(2, exitCode, "worker-top-level-invalid-invocation-code");
        Assert.AreEqual(
            "worker-exit-summary outcome=invalid-input phase=input message=worker invocation or input is invalid" + Environment.NewLine,
            errorWriter.ToString(),
            "worker-top-level-invalid-invocation-summary");
    }

    [TestMethod]
    [TestCategory("Change23")]
    public void ProcessBoundary_ResidualArgumentsRejectBeforeRunDelegateWithoutSummary()
    {
        var calls = 0;
        using var errorWriter = new StringWriter();

        Assert.ThrowsExactly<InvalidOperationException>(() => InternalWorkerProcess.Run(
            ["--unexpected-residual"],
            errorWriter,
            _ =>
            {
                calls++;
                return Task.FromResult(0);
            }));

        Assert.AreEqual(0, calls, "worker-top-level-invalid-residual-does-not-run");
        Assert.AreEqual(string.Empty, errorWriter.ToString(), "worker-top-level-invalid-residual-no-summary");
    }

    [TestMethod]
    [TestCategory("Change23")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Runner_GenericTypedTransportStartOrDisposeExceptionMapsInfrastructureFive(bool startFault)
    {
        var outcomes = new ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator();
        using var services = new ServiceCollection()
            .AddSingleton(outcomes)
            .BuildServiceProvider();
        var typed = new WorkerNdjsonTransportException(WorkerNdjsonFailureStage.Write);
        var host = new FaultingHost(
            services,
            startFault ? typed : null,
            startFault ? new InvalidOperationException("cleanup-after-start") : typed);

        var exitCode = await WorkerHostFactory.RunHostAsync(host, outcomes);

        Assert.AreEqual(5, exitCode, "worker-generic-host-typed-transport-is-infrastructure");
        Assert.AreEqual(5, outcomes.Fact.ExitCode, "worker-generic-host-typed-transport-accumulator");
    }

    [TestMethod]
    public async Task Runner_LoggerResolutionPrimaryAndDisposeFault_PreservesResolutionFailure()
    {
        var primary = new InvalidOperationException("logger-resolution-primary");
        var host = new FaultingHost(new ThrowingServiceProvider(primary), new InvalidOperationException("start-should-not-run"), new InvalidOperationException("dispose-secondary"));

        var exitCode = await WorkerHostFactory.RunHostAsync(host);

        Assert.AreEqual(5, exitCode, "worker-runner-logger-resolution-infrastructure-code");
        Assert.AreEqual(1, host.DisposeAsyncCount, "worker-runner-logger-resolution-dispose-once");
    }

    private sealed class ThrowingServiceProvider(Exception exception) : IServiceProvider
    {
        internal ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator Outcomes { get; } = new();

        public object? GetService(Type serviceType)
        {
            return serviceType == typeof(ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator)
                ? Outcomes
                : throw exception;
        }
    }

    private sealed class LoggerFactoryServiceProvider(Microsoft.Extensions.Logging.ILoggerFactory factory) : IServiceProvider
    {
        private readonly ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator _outcomes = new();

        public object? GetService(Type serviceType)
        {
            return serviceType switch
            {
                var type when type == typeof(ImmichReverseGeo.Core.WorkerProcessExitOutcomes.WorkerProcessExitOutcomeAccumulator) => _outcomes,
                var type when type == typeof(Microsoft.Extensions.Logging.ILoggerFactory) => factory,
                _ => null
            };
        }
    }

    private sealed class OutOfMemoryTextWriter(OutOfMemoryException failure) : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public int WriteLineCount { get; private set; }
        public int FlushCount { get; private set; }

        public override void WriteLine(string? value)
        {
            WriteLineCount++;
            throw failure;
        }

        public override void Flush()
        {
            FlushCount++;
            throw failure;
        }
    }

    private sealed class ThrowingCountingTextWriter : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public int WriteLineCount { get; private set; }

        public int FlushCount { get; private set; }

        public override void WriteLine(string? value)
        {
            WriteLineCount++;
            throw new IOException("stderr-unavailable");
        }

        public override void Flush()
        {
            FlushCount++;
            throw new IOException("stderr-unavailable");
        }
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

    private sealed class LateDisposalFaultStream(List<string> ledger) : MemoryStream
    {
        private int _flushCount;

        public int DisposeCount { get; private set; }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            ledger.Add($"flush-{Interlocked.Increment(ref _flushCount)}");
            return Task.CompletedTask;
        }

        public override ValueTask DisposeAsync()
        {
            DisposeCount++;
            ledger.Add("stdout-dispose");
            return ValueTask.FromException(new IOException("LATE_STDOUT_DISPOSAL_SENTINEL"));
        }
    }

    private sealed class LedgerTextWriter(List<string> ledger) : StringWriter
    {
        public override void WriteLine(string? value)
        {
            ledger.Add("stderr-summary");
            base.WriteLine(value);
        }

        public override void Flush()
        {
            ledger.Add("stderr-flush");
            base.Flush();
        }
    }

    private sealed class FaultingHost(IServiceProvider services, Exception? startFailure, Exception? disposeFailure) : IHost, IAsyncDisposable
    {
        public IServiceProvider Services { get; } = services;
        public int StartAsyncCount { get; private set; }
        public int DisposeAsyncCount { get; private set; }
        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartAsyncCount++;
            return startFailure is null ? Task.CompletedTask : Task.FromException(startFailure);
        }
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Dispose() { }
        public ValueTask DisposeAsync()
        {
            DisposeAsyncCount++;
            return disposeFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(disposeFailure);
        }
    }

    private sealed class FaultingLease(ImmichReverseGeo.Core.Models.ProcessingRunRequest request, bool settleFault, bool disposeFault) : IProcessingRunLease
    {
        public int SettleCount { get; private set; }
        public int DisposeCount { get; private set; }
        public ImmichReverseGeo.Core.Models.ProcessingRunRequest Request { get; } = request;
        public CancellationToken CancellationToken => CancellationToken.None;

        public void NotifyExecutionStarting()
        {
        }

        public ValueTask<WorkerInputPumpFinality> SettleAsync(CancellationToken cancellationToken)
        {
            SettleCount++;
            return settleFault
                ? ValueTask.FromException<WorkerInputPumpFinality>(new InvalidOperationException("settle-secondary"))
                : ValueTask.FromResult(WorkerInputPumpFinality.ExpectedShutdown());
        }
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return disposeFault ? ValueTask.FromException(new InvalidOperationException("dispose-secondary")) : ValueTask.CompletedTask;
        }
    }

    private sealed class RawOutOfMemoryLease(
        ImmichReverseGeo.Core.Models.ProcessingRunRequest request,
        string boundary,
        OutOfMemoryException failure) : IProcessingRunLease
    {
        public int SettleCount { get; private set; }
        public int DisposeCount { get; private set; }
        public ImmichReverseGeo.Core.Models.ProcessingRunRequest Request { get; } = request;
        public CancellationToken CancellationToken => CancellationToken.None;

        public void NotifyExecutionStarting()
        {
        }

        public ValueTask<WorkerInputPumpFinality> SettleAsync(CancellationToken cancellationToken)
        {
            SettleCount++;
            return boundary == "settle"
                ? ValueTask.FromException<WorkerInputPumpFinality>(failure)
                : ValueTask.FromResult(WorkerInputPumpFinality.ExpectedShutdown());
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return boundary == "dispose" ? ValueTask.FromException(failure) : ValueTask.CompletedTask;
        }
    }

    private sealed class MultipleRawOutOfMemoryLease(
        ImmichReverseGeo.Core.Models.ProcessingRunRequest request,
        OutOfMemoryException settleFailure,
        OutOfMemoryException disposeFailure) : IProcessingRunLease
    {
        public int SettleCount { get; private set; }
        public int DisposeCount { get; private set; }
        public ImmichReverseGeo.Core.Models.ProcessingRunRequest Request { get; } = request;
        public CancellationToken CancellationToken => CancellationToken.None;

        public void NotifyExecutionStarting()
        {
        }

        public ValueTask<WorkerInputPumpFinality> SettleAsync(CancellationToken cancellationToken)
        {
            SettleCount++;
            return ValueTask.FromException<WorkerInputPumpFinality>(settleFailure);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.FromException(disposeFailure);
        }
    }

    private sealed class TransportThrowingAcceptedFinality(bool completionHook) : IWorkerAcceptedRunFinality
    {
        public int CompleteCount { get; private set; }

        public int FailCount { get; private set; }

        public Task CompleteAsync(
            ImmichReverseGeo.Core.Models.ProcessingRunRequest request,
            ImmichReverseGeo.Core.Models.ProcessingRunResult result,
            CancellationToken cancellationToken)
        {
            CompleteCount++;
            return completionHook
                ? Task.FromException(new WorkerNdjsonTransportException(WorkerNdjsonFailureStage.Write))
                : Task.CompletedTask;
        }

        public Task FailAsync(
            ImmichReverseGeo.Core.Models.ProcessingRunRequest request,
            WorkerSafeFailure failure,
            CancellationToken cancellationToken)
        {
            FailCount++;
            return completionHook
                ? Task.CompletedTask
                : Task.FromException(new WorkerNdjsonTransportException(WorkerNdjsonFailureStage.Write));
        }
    }

    private sealed class OutOfMemoryAcceptedFinality(OutOfMemoryException failure, bool completionPath) : IWorkerAcceptedRunFinality
    {
        public int CompleteCount { get; private set; }
        public int FailCount { get; private set; }

        public Task CompleteAsync(
            ImmichReverseGeo.Core.Models.ProcessingRunRequest request,
            ImmichReverseGeo.Core.Models.ProcessingRunResult result,
            CancellationToken cancellationToken)
        {
            CompleteCount++;
            return completionPath ? Task.FromException(failure) : Task.CompletedTask;
        }

        public Task FailAsync(
            ImmichReverseGeo.Core.Models.ProcessingRunRequest request,
            WorkerSafeFailure safeFailure,
            CancellationToken cancellationToken)
        {
            FailCount++;
            return completionPath ? Task.CompletedTask : Task.FromException(failure);
        }
    }

    private sealed class FaultingAcceptedFinality(bool failureFault, Exception? completionFault) : IWorkerAcceptedRunFinality
    {
        public int CompleteCount { get; private set; }
        public int FailCount { get; private set; }
        public Task CompleteAsync(ImmichReverseGeo.Core.Models.ProcessingRunRequest request, ImmichReverseGeo.Core.Models.ProcessingRunResult result, CancellationToken cancellationToken)
        {
            CompleteCount++;
            return completionFault is null
                ? Task.CompletedTask
                : Task.FromException(completionFault);
        }
        public Task FailAsync(ImmichReverseGeo.Core.Models.ProcessingRunRequest request, WorkerSafeFailure failure, CancellationToken cancellationToken)
        {
            FailCount++;
            return failureFault
                ? Task.FromException(new InvalidOperationException("failure-finality-secondary"))
                : Task.CompletedTask;
        }
    }

    private sealed class OutOfMemoryThrowingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => throw new OutOfMemoryException("controlled-boundary-logger-oom");
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

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
        }
    }

    private sealed class FlushAwareAcceptedAcquirer(CapturingOutputStream output, IProcessingRunLease lease) : IInitialProcessingRunAcquirer
    {
        public int CallCount { get; private set; }

        public Task<InitialProcessingRunAcquisition> AcquireAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            Assert.AreEqual(1, output.FlushCount, "worker-production-ready-flushed-before-acquisition");
            return Task.FromResult<InitialProcessingRunAcquisition>(InitialProcessingRunAcquisition.Accept(lease));
        }
    }

    private sealed class CapturingOutputStreamFactory : IWorkerNdjsonOutputStreamFactory
    {
        public CapturingOutputStream Output { get; } = new();

        public Stream OpenStandardOutput()
        {
            return Output;
        }
    }

    private sealed class CapturingOutputStream : MemoryStream
    {
        private int _flushCount;

        public int FlushCount => _flushCount;

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _flushCount);
            return Task.CompletedTask;
        }
    }

    private sealed class FaultingOutputStreamFactory : IWorkerNdjsonOutputStreamFactory
    {
        public FaultingOutputStreamFactory(string failureMode)
        {
            Output = new FaultingOutputStream(failureMode, Ledger);
        }

        public int OpenCount { get; private set; }

        public List<string> Ledger { get; } = [];

        public FaultingOutputStream Output { get; }

        public Stream OpenStandardOutput()
        {
            OpenCount++;
            Ledger.Add("open");
            return Output;
        }
    }

    private sealed class FaultingOutputStream(string failureMode, List<string> ledger) : MemoryStream
    {
        public int WriteCount { get; private set; }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteCount++;
            ledger.Add("write");
            if (failureMode == "partial-write")
            {
                base.Write(buffer.Span[..Math.Max(1, buffer.Length / 2)]);
            }

            if (failureMode is "write" or "partial-write" or "broken-pipe")
            {
                return ValueTask.FromException(new IOException(failureMode));
            }

            return base.WriteAsync(buffer, cancellationToken);
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            ledger.Add("flush");
            return failureMode == "flush"
                ? Task.FromException(new IOException(failureMode))
                : Task.CompletedTask;
        }
    }

    private sealed class TerminalFlushFailingOutputStreamFactory : IWorkerNdjsonOutputStreamFactory
    {
        public TerminalFlushFailingOutputStream Output { get; } = new();

        public Stream OpenStandardOutput()
        {
            return Output;
        }
    }

    private sealed class TerminalFlushFailingOutputStream : MemoryStream
    {
        public int WriteCount { get; private set; }

        public int FlushCount { get; private set; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteCount++;
            base.Write(buffer, offset, count);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteCount++;
            return base.WriteAsync(buffer, cancellationToken);
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCount++;
            return FlushCount == 4
                ? Task.FromException(new IOException("terminal-flush"))
                : Task.CompletedTask;
        }
    }

    private sealed class CountingOutputStreamFactory : IWorkerNdjsonOutputStreamFactory
    {
        public int OpenCount { get; private set; }

        public Stream OpenStandardOutput()
        {
            OpenCount++;
            return new MemoryStream();
        }
    }

    private sealed class ThrowingCountingOutputStreamFactory : IWorkerNdjsonOutputStreamFactory
    {
        public int OpenCount { get; private set; }

        public Stream OpenStandardOutput()
        {
            OpenCount++;
            throw new IOException("OPEN_SECRET_SENTINEL");
        }
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
