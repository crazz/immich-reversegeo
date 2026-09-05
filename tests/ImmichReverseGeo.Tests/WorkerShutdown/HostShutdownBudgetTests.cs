using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ImmichReverseGeo.Tests.WorkerShutdown;

[TestClass]
[TestCategory("Change29")]
public class HostShutdownBudgetTests
{
    [TestMethod]
    [DataRow(-1L)]
    [DataRow(0L)]
    [DataRow(29999L)]
    [DataRow(4294967295L)]
    public async Task InvalidBudget_FailsBeforeExecutorResolutionOrAdmission(long milliseconds)
    {
        var executorResolutions = 0;
        var services = CreateServices(() =>
        {
            executorResolutions++;
            return new UnexpectedExecutor();
        });
        services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromMilliseconds(milliseconds));
        await using var provider = services.BuildServiceProvider();

        var failure = Assert.ThrowsExactly<OptionsValidationException>(() =>
            provider.GetRequiredService<ProcessingRunCoordinator>());

        CollectionAssert.AreEqual(new[] { WorkerHostShutdownBudget.ValidationMessage }, failure.Failures.ToArray());
        Assert.AreEqual(0, executorResolutions);
        Assert.IsFalse(provider.GetRequiredService<ProcessingState>().IsRunning);
    }

    [TestMethod]
    [DataRow(30000L)]
    [DataRow(60000L)]
    [DataRow(4294967294L)]
    public async Task ValidBudget_IsPreservedAndAliasesKeepOneLifecycleOwner(long milliseconds)
    {
        var services = CreateServices(() => new UnexpectedExecutor());
        services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromMilliseconds(milliseconds));
        await using var provider = services.BuildServiceProvider();

        var coordinator = provider.GetRequiredService<ProcessingRunCoordinator>();
        var hostedDescriptors = services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)).ToArray();

        Assert.AreEqual(TimeSpan.FromMilliseconds(milliseconds), provider.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout);
        Assert.AreSame(coordinator, provider.GetRequiredService<IManualProcessingRunCoordinator>());
        Assert.AreSame(coordinator, provider.GetRequiredService<IScheduledRunTrigger>());
        Assert.AreEqual(2, hostedDescriptors.Length);
        Assert.AreSame(coordinator, hostedDescriptors[0].ImplementationFactory!(provider));
        Assert.AreEqual(typeof(ProcessingBackgroundService), hostedDescriptors[1].ImplementationFactory!.Method.ReturnType);
    }

    [TestMethod]
    public async Task GenericHostDefaultBudget_IsAcceptedWithoutAnOverride()
    {
        var services = CreateServices(() => new UnexpectedExecutor());
        await using var provider = services.BuildServiceProvider();

        Assert.AreEqual(TimeSpan.FromSeconds(30), provider.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout);
        Assert.IsNotNull(provider.GetRequiredService<ProcessingRunCoordinator>());
    }

    private static ServiceCollection CreateServices(Func<IProcessingRunExecutor> executorFactory)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IProcessingRunExecutor>(_ => executorFactory());
        services.AddProcessingControlPlaneServices();
        return services;
    }

    private sealed class UnexpectedExecutor : IProcessingRunExecutor
    {
        public Task<ProcessingRunResult> ExecuteAsync(ProcessingRunRequest request, IProcessingEventReporter reporter, CancellationToken cancellationToken)
        {
            throw new AssertFailedException("Budget validation must not dispatch processing.");
        }
    }
}
