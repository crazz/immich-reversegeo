using System.Collections.Concurrent;
using System.Reflection;
using ImmichReverseGeo.Core.ApplicationRole;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using ImmichReverseGeo.Gadm.Services;
using ImmichReverseGeo.Overture.Services;
using ImmichReverseGeo.Web.ApplicationRole;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Composition;
using ImmichReverseGeo.Web.ProcessingRunLocking;
using ImmichReverseGeo.Web.RunOnce;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerHost;
using ImmichReverseGeo.Web.WorkerHost.WorkerNdjsonOutput;
using ImmichReverseGeo.Web.WorkerHost.WorkerStdinRequestLoop;
using ImmichReverseGeo.Web.WorkerCommandInvocation;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Data.Sqlite;

namespace ImmichReverseGeo.Tests.ApplicationComposition;

[TestClass]
[TestCategory("Change45")]
public sealed class DeploymentModeCompositionMatrixTests
{
    [TestMethod]
    [TestCategory("Change55")]
    [TestCategory("Change56")]
    public async Task WebModes_ActualStartupDoesNotMaterializeDatabaseInventoryOrGeodata()
    {
        foreach (DeploymentMode mode in new[] { DeploymentMode.Standard, DeploymentMode.WebOnly })
        {
            var activations = new BoundaryRuntimeSentinel(mode == DeploymentMode.Standard ? BoundaryRole.Standard : BoundaryRole.WebOnly);
            object Forbidden(string category)
            {
                return activations.Forbid<object>("production startup", category);
            }
            var process = new NoLaunchChildProcessFactory(mode == DeploymentMode.Standard ? BoundaryRole.Standard : BoundaryRole.WebOnly);
            await using var fixture = WebFixture.Create(mode, validRuntimeFiles: true,
                sentinelExternal: true, childProcessBoundary: process,
                configureServices: services =>
                {
                    services.RemoveAll<Npgsql.NpgsqlDataSource>();
                    services.AddSingleton<Npgsql.NpgsqlDataSource>(_ => (Npgsql.NpgsqlDataSource)Forbidden("PostgreSQL"));
                    services.RemoveAll<ICacheInventoryStorageScanner>();
                    services.AddSingleton<ICacheInventoryStorageScanner>(_ => (ICacheInventoryStorageScanner)Forbidden("inventory"));
                    foreach (Type type in new[] { typeof(OvertureDivisionsService), typeof(OvertureDivisionCacheService),
                        typeof(OverturePlacesService), typeof(GadmDivisionsService), typeof(GadmDivisionCacheService) })
                    {
                        services.AddSingleton(type, _ => Forbidden(type.Name));
                    }
                });
            await fixture.Application.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(1, fixture.ListenerStarts, mode + "-actual-host-ready");
            Assert.IsEmpty(activations.Events, mode + "-no-eager-activation");
            Assert.AreEqual(0, fixture.ForbiddenResolutions, mode + "-no-executor");
            Assert.AreEqual(0, process.StartCalls, mode + "-no-child");
            await fixture.Application.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    [TestCategory("Change55")]
    [TestCategory("Change56")]
    public async Task WebModes_EveryComponentInjectionGraphResolvesWithoutHeavyOrChildWork()
    {
        foreach (DeploymentMode mode in new[] { DeploymentMode.Standard, DeploymentMode.WebOnly })
        {
            var process = new NoLaunchChildProcessFactory(mode == DeploymentMode.Standard ? BoundaryRole.Standard : BoundaryRole.WebOnly);
            await using var fixture = WebFixture.Create(mode, validRuntimeFiles: true,
                sentinelExternal: true, childProcessBoundary: process);
            await using var scope = fixture.Provider.CreateAsyncScope();
            Type[] components = typeof(StandardWebApplication).Assembly.GetTypes()
                .Where(type => !type.IsAbstract && typeof(Microsoft.AspNetCore.Components.IComponent).IsAssignableFrom(type)).ToArray();
            foreach (Type type in components)
            {
                object component = ActivatorUtilities.CreateInstance(scope.ServiceProvider, type);
                foreach (PropertyInfo property in WebBoundaryInspection.Injections(type))
                {
                    if (property.IsDefined(typeof(Microsoft.AspNetCore.Components.InjectAttribute), inherit: true))
                    {
                        property.SetValue(component, scope.ServiceProvider.GetRequiredService(property.PropertyType));
                    }
                }
            }
            Assert.IsGreaterThanOrEqualTo(10, components.Length, mode + "-all-compiled-components");
            Assert.AreEqual(0, fixture.ForbiddenResolutions, mode + "-no-heavy-construction");
            Assert.AreEqual(0, process.StartCalls, mode + "-no-child");
            Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Root, "data", "overture-divisions")));
            Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Root, "data", "gadm-divisions")));
        }
    }
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    [TestCategory("Change56")]
    public async Task WebOnlyRuntimeSentinel_ReportsTheActualProductionRole()
    {
        var process = new NoLaunchChildProcessFactory(BoundaryRole.WebOnly);
        await using var fixture = WebFixture.Create(DeploymentMode.WebOnly, sentinelExternal: true, childProcessBoundary: process);
        AssertFailedException failure = Assert.ThrowsExactly<AssertFailedException>(() => fixture.Provider.GetRequiredService<IProcessingRunExecutor>());
        StringAssert.Contains(failure.Message, "[WebOnly]", "The forbidden DI activation must name the actual role.");
        Assert.AreEqual(1, fixture.ForbiddenResolutions);
        AssertFailedException launch = Assert.ThrowsExactly<AssertFailedException>(() =>
        {
            _ = fixture.Provider.GetRequiredService<IChildProcessFactory>().StartAsync(null!, CancellationToken.None);
        });
        StringAssert.Contains(launch.Message, "[WebOnly]");
        StringAssert.Contains(launch.Message, "worker session");
        StringAssert.Contains(launch.Message, "order 1 -> count 1");
        Assert.AreEqual(1, process.StartCalls, "The actual substituted launcher boundary produced the failure before any process work.");
    }

    [TestMethod]
    [TestCategory("Change56")]
    public async Task WebModes_ProductionControllersRejectOrAdmitOnlyFakeSessionsWithoutLocalFallback()
    {
        foreach (DeploymentMode mode in new[] { DeploymentMode.Standard, DeploymentMode.WebOnly })
        {
            var events = new BoundaryRuntimeSentinel(mode == DeploymentMode.Standard ? BoundaryRole.Standard : BoundaryRole.WebOnly);
            var sessions = new BoundaryWorkerSessions(events);
            var process = new NoLaunchChildProcessFactory(mode == DeploymentMode.Standard ? BoundaryRole.Standard : BoundaryRole.WebOnly);
            await using var fixture = WebFixture.Create(mode, validRuntimeFiles: true, sentinelExternal: true,
                childProcessBoundary: process, configureServices: services =>
                {
                    services.RemoveAll<ICoordinateLookupWorkerClient>();
                    services.AddSingleton<ICoordinateLookupWorkerClient>(sessions);
                    services.RemoveAll<ICacheMutationWorkerClient>();
                    services.AddSingleton<ICacheMutationWorkerClient>(sessions);
                });
            await using var lookup = fixture.Provider.GetRequiredService<CoordinateLookupPageControllerFactory>().Create(() => { });
            await using var cache = fixture.Provider.GetRequiredService<CacheMutationPageControllerFactory>().Create(() => Task.CompletedTask, () => Task.CompletedTask);
            var request = new CoordinateLookupSubmission(47.4, 8.5, false, false, false);
            await lookup.SubmitAsync(request with { Latitude = double.NaN });
            Assert.ThrowsExactly<ArgumentException>(() => { _ = cache.RefreshAsync(CacheMutationSource.Overture, "bad-code"); });
            Assert.AreEqual(0, sessions.Sessions, mode + " invalid before admission");

            sessions.Unavailable = true;
            await lookup.SubmitAsync(request);
            await cache.RefreshAsync(CacheMutationSource.Overture, "CHE");
            Assert.AreEqual(0, sessions.Sessions, mode + " unavailable creates no session");
            Assert.IsNull(fixture.Provider.GetRequiredService<IWorkerJobArbitrationDiagnostics>().Snapshot.ActiveOwner, "unavailable releases admission");

            sessions.Unavailable = false;
            sessions.HoldLookup = true;
            Task activeLookup = lookup.SubmitAsync(request);
            try
            {
                Assert.IsNotNull(sessions.Lookup, "fake session exists before testing the busy branch");
                Assert.AreEqual(1, sessions.Sessions);
                Assert.IsNotNull(fixture.Provider.GetRequiredService<IWorkerJobArbitrationDiagnostics>().Snapshot.ActiveOwner);
                await cache.RefreshAsync(CacheMutationSource.Gadm, "CHE");
                Assert.AreEqual(ProcessingRunAdmissionResult.AlreadyRunning, await fixture.Provider.GetRequiredService<IManualProcessingRunCoordinator>().TriggerManualAsync());
                if (mode == DeploymentMode.Standard)
                {
                    await fixture.Provider.GetRequiredService<IScheduledRunTrigger>().TriggerScheduledAsync(CancellationToken.None);
                }
                Assert.AreEqual(1, sessions.Sessions, "busy operations add zero sessions");
                Assert.AreEqual(0, fixture.Children.Count);
            }
            finally
            {
                sessions.Lookup?.Finish();
                await activeLookup.WaitAsync(TimeSpan.FromSeconds(5));
            }
            await cache.RefreshAsync(CacheMutationSource.Overture, "CHE");
            Assert.AreEqual(2, sessions.Sessions, "one admitted Lookup and one admitted cache session");
            Assert.AreEqual(2, sessions.Disposals, "both exact sessions disposed");
            Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await fixture.Provider.GetRequiredService<IManualProcessingRunCoordinator>().TriggerManualAsync());
            Assert.AreEqual(1, fixture.Children.Count, "manual delegates exactly once");
            if (mode == DeploymentMode.Standard)
            {
                fixture.Gate!.Result = false;
                await fixture.Provider.GetRequiredService<IScheduledRunTrigger>().TriggerScheduledAsync(CancellationToken.None);
                Assert.AreEqual(1, fixture.Children.Count, "empty schedule adds zero sessions");
                fixture.Gate.Result = true;
                await fixture.Provider.GetRequiredService<IScheduledRunTrigger>().TriggerScheduledAsync(CancellationToken.None);
                Assert.AreEqual(2, fixture.Children.Count, "positive schedule delegates exactly once");
            }
            Assert.AreEqual(0, fixture.ForbiddenResolutions, "no local execution, geodata, or external database fallback");
            Assert.AreEqual(0, process.StartCalls, "no real process boundary reached");
            CollectionAssert.AreEqual(new[] { "Lookup admission", "cache admission" }, events.Events.Select(e => e.Owner).ToArray());
        }
    }

    [TestMethod]
    public void StartupSelection_MissingAndExactPublicModesUseOneImmutableReadAndExpectedContinuation()
    {
        foreach ((string name, string? value, DeploymentMode expected) in new[]
        {
            ("missing", null, DeploymentMode.Standard),
            ("standard", "standard", DeploymentMode.Standard),
            ("web-only", "web-only", DeploymentMode.WebOnly),
            ("run-once", "run-once", DeploymentMode.RunOnce)
        })
        {
            var mode = new ModeSource(value);
            DeploymentMode? web = null;
            DeploymentMode? runOnce = null;
            var privateCalls = 0;

            ApplicationRoleStartup.Begin(
                [],
                mode.Read,
                TextWriter.Null,
                (selected, _) => web = selected,
                _ => privateCalls++,
                (selected, _) => runOnce = selected,
                _ => Assert.Fail("private-exit-" + name),
                _ => Assert.Fail("mode-exit-" + name));

            Assert.AreEqual(1, mode.Reads, name + "-read-once");
            Assert.AreEqual(0, privateCalls, name + "-not-private");
            if (ReferenceEquals(expected, DeploymentMode.RunOnce))
            {
                Assert.AreSame(expected, runOnce, name + "-run-once");
                Assert.IsNull(web, name + "-no-web");
            }
            else
            {
                Assert.AreSame(expected, web, name + "-web");
                Assert.IsNull(runOnce, name + "-no-run-once");
            }
        }
    }

    [TestMethod]
    public void StartupSelection_InvalidPublicAndPrivatePrecedenceFailBeforeContinuations()
    {
        foreach (string value in new[] { "", " ", " standard", "STANDARD", "unknown" })
        {
            var mode = new ModeSource(value);
            var sideEffects = new PreHostSideEffectProbe();
            var exit = -1;
            using var diagnostic = new StringWriter();
            ApplicationRoleStartup.Begin(
                [], mode.Read, diagnostic,
                (_, _) => sideEffects.Enter("web"),
                _ => sideEffects.Enter("private"),
                (_, _) => sideEffects.Enter("run-once"),
                _ => Assert.Fail("invalid-private-exit"),
                code => exit = code);
            Assert.AreEqual(1, mode.Reads, value + "-one-read");
            Assert.AreEqual(2, exit, value + "-exit");
            StringAssert.Contains(diagnostic.ToString(), "IMMICH_REVERSEGEO_MODE", value + "-diagnostic");
            sideEffects.AssertUntouched(value);
        }

        foreach (string? publicMode in new string?[] { null, "standard", "web-only", "run-once", "not-a-public-mode" })
        {
            var privateMode = new ModeSource(publicMode);
            var privateCalls = 0;
            ApplicationRoleStartup.Begin(
                ["--internal-worker"], privateMode.Read, TextWriter.Null,
                (_, _) => Assert.Fail("private-web"),
                _ => privateCalls++,
                (_, _) => Assert.Fail("private-run-once"),
                _ => Assert.Fail("private-invalid"),
                _ => Assert.Fail("private-mode-exit"));
            Assert.AreEqual(0, privateMode.Reads, "private-bypasses-mode-" + (publicMode ?? "missing"));
            Assert.AreEqual(1, privateCalls, "private-continuation-" + (publicMode ?? "missing"));
        }

        foreach (string[] arguments in new[]
        {
            new[] { "--internal-worker", "unexpected" },
            new[] { "--internal-worker", "--internal-worker" },
            new[] { "--internal-worker=1" },
            new[] { "unexpected", "--internal-worker" }
        })
        {
            var invalid = new ModeSource("not-a-public-mode");
            var sideEffects = new PreHostSideEffectProbe();
            var privateExit = -1;
            ApplicationRoleStartup.Begin(
                arguments, invalid.Read, TextWriter.Null,
                (_, _) => sideEffects.Enter("web"),
                _ => sideEffects.Enter("private"),
                (_, _) => sideEffects.Enter("run-once"),
                code => privateExit = code,
                _ => Assert.Fail("combined-mode-exit"));
            Assert.AreEqual(0, invalid.Reads, "private-failure-before-mode-read-" + string.Join('-', arguments));
            Assert.AreEqual(2, privateExit, "private-failure-exit-" + string.Join('-', arguments));
            sideEffects.AssertUntouched("private-failure-" + string.Join('-', arguments));
        }
    }

    [TestMethod]
    [TestCategory("Change52")]
    public async Task Composition_ProductionDescriptorsAndAliasFactoriesMatchEveryRoot()
    {
        await using var standard = WebFixture.Create(DeploymentMode.Standard);
        await using var webOnly = WebFixture.Create(DeploymentMode.WebOnly);
        AssertWebDescriptors(standard.Descriptors, scheduler: true);
        AssertWebDescriptors(webOnly.Descriptors, scheduler: false);
        AssertWebProviderAliases(standard.Provider, scheduler: true);
        AssertWebProviderAliases(webOnly.Provider, scheduler: false);
        AssertMappedWebEndpoints(standard.Application, "standard");
        AssertMappedWebEndpoints(webOnly.Application, "web-only");

        await using var runOnce = RunOnceFixture.Create();
        AssertNonWebDescriptors(
            runOnce.Descriptors,
            privateProtocol: false,
            typeof(ProcessingRunExecutor),
            typeof(IProcessingRunExecutor),
            typeof(RunOnceProcessingRunConfiguration),
            typeof(RunOnceProcessingAssetRepository),
            typeof(RunOnceProcessingSkippedStore));
        AssertSingletonAliasDescriptors<ProcessingRunExecutor, IProcessingRunExecutor>(runOnce.Descriptors);
        AssertSingletonAliasDescriptors<AdministrativeAreaResolverService, IProcessingAdministrativeResolver>(runOnce.Descriptors);
        AssertSingletonAliasDescriptors<ProcessingInfrastructureLookup, IProcessingInfrastructureLookup>(runOnce.Descriptors);
        AssertSingletonAliasDescriptors<ProcessingRunDelay, IProcessingRunDelay>(runOnce.Descriptors);
        AssertSingletonAliasDescriptors<RunOnceProcessingRunConfiguration, IProcessingRunConfiguration>(runOnce.Descriptors);
        AssertSingletonAliasDescriptors<RunOnceProcessingAssetRepository, IProcessingAssetRepository>(runOnce.Descriptors);
        AssertSingletonAliasDescriptors<RunOnceProcessingSkippedStore, IProcessingSkippedStore>(runOnce.Descriptors);
        AssertSingletonAliasDescriptors<PostgresqlProcessingRunLock, IProcessingRunLock>(runOnce.Descriptors);
        AssertSingletonAliasDescriptors<SkippedAssetsWorkerStartupInitializer, IWorkerStartupInitializer>(runOnce.Descriptors);
        AssertSingletonAliasDescriptors<RunOnceProcessingEventReporter, IProcessingEventReporter>(runOnce.Descriptors);
        AssertAlias<ProcessingRunExecutor, IProcessingRunExecutor>(runOnce.Provider);
        AssertAlias<AdministrativeAreaResolverService, IProcessingAdministrativeResolver>(runOnce.Provider);
        AssertAlias<ProcessingInfrastructureLookup, IProcessingInfrastructureLookup>(runOnce.Provider);
        AssertAlias<ProcessingRunDelay, IProcessingRunDelay>(runOnce.Provider);
        AssertAlias<RunOnceProcessingRunConfiguration, IProcessingRunConfiguration>(runOnce.Provider);
        AssertAlias<RunOnceProcessingAssetRepository, IProcessingAssetRepository>(runOnce.Provider);
        AssertAlias<RunOnceProcessingSkippedStore, IProcessingSkippedStore>(runOnce.Provider);
        AssertAlias<PostgresqlProcessingRunLock, IProcessingRunLock>(runOnce.Provider);
        AssertAlias<SkippedAssetsWorkerStartupInitializer, IWorkerStartupInitializer>(runOnce.Provider);
        AssertAlias<RunOnceProcessingEventReporter, IProcessingEventReporter>(runOnce.Provider);

        await using var worker = WorkerFixture.Create();
        AssertNonWebDescriptors(
            worker.Descriptors,
            privateProtocol: true,
            typeof(InternalWorkerLifecycleService),
            typeof(WorkerStdinRequestSource),
            typeof(IInitialProcessingRunAcquirer),
            typeof(WorkerNdjsonEmitter),
            typeof(IWorkerReadinessPublisher));
        AssertSingletonAliasDescriptors<ProcessingRunExecutor, IProcessingRunExecutor>(worker.Descriptors);
        AssertSingletonAliasDescriptors<AdministrativeAreaResolverService, IProcessingAdministrativeResolver>(worker.Descriptors);
        AssertSingletonAliasDescriptors<ProcessingInfrastructureLookup, IProcessingInfrastructureLookup>(worker.Descriptors);
        AssertSingletonAliasDescriptors<ProcessingRunDelay, IProcessingRunDelay>(worker.Descriptors);
        AssertSingletonAliasDescriptors<PostgresqlProcessingRunLock, IProcessingRunLock>(worker.Descriptors);
        AssertSingletonAliasDescriptors<SkippedAssetsWorkerStartupInitializer, IWorkerStartupInitializer>(worker.Descriptors);
        AssertSingletonAliasDescriptors<WorkerStdinTransportConfigured, IWorkerTransportAvailability>(worker.Descriptors);
        AssertSingletonAliasDescriptors<WorkerStdinRequestSource, IInitialProcessingRunAcquirer>(worker.Descriptors);
        AssertSingletonAliasDescriptors<WorkerStdinAcceptedRunFinality, IWorkerAcceptedRunFinality>(worker.Descriptors);
        AssertSingletonAliasDescriptors<TransitionalWorkerPreRequestFinality, IWorkerPreRequestFinality>(worker.Descriptors);
        AssertSingletonAliasDescriptors<WorkerNdjsonEmitter, IWorkerReadinessPublisher>(worker.Descriptors);
        AssertSingletonAliasDescriptors<WorkerNdjsonProcessingEventReporter, IProcessingEventReporter>(worker.Descriptors);
        AssertAlias<ProcessingRunExecutor, IProcessingRunExecutor>(worker.Provider);
        AssertAlias<AdministrativeAreaResolverService, IProcessingAdministrativeResolver>(worker.Provider);
        AssertAlias<ProcessingInfrastructureLookup, IProcessingInfrastructureLookup>(worker.Provider);
        AssertAlias<ProcessingRunDelay, IProcessingRunDelay>(worker.Provider);
        AssertAlias<PostgresqlProcessingRunLock, IProcessingRunLock>(worker.Provider);
        AssertAlias<SkippedAssetsWorkerStartupInitializer, IWorkerStartupInitializer>(worker.Provider);
        AssertAlias<WorkerStdinTransportConfigured, IWorkerTransportAvailability>(worker.Provider);
        AssertAlias<WorkerStdinRequestSource, IInitialProcessingRunAcquirer>(worker.Provider);
        AssertAlias<WorkerStdinAcceptedRunFinality, IWorkerAcceptedRunFinality>(worker.Provider);
        AssertAlias<TransitionalWorkerPreRequestFinality, IWorkerPreRequestFinality>(worker.Provider);
        AssertAlias<WorkerNdjsonEmitter, IWorkerReadinessPublisher>(worker.Provider);
        AssertAlias<WorkerNdjsonProcessingEventReporter, IProcessingEventReporter>(worker.Provider);
        await worker.Provider.GetRequiredService<IWorkerReadinessPublisher>().PublishAsync(CancellationToken.None);
        AssertHostedAlias<InternalWorkerLifecycleService>(worker.Provider);
    }

    [TestMethod]
    [TestCategory("Change53")]
    public async Task WebModes_ResolveOneLazyInventoryWithoutHeavyOrStorageSideEffects()
    {
        foreach (DeploymentMode mode in new[] { DeploymentMode.Standard, DeploymentMode.WebOnly })
        {
            await using var fixture = WebFixture.Create(
                mode,
                validRuntimeFiles: true,
                sentinelExternal: true);

            ICacheInventory inventory = fixture.Provider.GetRequiredService<ICacheInventory>();
            ICacheInventoryInvalidator invalidator =
                fixture.Provider.GetRequiredService<ICacheInventoryInvalidator>();

            Assert.IsTrue(ReferenceEquals(inventory, invalidator),
                mode + "-one-inventory-singleton");
            Assert.AreEqual(0, fixture.ForbiddenResolutions,
                mode + "-inventory-resolution-keeps-heavy-leaves-lazy");
            Assert.IsFalse(Directory.Exists(Path.Combine(
                    fixture.Root,
                    "data",
                    "overture-divisions")),
                mode + "-inventory-resolution-no-source-directory-create");
            Assert.IsFalse(Directory.Exists(Path.Combine(
                    fixture.Root,
                    "data",
                    "gadm-divisions")),
                mode + "-inventory-resolution-no-source-directory-create");
        }
    }

    [TestMethod]
    [TestCategory("Change53")]
    [TestCategory("Change56")]
    public async Task WebModePages_RenderThroughProductionInventoryWithoutHeavyOrWorkerWork()
    {
        await using (var webOnly = WebFixture.Create(
            DeploymentMode.WebOnly,
            validRuntimeFiles: true,
            sentinelExternal: true))
        {
            CreateMinimalInventoryDatabase(webOnly.Root, "CHE", "web-only-release");
            var page = new ImmichReverseGeo.Web.Components.Pages.Data();
            SetInjected(page, "CacheInventory",
                webOnly.Provider.GetRequiredService<ICacheInventory>());
            SetInjected(page, "Skipped",
                webOnly.Provider.GetRequiredService<SkippedAssetsRepository>());
            await using var renderer = new WebStatusRenderingTests.ComponentRenderer();

            await renderer.AttachAsync(page);
            WebStatusRenderingTests.RenderSnapshot rendered = await renderer.ReadAsync();

            StringAssert.Contains(rendered.Text, "1 Overture cache(s), 0 GADM cache(s)");
            Assert.AreEqual(0, webOnly.ForbiddenResolutions,
                "web-only-data-does-not-resolve-heavy-cache-services");
            Assert.AreEqual(0, webOnly.Children.Count,
                "web-only-data-does-not-launch-processing-child");
        }

        await using (var standard = WebFixture.Create(
            DeploymentMode.Standard,
            validRuntimeFiles: true,
            sentinelExternal: true,
            childProcessBoundary: new NoLaunchChildProcessFactory(BoundaryRole.Standard)))
        {
            CreateMinimalInventoryDatabase(standard.Root, "CHE", "standard-release");
            var page = new ImmichReverseGeo.Web.Components.Pages.GeoBoundaries();
            SetInjected(page, "CacheInventory",
                standard.Provider.GetRequiredService<ICacheInventory>());
            SetInjected(page, "CacheMutations",
                standard.Provider.GetRequiredService<CacheMutationPageControllerFactory>());
            SetInjected(page, "CacheDeletions",
                standard.Provider.GetRequiredService<CacheInventoryDeletionPageControllerFactory>());
            await using var renderer = new WebStatusRenderingTests.ComponentRenderer();

            await renderer.AttachAsync(page);
            WebStatusRenderingTests.RenderSnapshot rendered = await renderer.ReadAsync();

            StringAssert.Contains(rendered.Text, "CHE");
            StringAssert.Contains(rendered.Text, "Available");
            StringAssert.Contains(rendered.Text, "standard-release");
            Assert.AreEqual(0, standard.ForbiddenResolutions,
                "standard-geoboundaries-does-not-resolve-heavy-cache-services");
            Assert.AreEqual(0, standard.Children.Count,
                "standard-geoboundaries-does-not-launch-processing-child");
            Assert.AreEqual(0,
                standard.Provider.GetRequiredService<IChildProcessFactory>() is
                    NoLaunchChildProcessFactory boundary
                        ? boundary.StartCalls
                        : -1,
                "standard-geoboundaries-does-not-launch-cache-worker");
            await page.DisposeAsync();
        }
    }

    [TestMethod]
    [TestCategory("Change51")]
    public async Task Composition_WebRootsRetainLightweightCacheControlsWithoutWorkerOwners()
    {
        await using var standard = WebFixture.Create(DeploymentMode.Standard);
        await using var webOnly = WebFixture.Create(DeploymentMode.WebOnly);

        foreach ((string Name, IReadOnlyList<ServiceDescriptor> Descriptors) root in new[]
        {
            ("standard", standard.Descriptors),
            ("web-only", webOnly.Descriptors)
        })
        {
            Assert.IsFalse(
                root.Descriptors.Any(descriptor => descriptor.ServiceType == typeof(OvertureDivisionCacheService)),
                root.Name + "-no-overture-owner");
            Assert.IsFalse(
                root.Descriptors.Any(descriptor => descriptor.ServiceType == typeof(GadmDivisionCacheService)),
                root.Name + "-no-gadm-owner");
            Assert.IsTrue(root.Descriptors.Any(descriptor => descriptor.ServiceType == typeof(ICacheInventory)),
                root.Name + "-lightweight-inventory");
            Assert.IsTrue(root.Descriptors.Any(descriptor => descriptor.ServiceType == typeof(CacheDeletionCommand)),
                root.Name + "-lightweight-deletion");
            Assert.IsFalse(
                root.Descriptors.Any(descriptor => descriptor.ServiceType == typeof(ICacheMutationSourceOperation)),
                root.Name + "-no-worker-source-operation");
            Assert.IsFalse(
                root.Descriptors.Any(descriptor => descriptor.ServiceType == typeof(IWorkerCacheMutationOperation)),
                root.Name + "-no-worker-mutation-operation");
        }
    }

    [TestMethod]
    public async Task TriggerMatrix_StandardAndWebOnlyKeepProcessingAtTheChildBoundary()
    {
        await using var standard = WebFixture.Create(DeploymentMode.Standard, scheduledWork: false);
        var manual = standard.Provider.GetRequiredService<IManualProcessingRunCoordinator>();
        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await manual.TriggerManualAsync());
        Assert.AreEqual(1, standard.Children.Count, "standard-manual-child");
        Assert.AreEqual(0, standard.Gate!.Calls, "standard-manual-detector");

        var scheduled = standard.Provider.GetRequiredService<IScheduledRunTrigger>();
        await scheduled.TriggerScheduledAsync(CancellationToken.None);
        Assert.AreEqual(1, standard.Children.Count, "standard-empty-no-child");
        Assert.AreEqual(1, standard.Gate.Calls, "standard-empty-detector");
        standard.Gate.Result = true;
        await scheduled.TriggerScheduledAsync(CancellationToken.None);
        Assert.AreEqual(2, standard.Children.Count, "standard-positive-child");
        Assert.AreEqual(2, standard.Gate.Calls, "standard-positive-detector");

        await using var webOnly = WebFixture.Create(DeploymentMode.WebOnly);
        Assert.IsNull(webOnly.Provider.GetService<ProcessingBackgroundService>(), "web-only-no-scheduler");
        Assert.IsNull(webOnly.Provider.GetService<IScheduledRunTrigger>(), "web-only-no-trigger");
        var webManual = webOnly.Provider.GetRequiredService<IManualProcessingRunCoordinator>();
        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await webManual.TriggerManualAsync());
        Assert.AreEqual(1, webOnly.Children.Count, "web-only-manual-child");
        Assert.IsNull(webOnly.Gate, "web-only-no-detector");
    }

    [TestMethod]
    public async Task InternalWorker_RealHostConsumesOneAcceptedRequestAtTheExternalLeafBoundary()
    {
        await using var fixture = WorkerFixture.Create(acceptedRun: true);
        AssertNonWebDescriptors(
            fixture.Descriptors,
            privateProtocol: true,
            typeof(InternalWorkerLifecycleService),
            typeof(IInitialProcessingRunAcquirer),
            typeof(IProcessingRunExecutor));

        int exit = await ImmichReverseGeo.Web.WorkerHost.InternalWorkerHost.RunHostAsync(fixture.Host, fixture.Outcomes).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(0, exit, "worker-accepted-exit");
        CollectionAssert.AreEqual(
            new[] { "initialise", "ready", "acquire", "execution-starting", "execute", "accepted-finality", "settle", "lease-dispose" },
            fixture.Ledger,
            "worker-real-lifecycle-order");
        Assert.AreEqual(1, fixture.Executor!.Calls, "worker-one-direct-executor");
        Assert.AreEqual(ProcessingRunTrigger.Manual, fixture.Executor.Request!.Trigger, "worker-request-preserved");
        Assert.AreEqual(0, fixture.ForbiddenResolutions, "worker-no-web-or-child-processing-resolution");
        Assert.AreEqual(1, fixture.Disposal.Count, "worker-host-disposed-once");
    }

    [TestMethod]
    public async Task WebOnlySavedSchedules_NeverReadOrPersistAutomaticSchedulingAndStillAllowManualChild()
    {
        foreach ((string name, ProcessingScheduleSnapshot snapshot) in new[]
        {
            ("enabled-valid", new ProcessingScheduleSnapshot(true, "0 * * * *")),
            ("enabled-invalid", new ProcessingScheduleSnapshot(true, "not-a-cron")),
            ("disabled", new ProcessingScheduleSnapshot(false, "0 * * * *")),
            ("empty", new ProcessingScheduleSnapshot(true, ""))
        })
        {
            await using var webOnly = WebFixture.Create(DeploymentMode.WebOnly, savedSchedule: snapshot);
            Assert.IsNull(webOnly.Provider.GetService<ProcessingBackgroundService>(), name + "-no-schedule-service");
            Assert.IsNull(webOnly.Provider.GetService<IScheduledRunTrigger>(), name + "-no-schedule-trigger");

            Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await webOnly.Provider.GetRequiredService<IManualProcessingRunCoordinator>().TriggerManualAsync(), name + "-manual-accepted");
            Assert.AreEqual(1, webOnly.Children.Count, name + "-manual-child");
            Assert.AreEqual(0, webOnly.Schedule!.Reads, name + "-no-schedule-read");
            Assert.AreEqual(0, webOnly.ScheduleTimerCreations, name + "-no-schedule-wait");
            Assert.AreEqual(0, webOnly.ListenerStarts, name + "-no-listener-start");
            Assert.IsFalse(webOnly.SettingsFileExists, name + "-no-settings-persistence");
        }
    }

    [TestMethod]
    public async Task WebOnlySavedSchedules_ActualHostStartupKeepsEveryStateManualOnly()
    {
        foreach ((string name, ProcessingScheduleSnapshot snapshot) in new[]
        {
            ("enabled-valid", new ProcessingScheduleSnapshot(true, "0 * * * *")),
            ("enabled-invalid", new ProcessingScheduleSnapshot(true, "not-a-cron")),
            ("disabled", new ProcessingScheduleSnapshot(false, "0 * * * *")),
            ("empty", new ProcessingScheduleSnapshot(true, ""))
        })
        {
            string root = Path.Combine(Path.GetTempPath(), "immich-reversegeo-change45-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            WebApplication? application = null;
            ProcessingState? observedState = null;
            Action? observer = null;
            var disposal = new DisposalReceipt();
            try
            {
                var builder = WebOnlyWebApplication.CreateBuilder(
                    DeploymentMode.WebOnly,
                    ["--environment=Development", "--contentRoot=" + root, "--applicationName=" + typeof(WebOnlyWebApplication).Assembly.GetName().Name],
                    variable => variable switch
                    {
                        "DATA_DIR" => Path.Combine(root, "data"),
                        "CONFIG_DIR" => Path.Combine(root, "config"),
                        _ => throw new AssertFailedException("unexpected-startup-environment-read-" + variable)
                    });
                ServiceDescriptor[] descriptors = builder.Services.ToArray();
                var server = new RecordingNoBindServer();
                var schedule = new RecordingSchedule(snapshot);
                var clock = new ScheduleTimeProvider();
                var children = new ChildBoundary();
                builder.Services.RemoveAll<IServer>();
                builder.Services.AddSingleton<IServer>(server);
                builder.Services.RemoveAll<IWorkerCommandRuntimeObservationSource>();
                builder.Services.AddSingleton<IWorkerCommandRuntimeObservationSource>(new ValidRuntimeSource(root));
                builder.Services.RemoveAll<IWorkerCommandRuntimeFactsCapture>();
                builder.Services.AddSingleton<IWorkerCommandRuntimeFactsCapture>(sp => new WorkerCommandRuntimeFactsCapture(sp.GetRequiredService<IWorkerCommandRuntimeObservationSource>()));
                builder.Services.AddSingleton<IProcessingScheduleConfiguration>(schedule);
                builder.Services.RemoveAll<TimeProvider>();
                builder.Services.AddSingleton<TimeProvider>(clock);
                builder.Services.RemoveAll<IChildProcessingRunBackend>();
                builder.Services.AddScoped<IChildProcessingRunBackend>(_ => children);
                builder.Services.AddSingleton(_ => disposal);
                application = WebApplicationComposition.Build(builder);
                _ = application.Services.GetRequiredService<DisposalReceipt>();
                observedState = application.Services.GetRequiredService<ProcessingState>();
                var observedTransitions = 0;
                var manualTransition = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                observer = () =>
                {
                    Interlocked.Increment(ref observedTransitions);
                    manualTransition.TrySetResult();
                };
                observedState.OnChanged += observer;

                await application.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
                int preManualTransitions = Volatile.Read(ref observedTransitions);
                Assert.AreEqual(1, server.Starts, name + "-host-started-without-binding");
                Assert.IsFalse(descriptors.Any(descriptor => descriptor.ServiceType == typeof(ProcessingBackgroundService)), name + "-no-scheduler-descriptor");
                Assert.AreEqual(0, schedule.Reads, name + "-started-host-no-schedule-read");
                Assert.AreEqual(0, clock.TimerCreations, name + "-started-host-no-schedule-wait");
                Assert.AreEqual(0, children.Count, name + "-started-host-no-automatic-child");
                Assert.AreEqual(0, preManualTransitions, name + "-started-host-no-pre-manual-state-transition");
                Assert.IsFalse(File.Exists(Path.Combine(root, "config", "settings.json")), name + "-started-host-no-settings-persistence");
                AssertMappedWebEndpoints(application, name + "-started-host");
                Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await application.Services.GetRequiredService<IManualProcessingRunCoordinator>().TriggerManualAsync(), name + "-started-host-manual-accepted");
                Assert.AreEqual(1, children.Count, name + "-started-host-manual-child");
                await manualTransition.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.IsGreaterThan(preManualTransitions, Volatile.Read(ref observedTransitions), name + "-observer-proves-manual-transition-receipt");
            }
            finally
            {
                if (observedState is not null && observer is not null)
                {
                    observedState.OnChanged -= observer;
                }
                if (application is not null)
                {
                    await application.DisposeAsync();
                }
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            Assert.AreEqual(1, disposal.Count, name + "-actual-web-host-disposed-once");
        }
    }

    [TestMethod]
    public async Task WebRoots_RunTheActualChildValidatorBeforeAcceptingManualProcessing()
    {
        foreach (DeploymentMode mode in new[] { DeploymentMode.Standard, DeploymentMode.WebOnly })
        {
            await using var fixture = WebFixture.Create(mode, validRuntimeFiles: true, sentinelExternal: true);
            await fixture.Provider.GetRequiredService<ChildWorkerStartupValidator>().StartingAsync(CancellationToken.None);
            Assert.AreEqual(1, fixture.RuntimeSource!.ProcessPathCalls, mode + "-validator-process-path-once");
            Assert.AreEqual(1, fixture.RuntimeSource.EntryAssemblyCalls, mode + "-validator-entry-assembly-once");
            Assert.AreEqual(1, fixture.RuntimeSource.WorkingDirectoryCalls, mode + "-validator-working-directory-once");
            Assert.AreEqual(3, fixture.RuntimeSource.TargetCalls, mode + "-validator-target-observations");
            Assert.AreEqual(0, fixture.Children.Count, mode.ToString() + "-validator-before-child");
            Assert.AreEqual(0, fixture.ListenerStarts, mode + "-validator-before-listener");
            Assert.AreEqual(0, fixture.ForbiddenResolutions, mode + "-validator-keeps-external-leaves-lazy");
            Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await fixture.Provider.GetRequiredService<IManualProcessingRunCoordinator>().TriggerManualAsync(), mode.ToString() + "-manual-after-validator");
            Assert.AreEqual(1, fixture.Children.Count, mode.ToString() + "-manual-child-after-validator");
            Assert.AreEqual(0, fixture.ForbiddenResolutions, mode + "-manual-stays-at-child-boundary");
        }
    }

    [TestMethod]
    public async Task WebValidationFailures_AcrossModesPreventReadinessSchedulingAndProcessingConstruction()
    {
        foreach (DeploymentMode mode in new[] { DeploymentMode.Standard, DeploymentMode.WebOnly })
        {
            foreach ((string failureName, bool includeRuntimeFiles, bool invalidShutdownBudget) in new[]
            {
                ("missing-runtime", false, false),
                ("invalid-shutdown-budget", true, true)
            })
            {
                string name = mode + "-" + failureName;
                string root = Path.Combine(Path.GetTempPath(), "immich-reversegeo-change45-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                WebApplication? application = null;
                ProcessingState? observedState = null;
                Action? observer = null;
                try
                {
                    string[] arguments = ["--environment=Development", "--contentRoot=" + root, "--applicationName=" + typeof(WebOnlyWebApplication).Assembly.GetName().Name];
                    string? ReadEnvironment(string variable) => variable switch
                    {
                        "DATA_DIR" => Path.Combine(root, "data"),
                        "CONFIG_DIR" => Path.Combine(root, "config"),
                        _ => throw new AssertFailedException("unexpected-failure-environment-read-" + variable)
                    };
                    WebApplicationBuilder builder = ReferenceEquals(mode, DeploymentMode.Standard)
                        ? StandardWebApplication.CreateBuilder(mode, arguments, ReadEnvironment)
                        : WebOnlyWebApplication.CreateBuilder(mode, arguments, ReadEnvironment);
                    var server = new RecordingNoBindServer();
                    var children = new ChildBoundary();
                    var readiness = new StartupReadinessProbe();
                    var sentinel = new ResolutionSentinel(mode == DeploymentMode.Standard ? BoundaryRole.Standard : BoundaryRole.WebOnly);
                    var runtime = new ValidRuntimeSource(root, includeRuntimeFiles);
                    var schedule = new RecordingSchedule(new ProcessingScheduleSnapshot(true, "0 * * * *"));
                    var clock = new ScheduleTimeProvider();
                    var gate = new WorkGate(true);
                    observedState = new ProcessingState();
                    var observedTransitions = 0;
                    observer = () => Interlocked.Increment(ref observedTransitions);
                    observedState.OnChanged += observer;
                    builder.Services.RemoveAll<IServer>();
                    builder.Services.AddSingleton<IServer>(server);
                    builder.Services.RemoveAll<IChildProcessingRunBackend>();
                    builder.Services.AddScoped<IChildProcessingRunBackend>(_ => children);
                    builder.Services.RemoveAll<IWorkerCommandRuntimeObservationSource>();
                    builder.Services.AddSingleton<IWorkerCommandRuntimeObservationSource>(runtime);
                    builder.Services.RemoveAll<IWorkerCommandRuntimeFactsCapture>();
                    builder.Services.AddSingleton<IWorkerCommandRuntimeFactsCapture>(sp => new WorkerCommandRuntimeFactsCapture(sp.GetRequiredService<IWorkerCommandRuntimeObservationSource>()));
                    builder.Services.RemoveAll<IProcessingScheduleConfiguration>();
                    builder.Services.AddSingleton<IProcessingScheduleConfiguration>(schedule);
                    builder.Services.RemoveAll<TimeProvider>();
                    builder.Services.AddSingleton<TimeProvider>(clock);
                    builder.Services.RemoveAll<ProcessingState>();
                    builder.Services.AddSingleton(observedState);
                    if (ReferenceEquals(mode, DeploymentMode.Standard))
                    {
                        builder.Services.RemoveAll<IProcessingWorkDetector>();
                        builder.Services.AddSingleton<IProcessingWorkDetector>(gate);
                    }
                    if (invalidShutdownBudget)
                    {
                        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.Zero);
                    }
                    ArmWebExternalSentinels(builder.Services, sentinel);
                    builder.Services.AddSingleton<IHostedService>(readiness);

                    if (invalidShutdownBudget)
                    {
                        OptionsValidationException failure = Assert.ThrowsExactly<OptionsValidationException>(
                            () => WebApplicationComposition.Build(builder));
                        CollectionAssert.AreEqual(
                            new[] { WorkerHostShutdownBudget.ValidationMessage },
                            failure.Failures.ToArray(),
                            name + "-planned-safe-shutdown-budget-classification");
                    }
                    else
                    {
                        application = WebApplicationComposition.Build(builder);
                        InvalidOperationException failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                            () => application.StartAsync().WaitAsync(TimeSpan.FromSeconds(5)));
                        Assert.AreEqual(
                            "The child worker runtime files are unavailable. Publish the complete Web application artifact and retry startup.",
                            failure.Message,
                            name + "-planned-safe-missing-runtime-classification");
                    }

                    int expectedRuntimeReads = invalidShutdownBudget ? 0 : 1;
                    Assert.AreEqual(expectedRuntimeReads, runtime.ProcessPathCalls, name + "-runtime-process-path-reads");
                    Assert.AreEqual(expectedRuntimeReads, runtime.EntryAssemblyCalls, name + "-runtime-entry-assembly-reads");
                    Assert.AreEqual(expectedRuntimeReads, runtime.WorkingDirectoryCalls, name + "-runtime-working-directory-reads");
                    Assert.AreEqual(expectedRuntimeReads * 3, runtime.TargetCalls, name + "-runtime-target-observations");
                    Assert.AreEqual(0, server.Starts, name + "-before-listener-start");
                    Assert.AreEqual(0, children.Count, name + "-before-child");
                    Assert.AreEqual(0, readiness.StartCalls, name + "-before-acceptance-readiness");
                    Assert.AreEqual(0, schedule.Reads, name + "-before-schedule-read");
                    Assert.AreEqual(0, clock.TimerCreations, name + "-before-schedule-wait");
                    if (ReferenceEquals(mode, DeploymentMode.Standard))
                    {
                        Assert.AreEqual(0, gate.Calls, name + "-before-scheduled-work-gate");
                    }
                   else
                   {
                        Assert.IsFalse(
                            builder.Services.Any(descriptor => descriptor.ServiceType == typeof(IProcessingWorkDetector)),
                            name + "-no-scheduled-work-gate-descriptor");
                   }
                    Assert.AreEqual(0, Volatile.Read(ref observedTransitions), name + "-before-processing-state-transition");
                    Assert.IsFalse(observedState.IsRunning, name + "-processing-never-pending-or-running");
                    Assert.AreEqual(0, sentinel.Resolutions, name + "-before-executor-database-geodata-process-http");
                    Assert.IsFalse(File.Exists(Path.Combine(root, "config", "settings.json")), name + "-no-settings-persistence");
                }
                finally
                {
                    if (observedState is not null && observer is not null)
                    {
                        observedState.OnChanged -= observer;
                    }
                    if (application is not null)
                    {
                        await application.DisposeAsync();
                    }
                    if (Directory.Exists(root))
                    {
                        Directory.Delete(root, recursive: true);
                    }
                }
            }
        }
    }

    [TestMethod]
    public async Task WebProcessingRoots_ReuseTheProductionFactoryGraphBeforeExternalOverrides()
    {
        foreach (DeploymentMode mode in new[] { DeploymentMode.Standard, DeploymentMode.WebOnly })
        {
            await using var fixture = WebFixture.Create(mode);
            var guard = new WebProcessingGeodataBoundaryTests.ProcessingFactoryGraph(fixture.Descriptors);
            Type[] roots = ReferenceEquals(mode, DeploymentMode.Standard)
                ? [typeof(IManualProcessingRunCoordinator), typeof(IScheduledRunTrigger), typeof(IProcessingWorkDetector), typeof(ProcessingBackgroundService), typeof(IChildProcessingRunBackend)]
                : [typeof(IManualProcessingRunCoordinator), typeof(IChildProcessingRunBackend)];

            string? failure = guard.FindForbiddenPath(roots);
            Assert.IsNull(failure, mode + "-production-processing-factory-path");
            Assert.AreEqual(0, guard.OpaqueFactoryCount, mode + "-production-processing-factory-opacity");
            Assert.IsTrue(guard.CapturedFactoryTypes.Contains(typeof(IChildProcessingRunBackend)), mode + "-production-child-factory-inspected");
        }
    }

    [TestMethod]
    public async Task RunOnce_EligibleAndAuthoritativeNoWorkAreOneFreshDirectAttemptAndOneScopeDisposal()
    {
        var runIds = new HashSet<Guid>();
        foreach ((string name, int eligibility) in new[] { ("eligible", 1), ("authoritative-no-work", 0) })
        {
            await using var fixture = RunOnceFixture.Create(overrideExecution: true, eligibility);
            AssertNonWebDescriptors(
                fixture.Descriptors,
                privateProtocol: false,
                typeof(ProcessingRunExecutor),
                typeof(IProcessingRunExecutor));
            Assert.AreEqual(0, fixture.Disposal.Count, name + "-host-live-before-run");
            int exit = await RunOnceApplication.RunHostAsync(fixture.Host, fixture.Outcomes);
            Assert.AreEqual(0, exit, name + "-exit");
            Assert.AreEqual(1, fixture.Initializer!.Calls, name + "-owning-root-initializer-once");
            Assert.AreEqual(1, fixture.Executor!.Calls, name + "-one-executor");
            Assert.IsNotNull(fixture.Executor.Request, name + "-fresh-request");
            Assert.AreNotEqual(Guid.Empty, fixture.Executor.Request.RunId, name + "-request-id");
            Assert.IsTrue(runIds.Add(fixture.Executor.Request.RunId), name + "-fresh-request-id");
            Assert.AreEqual(ProcessingRunTrigger.RunOnce, fixture.Executor.Request.Trigger, name + "-trigger");
            Assert.AreEqual(eligibility, fixture.Executor.Eligibility, name + "-eligibility");
            Assert.AreEqual(1, fixture.Executor.DisposeCalls, name + "-scope-disposal");
            Assert.AreEqual(1, fixture.Disposal.Count, name + "-host-disposal");
        }
    }

    [TestMethod]
    public async Task ParallelMatrix_FourIndependentRootsDoNotLeakSourcesProvidersOrFakes()
    {
        var bound = TimeSpan.FromSeconds(5);
        var overlap = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var providersArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = 0;
        var cases = new[]
        {
            ("missing", (string?)null, DeploymentMode.Standard),
            ("standard", (string?)"standard", DeploymentMode.Standard),
            ("web-only", (string?)"web-only", DeploymentMode.WebOnly),
            ("run-once", (string?)"run-once", DeploymentMode.RunOnce)
        };

        Task<ParallelResult>[] rows = cases.Select(async item =>
        {
            try
            {
                await Task.Yield();
                var source = new ModeSource(item.Item2);
                DeploymentMode? selected = null;
                ApplicationRoleStartup.Begin(
                    [], source.Read, TextWriter.Null,
                    (mode, _) => selected = mode,
                    _ => Assert.Fail(item.Item1 + "-private"),
                    (mode, _) => selected = mode,
                    _ => Assert.Fail(item.Item1 + "-private-exit"),
                    _ => Assert.Fail(item.Item1 + "-mode-exit"));
                if (ReferenceEquals(selected, DeploymentMode.RunOnce))
                {
                    await using var runOnceFixture = RunOnceFixture.Create(overrideExecution: true);
                    object providerIdentity = runOnceFixture.Provider;
                    object fakeIdentity = runOnceFixture.Executor!;
                    if (Interlocked.Increment(ref arrived) == cases.Length)
                    {
                        providersArrived.TrySetResult();
                    }
                    await overlap.Task.WaitAsync(bound);
                    int exit = await RunOnceApplication.RunHostAsync(runOnceFixture.Host, runOnceFixture.Outcomes);
                    Assert.AreEqual(0, exit, item.Item1 + "-run-once-exit");
                    Assert.AreEqual(1, runOnceFixture.Executor!.Calls, item.Item1 + "-run-once-one-executor");
                    return new ParallelResult(
                        item.Item1,
                        source,
                        providerIdentity,
                        fakeIdentity,
                        runOnceFixture.Disposal,
                        null,
                        null,
                        DescriptorSignature(runOnceFixture.Descriptors),
                        0,
                        null,
                        CountScheduledRegistrations(runOnceFixture.Descriptors),
                        0,
                        null,
                        0);
                }

                bool webOnly = ReferenceEquals(selected, DeploymentMode.WebOnly);
                await using var fixture = WebFixture.Create(
                    selected!,
                    savedSchedule: webOnly
                        ? new ProcessingScheduleSnapshot(true, "not-a-cron")
                        : null,
                    validRuntimeFiles: webOnly);
                var coordinator = fixture.Provider.GetRequiredService<ProcessingRunCoordinator>();
                var status = fixture.Provider.GetRequiredService<ProcessAssetsWebStatus>();
                Assert.AreSame(coordinator, fixture.Provider.GetRequiredService<IManualProcessingRunCoordinator>(), item.Item1 + "-manual-coordinator-alias");
                Assert.AreSame(status, fixture.Provider.GetRequiredService<IProcessAssetsWebStatus>(), item.Item1 + "-query-status-alias");
                Assert.AreSame(status, fixture.Provider.GetRequiredService<IProcessAssetsWorkerStatusSink>(), item.Item1 + "-sink-status-alias");
                var observedTransitions = 0;
                var manualTransition = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Action observer = () =>
                {
                    Interlocked.Increment(ref observedTransitions);
                    manualTransition.TrySetResult();
                };
                ProcessingState state = fixture.Provider.GetRequiredService<ProcessingState>();
                state.OnChanged += observer;
                try
                {
                    if (Interlocked.Increment(ref arrived) == cases.Length)
                    {
                        providersArrived.TrySetResult();
                    }
                    await overlap.Task.WaitAsync(bound);
                    if (webOnly)
                    {
                        await fixture.Application.StartAsync().WaitAsync(bound);
                        int preManualTransitions = Volatile.Read(ref observedTransitions);
                        int scheduledRegistrations = CountScheduledRegistrations(fixture.Descriptors);
                        Assert.AreEqual(0, scheduledRegistrations, item.Item1 + "-scheduler-gate-and-trigger-descriptors-absent");
                        Assert.IsNull(fixture.Provider.GetService<ProcessingBackgroundService>(), item.Item1 + "-scheduler-provider-absent");
                        Assert.IsNull(fixture.Provider.GetService<IScheduledRunTrigger>(), item.Item1 + "-scheduled-trigger-provider-absent");
                        Assert.IsNull(fixture.Provider.GetService<IProcessingWorkDetector>(), item.Item1 + "-scheduled-gate-provider-absent");
                        Assert.AreEqual(0, fixture.Children.Count, item.Item1 + "-pre-manual-child-count");
                        Assert.AreEqual(0, fixture.Schedule!.Reads, item.Item1 + "-pre-manual-schedule-reads");
                        Assert.AreEqual(0, fixture.ScheduleTimerCreations, item.Item1 + "-pre-manual-schedule-waits");
                        Assert.IsFalse(fixture.SettingsFileExists, item.Item1 + "-pre-manual-settings-persistence");
                        await fixture.Provider.GetRequiredService<IManualProcessingRunCoordinator>().TriggerManualAsync();
                        await manualTransition.Task.WaitAsync(bound);
                        Assert.IsGreaterThan(preManualTransitions, Volatile.Read(ref observedTransitions), item.Item1 + "-manual-transition-observed");
                        return new ParallelResult(
                            item.Item1,
                            source,
                            fixture.Provider,
                            fixture.Children,
                            fixture.Disposal,
                            coordinator,
                            status,
                            DescriptorSignature(fixture.Descriptors),
                            fixture.Children.Count,
                            null,
                            scheduledRegistrations,
                            fixture.Schedule!.Reads,
                            preManualTransitions,
                            fixture.ListenerStarts);
                    }

                    fixture.Gate!.Result = true;
                    await fixture.Provider.GetRequiredService<IScheduledRunTrigger>().TriggerScheduledAsync(CancellationToken.None);
                    return new ParallelResult(
                        item.Item1,
                        source,
                        fixture.Provider,
                        fixture.Children,
                        fixture.Disposal,
                        coordinator,
                        status,
                        DescriptorSignature(fixture.Descriptors),
                        fixture.Children.Count,
                        fixture.Gate.Calls,
                        CountScheduledRegistrations(fixture.Descriptors),
                        0,
                        null,
                        fixture.ListenerStarts);
                }
                finally
                {
                    state.OnChanged -= observer;
                }
            }
            catch (Exception exception)
            {
                failure.TrySetResult(exception);
                throw;
            }
        }).ToArray();
        Task<ParallelResult[]> allRows = Task.WhenAll(rows);
        ParallelResult[] results;
        try
        {
            Task completed = await Task.WhenAny(providersArrived.Task, failure.Task).WaitAsync(bound);
            if (ReferenceEquals(completed, failure.Task))
            {
                throw await failure.Task;
            }
            overlap.TrySetResult();
            results = await allRows.WaitAsync(bound);
        }
        finally
        {
            overlap.TrySetResult();
            try
            {
                await allRows.WaitAsync(bound);
            }
            catch
            {
            }
        }

        Assert.IsTrue(results.All(result => result.Source.Reads == 1), "parallel-one-mode-read-each");
        Assert.AreEqual(4, results.Select(result => result.Source).Distinct(ReferenceEqualityComparer.Instance).Count(), "parallel-no-source-leak");
        Assert.AreEqual(1, results.Single(result => result.Name == "missing").Children, "parallel-missing-standard-child");
        Assert.AreEqual(1, results.Single(result => result.Name == "standard").Children, "parallel-standard-child");
        Assert.AreEqual(1, results.Single(result => result.Name == "missing").DetectorCalls!.Value, "parallel-missing-standard-detector");
        Assert.AreEqual(1, results.Single(result => result.Name == "standard").DetectorCalls!.Value, "parallel-standard-detector");
        Assert.AreEqual(1, results.Single(result => result.Name == "web-only").Children, "parallel-web-only-manual-child");
        Assert.IsNull(results.Single(result => result.Name == "web-only").DetectorCalls, "parallel-web-only-detector-is-structurally-absent");
        Assert.AreEqual(0, results.Single(result => result.Name == "web-only").ScheduledRegistrationCount, "parallel-web-only-no-scheduled-registration");
        Assert.AreEqual(0, results.Single(result => result.Name == "web-only").ScheduleReads, "parallel-web-only-invalid-saved-schedule-unread");
        Assert.AreEqual(0, results.Single(result => result.Name == "web-only").PreManualTransitions!.Value, "parallel-web-only-started-without-automatic-state-transition");
        Assert.AreEqual(1, results.Single(result => result.Name == "web-only").ListenerStarts, "parallel-web-only-started-on-no-bind-server");
        Assert.AreEqual(0, results.Single(result => result.Name == "run-once").Children, "parallel-run-once-no-child");
        Assert.AreEqual(4, results.Select(result => result.Provider).Distinct(ReferenceEqualityComparer.Instance).Count(), "parallel-no-provider-leak");
        Assert.AreEqual(4, results.Select(result => result.Fake).Distinct(ReferenceEqualityComparer.Instance).Count(), "parallel-no-fake-leak");
        ParallelResult missing = results.Single(result => result.Name == "missing");
        ParallelResult explicitStandard = results.Single(result => result.Name == "standard");
        Assert.AreEqual(missing.DescriptorSignature, explicitStandard.DescriptorSignature, "parallel-missing-and-explicit-standard-service-graphs-equivalent");
        Assert.AreNotEqual(explicitStandard.DescriptorSignature, results.Single(result => result.Name == "web-only").DescriptorSignature, "parallel-web-only-service-graph-distinct");
        Assert.AreNotEqual(explicitStandard.DescriptorSignature, results.Single(result => result.Name == "run-once").DescriptorSignature, "parallel-run-once-service-graph-distinct");
        ParallelResult[] webResults = results.Where(result => result.Coordinator is not null).ToArray();
        Assert.HasCount(3, webResults, "parallel-three-web-roots");
        Assert.AreEqual(3, webResults.Select(result => result.Coordinator).Distinct(ReferenceEqualityComparer.Instance).Count(), "parallel-no-coordinator-singleton-leak");
        Assert.AreEqual(3, webResults.Select(result => result.Status).Distinct(ReferenceEqualityComparer.Instance).Count(), "parallel-no-status-singleton-leak");
        Assert.IsTrue(results.All(result => result.Disposal.Count == 1), "parallel-every-provider-disposed-once");
    }

    private static void CreateMinimalInventoryDatabase(
        string fixtureRoot,
        string iso3,
        string release)
    {
        string directory = Path.Combine(fixtureRoot, "data", "overture-divisions");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, iso3 + ".db");
        using var connection = new SqliteConnection($"Data Source={path};Pooling=false");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE division_area (id INTEGER);
            CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO _meta (key, value) VALUES ('downloadedAt', '2026-09-10T00:00:00Z');
            INSERT INTO _meta (key, value) VALUES ('release', $release);
            """;
        command.Parameters.AddWithValue("$release", release);
        command.ExecuteNonQuery();
    }

    private static void SetInjected(object component, string propertyName, object value)
    {
        PropertyInfo property = component.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new AssertFailedException($"Missing injected property {propertyName}.");
        property.SetValue(component, value);
    }

    private static void AssertWebDescriptors(IReadOnlyList<ServiceDescriptor> descriptors, bool scheduler)
    {
        foreach (Type webService in new[]
        {
            typeof(IServer),
            typeof(IDataProtectionProvider),
            typeof(IAntiforgery),
            typeof(IPostConfigureOptions<RazorComponentsServiceOptions>),
            typeof(IConfigureOptions<CircuitOptions>),
            typeof(ChildWorkerStartupValidator),
            typeof(IChildProcessingRunBackend),
            typeof(IChildWorkerLauncher)
        })
        {
            Assert.IsTrue(descriptors.Any(descriptor => descriptor.ServiceType == webService), webService.Name + "-web-present");
        }
        foreach (Type transitionalWebDependency in new[]
        {
            typeof(ImmichDbRepository),
            typeof(SkippedAssetsRepository),
            typeof(ICacheInventory),
            typeof(CacheDeletionCommand),
            typeof(CacheMutationPageControllerFactory),
            typeof(ConfigService)
        })
        {
            Assert.AreEqual(
                1,
                descriptors.Count(descriptor => descriptor.ServiceType == transitionalWebDependency),
                transitionalWebDependency.Name + "-control-plane-registration");
        }

        AssertSingletonAliasDescriptors<ProcessAssetsWebStatus, IProcessAssetsWebStatus>(descriptors);
        AssertSingletonAliasDescriptors<ProcessAssetsWebStatus, IProcessAssetsWorkerStatusSink>(descriptors);
        AssertSingletonAliasDescriptors<ProcessingStateEventReporter, IProcessingEventReporter>(descriptors);
        AssertSingletonAliasDescriptors<ProcessingRunCoordinator, IManualProcessingRunCoordinator>(descriptors);
        AssertSingletonAliasDescriptors<SystemChildProcessFactory, IChildProcessFactory>(descriptors);
        AssertSingletonAliasDescriptors<ChildWorkerLauncher, IChildWorkerLauncher>(descriptors);
        AssertSingletonAliasDescriptors<WorkerCommandAmbientRuntimeObservationSource, IWorkerCommandRuntimeObservationSource>(descriptors);
        AssertSingletonAliasDescriptors<WorkerCommandRuntimeFactsCapture, IWorkerCommandRuntimeFactsCapture>(descriptors);
        AssertSingletonAliasDescriptors<WorkerCommandInvocationBuilder, IWorkerCommandInvocationBuilder>(descriptors);
        AssertSingletonAliasDescriptors<WorkerJobCoordinator, IWorkerJobAdmissionGate>(descriptors);
        AssertSingletonAliasDescriptors<WorkerJobCoordinator, IWorkerJobArbitrationDiagnostics>(descriptors);
        AssertSingletonHostedAliasDescriptor<WorkerJobCoordinator>(descriptors);
        AssertSingletonAliasDescriptors<PhysicalCacheDeletionFileSystem, ICacheDeletionFileSystem>(descriptors);
        AssertSingletonAliasDescriptors<PhysicalCacheInventoryFileSystem, ICacheInventoryFileSystem>(descriptors);
        AssertSingletonAliasDescriptors<CacheInventorySqliteMetadataReader, ICacheInventoryMetadataReader>(descriptors);
        AssertSingletonAliasDescriptors<CacheInventoryStorageScanner, ICacheInventoryStorageScanner>(descriptors);
        AssertSingletonAliasDescriptors<CacheInventoryService, ICacheInventory>(descriptors);
        AssertSingletonAliasDescriptors<CacheInventoryService, ICacheInventoryInvalidator>(descriptors);
        Assert.AreEqual(
            ServiceLifetime.Singleton,
            descriptors.Single(descriptor => descriptor.ServiceType == typeof(CacheDeletionCommand)).Lifetime);
        Assert.AreEqual(
            ServiceLifetime.Singleton,
            descriptors.Single(descriptor => descriptor.ServiceType == typeof(CacheDeletionPageControllerFactory)).Lifetime);
        Assert.AreEqual(
            ServiceLifetime.Singleton,
            descriptors.Single(descriptor => descriptor.ServiceType == typeof(CacheInventoryDeletionOperations)).Lifetime);
        Assert.AreEqual(
            ServiceLifetime.Singleton,
            descriptors.Single(descriptor => descriptor.ServiceType == typeof(CacheInventoryDeletionPageControllerFactory)).Lifetime);
        AssertSingletonAliasDescriptors<ConfigCoordinateLookupSettingsSnapshotProvider, ICoordinateLookupSettingsSnapshotProvider>(descriptors);
        AssertSingletonAliasDescriptors<CoordinateLookupWorkerClient, ICoordinateLookupWorkerClient>(descriptors);
        AssertSingletonHostedAliasDescriptor<CoordinateLookupPageControllerHostLifetime>(descriptors);
        AssertSingletonAliasDescriptors<CacheInventoryMutationWorkerClient, ICacheMutationWorkerClient>(descriptors);
        AssertSingletonHostedAliasDescriptor<CacheMutationPageControllerHostLifetime>(descriptors);
        Assert.AreEqual(scheduler ? 7 : 6, descriptors.Count(descriptor => descriptor.ServiceType == typeof(IHostedService)), "web-hosted-alias-count");
        Assert.AreEqual(scheduler ? 1 : 0, descriptors.Count(descriptor => descriptor.ServiceType == typeof(ProcessingBackgroundService)), "scheduler-descriptor");
        Assert.AreEqual(scheduler ? 1 : 0, descriptors.Count(descriptor => descriptor.ServiceType == typeof(IScheduledRunTrigger)), "scheduled-trigger-descriptor");
        if (scheduler)
        {
            AssertSingletonAliasDescriptors<ProcessingRunCoordinator, IScheduledRunTrigger>(descriptors);
            AssertSingletonHostedAliasDescriptor<ProcessingBackgroundService>(descriptors);
        }
        else
        {
            foreach (Type scheduledService in new[]
            {
                typeof(ProcessingBackgroundService),
                typeof(IScheduledRunTrigger),
                typeof(IProcessingWorkDetector),
                typeof(IScheduledRunWorkProbe),
                typeof(IProcessingScheduleConfiguration)
            })
            {
                Assert.IsFalse(descriptors.Any(descriptor => descriptor.ServiceType == scheduledService), scheduledService.Name + "-web-only-absent");
            }
        }

        Assert.IsFalse(descriptors.Any(descriptor => descriptor.ServiceType == typeof(ProcessingRunExecutor)), "web-no-authoritative-executor-owner");
        Assert.IsFalse(descriptors.Any(descriptor => descriptor.ServiceType == typeof(IProcessingRunExecutor)), "web-no-authoritative-executor-alias");
        Assert.IsFalse(descriptors.Any(descriptor => descriptor.ServiceType == typeof(InternalWorkerLifecycleService)), "web-no-private-worker-lifecycle");
        Assert.IsFalse(descriptors.Any(descriptor => descriptor.ServiceType == typeof(IInitialProcessingRunAcquirer)), "web-no-private-request-source");
    }

    private static void AssertWebProviderAliases(IServiceProvider provider, bool scheduler)
    {
        AssertAlias<ProcessAssetsWebStatus, IProcessAssetsWebStatus>(provider);
        AssertAlias<ProcessAssetsWebStatus, IProcessAssetsWorkerStatusSink>(provider);
        AssertAlias<ProcessingStateEventReporter, IProcessingEventReporter>(provider);
        AssertAlias<ProcessingRunCoordinator, IManualProcessingRunCoordinator>(provider);
        AssertAlias<SystemChildProcessFactory, IChildProcessFactory>(provider);
        AssertAlias<ChildWorkerLauncher, IChildWorkerLauncher>(provider);
        AssertAlias<WorkerCommandAmbientRuntimeObservationSource, IWorkerCommandRuntimeObservationSource>(provider);
        AssertAlias<WorkerCommandRuntimeFactsCapture, IWorkerCommandRuntimeFactsCapture>(provider);
        AssertAlias<WorkerCommandInvocationBuilder, IWorkerCommandInvocationBuilder>(provider);
        AssertAlias<WorkerJobCoordinator, IWorkerJobAdmissionGate>(provider);
        AssertAlias<WorkerJobCoordinator, IWorkerJobArbitrationDiagnostics>(provider);
        AssertHostedAlias<WorkerJobCoordinator>(provider);
        AssertAlias<PhysicalCacheDeletionFileSystem, ICacheDeletionFileSystem>(provider);
        AssertAlias<PhysicalCacheInventoryFileSystem, ICacheInventoryFileSystem>(provider);
        AssertAlias<CacheInventorySqliteMetadataReader, ICacheInventoryMetadataReader>(provider);
        AssertAlias<CacheInventoryStorageScanner, ICacheInventoryStorageScanner>(provider);
        AssertAlias<CacheInventoryService, ICacheInventory>(provider);
        AssertAlias<CacheInventoryService, ICacheInventoryInvalidator>(provider);
        Assert.AreSame(
            provider.GetRequiredService<CacheDeletionCommand>(),
            provider.GetRequiredService<CacheDeletionCommand>());
        Assert.AreSame(
            provider.GetRequiredService<CacheDeletionPageControllerFactory>(),
            provider.GetRequiredService<CacheDeletionPageControllerFactory>());
        Assert.AreSame(
            provider.GetRequiredService<CacheInventoryDeletionPageControllerFactory>(),
            provider.GetRequiredService<CacheInventoryDeletionPageControllerFactory>());
        AssertAlias<ConfigCoordinateLookupSettingsSnapshotProvider, ICoordinateLookupSettingsSnapshotProvider>(provider);
        AssertAlias<CoordinateLookupWorkerClient, ICoordinateLookupWorkerClient>(provider);
        AssertAlias<CacheInventoryMutationWorkerClient, ICacheMutationWorkerClient>(provider);
        AssertHostedAlias<ProcessingRunCoordinator>(provider);
        AssertHostedAlias<ChildWorkerStartupValidator>(provider);
        AssertHostedAlias<CoordinateLookupPageControllerHostLifetime>(provider);
        AssertHostedAlias<CacheMutationPageControllerHostLifetime>(provider);
        ProcessAssetsWebStatus status = provider.GetRequiredService<ProcessAssetsWebStatus>();
        Assert.AreSame(status, provider.GetRequiredService<IProcessAssetsWebStatus>(), "web-status-query-alias");
        Assert.AreSame(status, provider.GetRequiredService<IProcessAssetsWorkerStatusSink>(), "web-status-sink-alias");
        if (scheduler)
        {
            AssertHostedAlias<ProcessingBackgroundService>(provider);
            Assert.AreSame(provider.GetRequiredService<ProcessingRunCoordinator>(), provider.GetRequiredService<IScheduledRunTrigger>(), "scheduled-trigger-coordinator-alias");
        }
    }

    private static void AssertMappedWebEndpoints(WebApplication application, string name)
    {
        string?[] routes = ((IEndpointRouteBuilder)application).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToArray();
        foreach (string route in new[] { "/", "/settings", "/lookup", "/data" })
        {
            CollectionAssert.Contains(routes, route, name + "-mapped-" + route);
        }
    }

    private static int CountScheduledRegistrations(IReadOnlyList<ServiceDescriptor> descriptors)
    {
        Type[] scheduledTypes =
        [
            typeof(ProcessingBackgroundService),
            typeof(IScheduledRunTrigger),
            typeof(IProcessingWorkDetector),
            typeof(IScheduledRunWorkProbe),
            typeof(IProcessingScheduleConfiguration)
        ];
        return descriptors.Count(descriptor => scheduledTypes.Contains(descriptor.ServiceType));
    }

    private static string DescriptorSignature(IReadOnlyList<ServiceDescriptor> descriptors)
    {
        return string.Join(
            "|",
            descriptors
                .Select(descriptor => $"{descriptor.ServiceType.AssemblyQualifiedName}:{descriptor.Lifetime}")
                .Order(StringComparer.Ordinal));
    }

    private static void AssertNonWebDescriptors(
        IReadOnlyList<ServiceDescriptor> descriptors,
        bool privateProtocol,
        params Type[] required)
    {
        foreach (Type service in required)
        {
            Assert.IsTrue(descriptors.Any(descriptor => descriptor.ServiceType == service), service.Name + "-present");
        }

        foreach (Type forbidden in ControlPlaneDependencyPolicy.WebOnlyTypes)
        {
            Assert.IsFalse(descriptors.Any(descriptor => descriptor.ServiceType == forbidden), forbidden.Name + "-absent");
        }

        Type[] privateServices =
        [
            typeof(WorkerStdinTransportConfigured), typeof(IWorkerTransportAvailability),
            typeof(IWorkerStandardInputStreamFactory), typeof(WorkerStdinRequestSource),
            typeof(IInitialProcessingRunAcquirer), typeof(WorkerStdinAcceptedRunFinality),
            typeof(IWorkerAcceptedRunFinality), typeof(TransitionalWorkerPreRequestFinality),
            typeof(IWorkerPreRequestFinality), typeof(WorkerNdjsonEmitter),
            typeof(IWorkerReadinessPublisher), typeof(WorkerNdjsonProcessingEventReporter),
            typeof(InternalWorkerLifecycleService)
        ];
        foreach (Type privateService in privateServices)
        {
            Assert.AreEqual(
                privateProtocol ? 1 : 0,
                descriptors.Count(descriptor => descriptor.ServiceType == privateService),
                privateService.Name + (privateProtocol ? "-private-present-once" : "-run-once-private-absent"));
        }

        Assert.AreEqual(privateProtocol ? 1 : 0, descriptors.Count(descriptor => descriptor.ServiceType == typeof(IHostedService)), "non-web-hosted-service-count");
    }

    private static void AssertSingletonHostedAliasDescriptor<TOwner>(IReadOnlyList<ServiceDescriptor> descriptors)
        where TOwner : class, IHostedService
    {
        Assert.AreEqual(ServiceLifetime.Singleton, descriptors.Single(descriptor => descriptor.ServiceType == typeof(TOwner)).Lifetime, typeof(TOwner).Name);
        Assert.IsTrue(
            descriptors.Any(descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.Lifetime == ServiceLifetime.Singleton
                && descriptor.ImplementationFactory is not null),
            typeof(TOwner).Name + "-hosted-factory");
    }

    private static void AssertSingletonAliasDescriptors<TOwner, TAlias>(IReadOnlyList<ServiceDescriptor> descriptors)
        where TOwner : class
        where TAlias : class
    {
        Assert.AreEqual(ServiceLifetime.Singleton, descriptors.Single(descriptor => descriptor.ServiceType == typeof(TOwner)).Lifetime, typeof(TOwner).Name);
        ServiceDescriptor alias = descriptors.Single(descriptor => descriptor.ServiceType == typeof(TAlias));
        Assert.AreEqual(ServiceLifetime.Singleton, alias.Lifetime, typeof(TAlias).Name);
        Assert.IsNotNull(alias.ImplementationFactory, typeof(TAlias).Name + "-factory");
    }

    private static void AssertAlias<TOwner, TAlias>(IServiceProvider provider)
        where TOwner : class
        where TAlias : class
    {
        Assert.AreSame(
            (object)provider.GetRequiredService<TOwner>(),
            provider.GetRequiredService<TAlias>(),
            typeof(TOwner).Name + "-" + typeof(TAlias).Name);
    }

    private static void AssertHostedAlias<TOwner>(IServiceProvider provider)
        where TOwner : class, IHostedService
    {
        TOwner owner = provider.GetRequiredService<TOwner>();
        IHostedService[] hosted = provider.GetServices<IHostedService>().Where(service => service is TOwner).ToArray();
        Assert.HasCount(1, hosted, typeof(TOwner).Name + "-hosted-alias-count");
        Assert.AreSame((IHostedService)owner, hosted[0], typeof(TOwner).Name + "-hosted-alias");
    }

    private sealed record ParallelResult(
        string Name,
        ModeSource Source,
        object Provider,
        object Fake,
        DisposalReceipt Disposal,
        object? Coordinator,
        object? Status,
        string DescriptorSignature,
        int Children,
        int? DetectorCalls,
        int ScheduledRegistrationCount,
        int ScheduleReads,
        int? PreManualTransitions,
        int ListenerStarts);

    private sealed class ModeSource(string? value)
    {
        private readonly IReadOnlyDictionary<string, string?> _values = value is null
            ? new Dictionary<string, string?>(StringComparer.Ordinal)
            : new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["IMMICH_REVERSEGEO_MODE"] = value
            };
        private int _reads;
        internal int Reads => Volatile.Read(ref _reads);
        internal string? Read(string name)
        {
            Assert.AreEqual("IMMICH_REVERSEGEO_MODE", name, "mode-source-key");
            Interlocked.Increment(ref _reads);
            return _values.TryGetValue(name, out string? current) ? current : null;
        }
    }

    private sealed class PreHostSideEffectProbe
    {
        private readonly ConcurrentDictionary<string, int> _counts = new(
            new[]
            {
                "composition-root", "builder", "provider", "application-log", "path", "filesystem",
                "settings", "listener", "database", "geodata", "download", "docker", "process",
                "http", "socket", "work"
            }.Select(name => new KeyValuePair<string, int>(name, 0)));

        internal void Enter(string root)
        {
            _counts.AddOrUpdate("composition-root", 1, static (_, current) => checked(current + 1));
            foreach (string category in _counts.Keys.Where(category => category != "composition-root"))
            {
                _counts.AddOrUpdate(category, 1, static (_, current) => checked(current + 1));
            }
            Assert.Fail("unexpected-pre-host-continuation-" + root);
        }

        internal void AssertUntouched(string name)
        {
            foreach ((string category, int count) in _counts.OrderBy(pair => pair.Key))
            {
                Assert.AreEqual(0, count, name + "-pre-host-" + category);
            }
        }
    }

    private sealed class WorkGate(bool result) : IProcessingWorkDetector
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        internal bool Result { get; set; } = result;
        public Task<ProcessingWorkDetectionResult> DetectAsync(ProcessingWorkDetectionRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            return Task.FromResult(ProcessingWorkDetectorStub.Result(Result));
        }
    }

    private sealed class ChildBoundary : IChildProcessingRunBackend, IAsyncDisposable
    {
        private int _count;
        internal int Count => Volatile.Read(ref _count);
        public async Task<ProcessingRunResult> ExecuteAsync(ProcessingRunRequest request, IProcessingEventReporter reporter, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _count);
            var session = await reporter.OpenRunAsync(request, Now, cancellationToken);
            await session.DetermineEligibilityAsync(0, cancellationToken);
            var result = new ProcessingRunResult(request, Now, Now, 0, 0, 0, 0, ProcessingRunOutcome.Completed, null);
            await session.FinishAsync(result);
            return result;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingInitializer : IWorkerStartupInitializer
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        public Task InitialiseAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingExecutor(List<string>? ledger = null, int eligibility = 0) : IProcessingRunExecutor, IAsyncDisposable
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        internal ProcessingRunRequest? Request { get; private set; }
        internal int Eligibility { get; } = eligibility;
        private int _disposeCalls;
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);

        public async Task<ProcessingRunResult> ExecuteAsync(ProcessingRunRequest request, IProcessingEventReporter reporter, CancellationToken cancellationToken)
        {
            Request = request;
            Interlocked.Increment(ref _calls);
            ledger?.Add("execute");
            var session = await reporter.OpenRunAsync(request, Now, cancellationToken);
            await session.DetermineEligibilityAsync(Eligibility, cancellationToken);
            var result = new ProcessingRunResult(request, Now, Now, 0, 0, 0, 0, ProcessingRunOutcome.Completed, null);
            await session.FinishAsync(result);
            return result;
        }
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class LedgerInitializer(List<string> ledger) : IWorkerStartupInitializer
    {
        public Task InitialiseAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ledger.Add("initialise");
            return Task.CompletedTask;
        }
    }

    private sealed class ConfiguredTransport : IWorkerTransportAvailability
    {
        public bool IsConfigured => true;
    }

    private sealed class LedgerReadiness(List<string> ledger) : IWorkerReadinessPublisher
    {
        public Task PublishAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ledger.Add("ready");
            return Task.CompletedTask;
        }
    }

    private sealed class AcceptedAcquirer(IProcessingRunLease lease, List<string> ledger) : IInitialProcessingRunAcquirer
    {
        public Task<InitialProcessingRunAcquisition> AcquireAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ledger.Add("acquire");
            return Task.FromResult<InitialProcessingRunAcquisition>(InitialProcessingRunAcquisition.Accept(lease));
        }
    }

    private sealed class RecordingLease(ProcessingRunRequest request, List<string> ledger) : IProcessingRunLease
    {
        public ProcessingRunRequest Request { get; } = request;
        public CancellationToken CancellationToken => CancellationToken.None;
        public void NotifyExecutionStarting() => ledger.Add("execution-starting");
        public ValueTask<WorkerInputPumpFinality> SettleAsync(CancellationToken cancellationToken)
        {
            ledger.Add("settle");
            return ValueTask.FromResult(WorkerInputPumpFinality.ControlsClosed());
        }
        public ValueTask DisposeAsync()
        {
            ledger.Add("lease-dispose");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class LedgerAcceptedFinality(List<string> ledger) : IWorkerAcceptedRunFinality
    {
        public Task CompleteAsync(ProcessingRunRequest request, ProcessingRunResult result, CancellationToken cancellationToken)
        {
            ledger.Add("accepted-finality");
            return Task.CompletedTask;
        }
        public Task FailAsync(ProcessingRunRequest request, WorkerSafeFailure failure, CancellationToken cancellationToken) => Task.FromException(new AssertFailedException("worker-accepted-failure"));
    }

    private sealed class NoOpPreRequestFinality : IWorkerPreRequestFinality
    {
        public Task CompleteAsync(WorkerPreRequestOutcome outcome, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static void ReplaceSingleton<TService>(IServiceCollection services, TService instance)
        where TService : class
    {
        services.RemoveAll<TService>();
        services.AddSingleton(instance);
    }

    private sealed class RecordingSchedule(ProcessingScheduleSnapshot snapshot) : IProcessingScheduleConfiguration
    {
        private int _reads;
        internal int Reads => Volatile.Read(ref _reads);
        public Task<ProcessingScheduleSnapshot> GetSnapshotAsync()
        {
            Interlocked.Increment(ref _reads);
            return Task.FromResult(snapshot);
        }
    }

    private sealed class ScheduleTimeProvider : TimeProvider
    {
        private int _timerCreations;
        internal int TimerCreations => Volatile.Read(ref _timerCreations);
        public override DateTimeOffset GetUtcNow() => Now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            // Only the known read-model timer is excluded; scheduler/retry and
            // any other timer still count against the no-scheduling assertion.
            if (callback.Target is not ImmichReverseGeo.Web.WorkerEventDelivery.ReadModelNotificationCadence)
            {
                Interlocked.Increment(ref _timerCreations);
            }
            return TimeProvider.System.CreateTimer(callback, state, dueTime, period);
        }
    }

    private sealed class ResolutionSentinel(BoundaryRole role)
    {
        private readonly BoundaryRuntimeSentinel _sentinel = new(role);
        internal int Resolutions => _sentinel.Events.Count;
        internal T Fail<T>()
        {
            return _sentinel.Forbid<T>("forbidden-external-resolution-" + typeof(T).Name,
                ControlPlaneDependencyPolicy.HeavyTypes.GetValueOrDefault(typeof(T)) ?? typeof(T).Name);
        }
    }

    private sealed class NoLaunchChildProcessFactory(BoundaryRole role) : IChildProcessFactory
    {
        private readonly BoundaryRuntimeSentinel _sentinel = new(role);
        internal int StartCalls => _sentinel.Events.Count;

        public ValueTask<IChildProcess?> StartAsync(
            ChildProcessStartDescriptor descriptor,
            CancellationToken cancellationToken)
        {
            return _sentinel.Forbid<ValueTask<IChildProcess?>>("production page", "worker session");
        }
    }

    private static void ArmWebExternalSentinels(IServiceCollection services, ResolutionSentinel sentinel)
    {
        services.RemoveAll<IProcessingRunExecutor>();
        services.AddSingleton<IProcessingRunExecutor>(_ => sentinel.Fail<IProcessingRunExecutor>());
        services.RemoveAll<IProcessingAssetRepository>();
        services.AddSingleton<IProcessingAssetRepository>(_ => sentinel.Fail<IProcessingAssetRepository>());
        services.RemoveAll<IProcessingAdministrativeResolver>();
        services.AddSingleton<IProcessingAdministrativeResolver>(_ => sentinel.Fail<IProcessingAdministrativeResolver>());
        services.RemoveAll<IProcessingInfrastructureLookup>();
        services.AddSingleton<IProcessingInfrastructureLookup>(_ => sentinel.Fail<IProcessingInfrastructureLookup>());
        services.RemoveAll<IChildProcessFactory>();
        services.AddSingleton<IChildProcessFactory>(_ => sentinel.Fail<IChildProcessFactory>());
    }

    private sealed class StartupReadinessProbe : IHostedService
    {
        private int _startCalls;
        internal int StartCalls => Volatile.Read(ref _startCalls);
        public Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _startCalls);
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class DisposalReceipt : IAsyncDisposable
    {
        private int _count;
        internal int Count => Volatile.Read(ref _count);
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _count);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingNoBindServer : IServer
    {
        private int _starts;
        internal int Starts => Volatile.Read(ref _starts);
        public IFeatureCollection Features { get; } = CreateFeatures();
        public Task StartAsync<TContext>(Microsoft.AspNetCore.Hosting.Server.IHttpApplication<TContext> application, CancellationToken cancellationToken)
            where TContext : notnull
        {
            Interlocked.Increment(ref _starts);
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose()
        {
        }
        private static IFeatureCollection CreateFeatures()
        {
            var features = new FeatureCollection();
            features.Set<IServerAddressesFeature>(new ServerAddressesFeature());
            return features;
        }
    }

    private sealed class WebFixture : IAsyncDisposable
    {
        private WebFixture(
            string root,
            WebApplication application,
            IReadOnlyList<ServiceDescriptor> descriptors,
            ChildBoundary children,
            WorkGate? gate,
            RecordingSchedule? schedule,
            ResolutionSentinel? sentinel,
            ScheduleTimeProvider? scheduleClock,
            RecordingNoBindServer listener,
            ValidRuntimeSource? runtimeSource,
            DisposalReceipt disposal)
        {
            Root = root;
            Application = application;
            Descriptors = descriptors;
            Children = children;
            Gate = gate;
            Schedule = schedule;
            Sentinel = sentinel;
            ScheduleClock = scheduleClock;
            Listener = listener;
            RuntimeSource = runtimeSource;
            Disposal = disposal;
        }

        internal string Root { get; }
        internal WebApplication Application { get; }
        internal IServiceProvider Provider => Application.Services;
        internal IReadOnlyList<ServiceDescriptor> Descriptors { get; }
        internal ChildBoundary Children { get; }
        internal WorkGate? Gate { get; }
        internal RecordingSchedule? Schedule { get; }
        internal int ScheduleTimerCreations => ScheduleClock?.TimerCreations ?? 0;
        internal bool SettingsFileExists => File.Exists(Path.Combine(Root, "config", "settings.json"));
        internal ResolutionSentinel? Sentinel { get; }
        internal int ForbiddenResolutions => Sentinel?.Resolutions ?? 0;
        internal ScheduleTimeProvider? ScheduleClock { get; }
        internal RecordingNoBindServer Listener { get; }
        internal int ListenerStarts => Listener.Starts;
        internal ValidRuntimeSource? RuntimeSource { get; }
        internal DisposalReceipt Disposal { get; }

        internal static WebFixture Create(
            DeploymentMode mode,
            bool scheduledWork = false,
            ProcessingScheduleSnapshot? savedSchedule = null,
            bool validRuntimeFiles = false,
            bool sentinelExternal = false,
            IChildProcessFactory? childProcessBoundary = null,
            Action<IServiceCollection>? configureServices = null)
        {
            string root = Path.Combine(Path.GetTempPath(), "immich-reversegeo-change45-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            WebApplication? application = null;
            try
            {
            string[] arguments = ["--environment=Development", "--contentRoot=" + root, "--applicationName=" + typeof(WebOnlyWebApplication).Assembly.GetName().Name];
            string? ReadEnvironment(string name) => name switch
            {
                "DATA_DIR" => Path.Combine(root, "data"),
                "CONFIG_DIR" => Path.Combine(root, "config"),
                _ => throw new AssertFailedException("unexpected-web-fixture-environment-read-" + name)
            };
            WebApplicationBuilder builder = ReferenceEquals(mode, DeploymentMode.Standard)
                ? StandardWebApplication.CreateBuilder(mode, arguments, ReadEnvironment)
                : WebOnlyWebApplication.CreateBuilder(mode, arguments, ReadEnvironment);
            IServiceCollection services = builder.Services;

            IReadOnlyList<ServiceDescriptor> descriptors = services.ToArray();
            var children = new ChildBoundary();
            var listener = new RecordingNoBindServer();
            var disposal = new DisposalReceipt();
            services.RemoveAll<IServer>();
            services.AddSingleton<IServer>(listener);
            services.RemoveAll<IChildProcessingRunBackend>();
            services.AddScoped<IChildProcessingRunBackend>(_ => children);
            ValidRuntimeSource? runtimeSource = null;
            if (validRuntimeFiles)
            {
                runtimeSource = new ValidRuntimeSource(root);
                services.RemoveAll<IWorkerCommandRuntimeObservationSource>();
                services.AddSingleton<IWorkerCommandRuntimeObservationSource>(runtimeSource);
                services.RemoveAll<IWorkerCommandRuntimeFactsCapture>();
                services.AddSingleton<IWorkerCommandRuntimeFactsCapture>(sp => new WorkerCommandRuntimeFactsCapture(sp.GetRequiredService<IWorkerCommandRuntimeObservationSource>()));
            }
            WorkGate? gate = null;
            if (ReferenceEquals(mode, DeploymentMode.Standard))
            {
                gate = new WorkGate(scheduledWork);
                services.RemoveAll<IProcessingWorkDetector>();
                services.AddSingleton<IProcessingWorkDetector>(gate);
            }
            RecordingSchedule? schedule = null;
            ScheduleTimeProvider? scheduleClock = null;
            if (savedSchedule is not null)
            {
                schedule = new RecordingSchedule(savedSchedule);
                services.RemoveAll<IProcessingScheduleConfiguration>();
                services.AddSingleton<IProcessingScheduleConfiguration>(schedule);
                scheduleClock = new ScheduleTimeProvider();
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(scheduleClock);
            }

            ResolutionSentinel? sentinel = null;
            if (sentinelExternal)
            {
                sentinel = new ResolutionSentinel(mode == DeploymentMode.Standard ? BoundaryRole.Standard : BoundaryRole.WebOnly);
                ArmWebExternalSentinels(services, sentinel);
            }
            if (childProcessBoundary is not null)
            {
                services.RemoveAll<IChildProcessFactory>();
                services.AddSingleton(childProcessBoundary);
            }
            services.AddSingleton(_ => disposal);

            configureServices?.Invoke(services);
            application = WebApplicationComposition.Build(builder);
            _ = application.Services.GetRequiredService<DisposalReceipt>();
            return new WebFixture(root, application, descriptors, children, gate, schedule, sentinel, scheduleClock, listener, runtimeSource, disposal);
            }
            catch
            {
                if (application is not null)
                {
                    application.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Application.DisposeAsync();
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class ValidRuntimeSource : IWorkerCommandRuntimeObservationSource
    {
        private const string DotnetHostPath = "/test-runtime/dotnet";
        private readonly string _root;
        private readonly string _assemblyPath;
        private int _processPathCalls;
        private int _entryAssemblyCalls;
        private int _workingDirectoryCalls;
        private int _targetCalls;
        internal int ProcessPathCalls => Volatile.Read(ref _processPathCalls);
        internal int EntryAssemblyCalls => Volatile.Read(ref _entryAssemblyCalls);
        internal int WorkingDirectoryCalls => Volatile.Read(ref _workingDirectoryCalls);
        internal int TargetCalls => Volatile.Read(ref _targetCalls);
        internal ValidRuntimeSource(string root, bool includeRuntimeFiles = true)
        {
            _root = root;
            string runtime = Path.Combine(root, "runtime");
            Directory.CreateDirectory(runtime);
            _assemblyPath = Path.Combine(runtime, "ImmichReverseGeo.Web.dll");
            File.WriteAllText(_assemblyPath, string.Empty);
            if (includeRuntimeFiles)
            {
                File.WriteAllText(Path.ChangeExtension(_assemblyPath, ".runtimeconfig.json"), "{}");
                File.WriteAllText(Path.ChangeExtension(_assemblyPath, ".deps.json"), "{}");
            }
        }
        public string? GetProcessPath()
        {
            Interlocked.Increment(ref _processPathCalls);
            return DotnetHostPath;
        }
        public WorkerCommandEntryAssemblyObservation GetEntryAssembly()
        {
            Interlocked.Increment(ref _entryAssemblyCalls);
            return new("ImmichReverseGeo.Web", _assemblyPath);
        }
        public string GetCurrentDirectory()
        {
            Interlocked.Increment(ref _workingDirectoryCalls);
            return _root;
        }
        public bool IsWindows() => false;
        public WorkerTargetObservation ObserveTarget(string path)
        {
            Interlocked.Increment(ref _targetCalls);
            return path == DotnetHostPath
                ? WorkerTargetObservation.File
                : Directory.Exists(path) ? WorkerTargetObservation.Directory
                : File.Exists(path) ? WorkerTargetObservation.File
                : WorkerTargetObservation.Missing;
        }
    }

    private sealed class RunOnceFixture : IAsyncDisposable
    {
        private RunOnceFixture(
            string root,
            IHost host,
            IReadOnlyList<ServiceDescriptor> descriptors,
            WorkerProcessExitOutcomeAccumulator outcomes,
            RecordingExecutor? executor,
            RecordingInitializer? initializer,
            DisposalReceipt disposal)
        {
            Root = root;
            Host = host;
            Descriptors = descriptors;
            Outcomes = outcomes;
            Executor = executor;
            Initializer = initializer;
            Disposal = disposal;
        }

        internal string Root { get; }
        internal IHost Host { get; }
        internal IServiceProvider Provider => Host.Services;
        internal IReadOnlyList<ServiceDescriptor> Descriptors { get; }
        internal WorkerProcessExitOutcomeAccumulator Outcomes { get; }
        internal RecordingExecutor? Executor { get; }
        internal RecordingInitializer? Initializer { get; }
        internal DisposalReceipt Disposal { get; }

        internal static RunOnceFixture Create(bool overrideExecution = false, int eligibility = 0)
        {
            string root = Path.Combine(Path.GetTempPath(), "immich-reversegeo-change45-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var outcomes = new WorkerProcessExitOutcomeAccumulator();
            var builder = RunOnceApplication.CreateBuilder(
                DeploymentMode.RunOnce, [],
                name => name switch
                {
                    "DATA_DIR" => Path.Combine(root, "data"),
                    "CONFIG_DIR" => Path.Combine(root, "config"),
                    _ => throw new AssertFailedException("unexpected-root-read-" + name)
                },
                TextWriter.Null, TextWriter.Null, outcomes);
            IReadOnlyList<ServiceDescriptor> descriptors = builder.Services.ToArray();
            RecordingExecutor? executor = null;
            RecordingInitializer? initializer = null;
            if (overrideExecution)
            {
                executor = new RecordingExecutor(eligibility: eligibility);
                initializer = new RecordingInitializer();
                builder.Services.RemoveAll<IWorkerStartupInitializer>();
                builder.Services.AddSingleton<IWorkerStartupInitializer>(initializer);
                builder.Services.RemoveAll<IProcessingRunExecutor>();
                builder.Services.AddScoped<IProcessingRunExecutor>(_ => executor);
                builder.Services.RemoveAll<IProcessingEventReporter>();
                builder.Services.AddSingleton<IProcessingEventReporter>(NoOpProcessingEventReporter.Instance);
            }
            var disposal = new DisposalReceipt();
            builder.Services.AddSingleton(_ => disposal);
            IHost host = builder.Build();
            _ = host.Services.GetRequiredService<DisposalReceipt>();
            return new RunOnceFixture(root, host, descriptors, outcomes, executor, initializer, disposal);
        }

        public async ValueTask DisposeAsync()
        {
            await ((IAsyncDisposable)Host).DisposeAsync();
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class WorkerFixture : IAsyncDisposable
    {
        private WorkerFixture(
            string root,
            IHost host,
            IReadOnlyList<ServiceDescriptor> descriptors,
            WorkerProcessExitOutcomeAccumulator outcomes,
            List<string> ledger,
            RecordingExecutor? executor,
            ResolutionSentinel? sentinel,
            DisposalReceipt disposal)
        {
            Root = root;
            Host = host;
            Descriptors = descriptors;
            Outcomes = outcomes;
            Ledger = ledger;
            Executor = executor;
            Sentinel = sentinel;
            Disposal = disposal;
        }

        internal string Root { get; }
        internal IHost Host { get; }
        internal IServiceProvider Provider => Host.Services;
        internal IReadOnlyList<ServiceDescriptor> Descriptors { get; }
        internal WorkerProcessExitOutcomeAccumulator Outcomes { get; }
        internal List<string> Ledger { get; }
        internal RecordingExecutor? Executor { get; }
        internal int ForbiddenResolutions => Sentinel?.Resolutions ?? 0;
        internal ResolutionSentinel? Sentinel { get; }
        internal DisposalReceipt Disposal { get; }

        internal static WorkerFixture Create(bool acceptedRun = false)
        {
            string root = Path.Combine(Path.GetTempPath(), "immich-reversegeo-change45-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var context = ApplicationCompositionContext.Create(CompositionEnvironment.Development, root, Path.Combine(root, "data"), Path.Combine(root, "config"));
            var outcomes = new WorkerProcessExitOutcomeAccumulator();
            var builder = ImmichReverseGeo.Web.WorkerHost.InternalWorkerHost.CreateBuilder(
                context,
                outcomes);
            IReadOnlyList<ServiceDescriptor> descriptors = builder.Services.ToArray();
            builder.Services.RemoveAll<IWorkerStandardInputStreamFactory>();
            builder.Services.AddSingleton<IWorkerStandardInputStreamFactory>(new NullStandardInputFactory());
            builder.Services.RemoveAll<IWorkerNdjsonOutputStreamFactory>();
            builder.Services.AddSingleton<IWorkerNdjsonOutputStreamFactory>(new NullNdjsonOutputFactory());
            var disposal = new DisposalReceipt();
            builder.Services.AddSingleton(_ => disposal);
            if (!acceptedRun)
            {
                IHost idleHost = builder.Build();
                _ = idleHost.Services.GetRequiredService<DisposalReceipt>();
                return new WorkerFixture(root, idleHost, descriptors, outcomes, [], null, null, disposal);
            }

            var ledger = new List<string>();
            var executor = new RecordingExecutor(ledger);
            var lease = new RecordingLease(new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual), ledger);
            ReplaceSingleton(builder.Services, (IWorkerStartupInitializer)new LedgerInitializer(ledger));
            ReplaceSingleton(builder.Services, (IWorkerTransportAvailability)new ConfiguredTransport());
            ReplaceSingleton(builder.Services, (IWorkerReadinessPublisher)new LedgerReadiness(ledger));
            ReplaceSingleton(builder.Services, (IInitialProcessingRunAcquirer)new AcceptedAcquirer(lease, ledger));
            ReplaceSingleton(builder.Services, (IWorkerPreRequestFinality)new NoOpPreRequestFinality());
            ReplaceSingleton(builder.Services, (IWorkerAcceptedRunFinality)new LedgerAcceptedFinality(ledger));
            ReplaceSingleton(builder.Services, (IProcessingRunExecutor)executor);
            ReplaceSingleton(builder.Services, (IProcessingEventReporter)NoOpProcessingEventReporter.Instance);
            var sentinel = new ResolutionSentinel(BoundaryRole.InternalWorker);
            builder.Services.RemoveAll<CountryCodeService>();
            builder.Services.AddSingleton<CountryCodeService>(_ => sentinel.Fail<CountryCodeService>());
            builder.Services.RemoveAll<IProcessingAssetRepository>();
            builder.Services.AddSingleton<IProcessingAssetRepository>(_ => sentinel.Fail<IProcessingAssetRepository>());
            IHost host = builder.Build();
            _ = host.Services.GetRequiredService<DisposalReceipt>();
            return new WorkerFixture(root, host, descriptors, outcomes, ledger, executor, sentinel, disposal);
        }

        public async ValueTask DisposeAsync()
        {
            await ((IAsyncDisposable)Host).DisposeAsync();
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class NullNdjsonOutputFactory : IWorkerNdjsonOutputStreamFactory
    {
        public Stream OpenStandardOutput() => Stream.Null;
    }

    private sealed class NullStandardInputFactory : IWorkerStandardInputStreamFactory
    {
        public Stream OpenStandardInput() => Stream.Null;
    }
}
