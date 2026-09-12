using ImmichReverseGeo.Core.ApplicationRole;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Composition;
using ImmichReverseGeo.Web.ProcessingRunLocking;
using ImmichReverseGeo.Web.RunOnce;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerHost;
using ImmichReverseGeo.Web.WorkerHost.WorkerNdjsonOutput;
using ImmichReverseGeo.Web.WorkerHost.WorkerStdinRequestLoop;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ImmichReverseGeo.Tests.RunOnceDeploymentMode;

[TestClass]
[TestCategory("Change43")]
public sealed class RunOnceCompositionTests
{
    [TestMethod]
    public async Task ProductionBuilder_UsesExactExecutionAliasesAndExcludesControlPlaneGraphs()
    {
        var root = Path.Combine(Path.GetTempPath(), "immich-reversegeo-run-once-composition", Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(root);

        try
        {
            var outcomes = new WorkerProcessExitOutcomeAccumulator();
            var reads = new List<string>();
            var builder = RunOnceApplication.CreateBuilder(
                DeploymentMode.RunOnce,
                ["--ordinary-host-argument", "value"],
                name =>
                {
                    reads.Add(name);
                    return name == "DATA_DIR" ? Path.Combine(root, "data") : Path.Combine(root, "config");
                },
                TextWriter.Null,
                TextWriter.Null,
                outcomes);

            ServiceDescriptor[] descriptors = builder.Services.ToArray();
            CollectionAssert.AreEqual(new[] { "DATA_DIR", "CONFIG_DIR" }, reads);
            Assert.AreEqual(1, descriptors.Count(x => x.ServiceType == typeof(WorkerProcessExitOutcomeAccumulator)));
            Assert.AreSame(outcomes, descriptors.Single(x => x.ServiceType == typeof(WorkerProcessExitOutcomeAccumulator)).ImplementationInstance);
            AssertAlias<ProcessingRunExecutor, IProcessingRunExecutor>(descriptors);
            AssertAlias<RunOnceProcessingRunConfiguration, IProcessingRunConfiguration>(descriptors);
            AssertAlias<RunOnceProcessingAssetRepository, IProcessingAssetRepository>(descriptors);
            AssertAlias<RunOnceProcessingSkippedStore, IProcessingSkippedStore>(descriptors);
            AssertAlias<PostgresqlProcessingRunLock, IProcessingRunLock>(descriptors);
            AssertAlias<SkippedAssetsWorkerStartupInitializer, IWorkerStartupInitializer>(descriptors);
            AssertAlias<RunOnceProcessingEventReporter, IProcessingEventReporter>(descriptors);

            foreach (Type forbidden in new[]
            {
                typeof(WebApplicationBuilder),
                typeof(WebApplication),
                typeof(IDataProtectionProvider),
                typeof(ProcessingBackgroundService),
                typeof(IProcessingScheduleConfiguration),
                typeof(IProcessingWorkDetector),
                typeof(ProcessingRunCoordinator),
                typeof(ProcessingState),
                typeof(IChildProcessingRunBackend),
                typeof(IChildWorkerLauncher),
                typeof(WorkerStdinRequestSource),
                typeof(IInitialProcessingRunAcquirer),
                typeof(WorkerNdjsonEmitter),
                typeof(WorkerNdjsonProcessingEventReporter),
                typeof(IWorkerReadinessPublisher),
                typeof(IHostedService)
            })
            {
                Assert.AreEqual(0, descriptors.Count(x => x.ServiceType == forbidden), forbidden.FullName);
            }

            IHost host = builder.Build();
            await ((IAsyncDisposable)host).DisposeAsync();
            Assert.IsFalse(outcomes.HasFact, "Building and disposing an unstarted Run-once host must stay lazy.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ProductionBuilder_RejectsAnyModeOtherThanTheResolvedRunOnceSingleton()
    {
        Assert.ThrowsExactly<ArgumentException>(() => RunOnceApplication.CreateBuilder(
            DeploymentMode.Standard,
            [],
            _ => null,
            TextWriter.Null,
            TextWriter.Null,
            new WorkerProcessExitOutcomeAccumulator()));
    }

    private static void AssertAlias<TOwner, TAlias>(IReadOnlyList<ServiceDescriptor> descriptors)
    {
        ServiceDescriptor owner = descriptors.Single(x => x.ServiceType == typeof(TOwner));
        ServiceDescriptor alias = descriptors.Single(x => x.ServiceType == typeof(TAlias));
        Assert.AreEqual(ServiceLifetime.Singleton, owner.Lifetime, typeof(TOwner).FullName);
        Assert.AreEqual(ServiceLifetime.Singleton, alias.Lifetime, typeof(TAlias).FullName);
        Assert.IsNotNull(alias.ImplementationFactory, typeof(TAlias).FullName);
    }
}
