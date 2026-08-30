using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.CompilerServices;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class ProcessingServiceRegistrationTests
{
    [TestMethod]
    public async Task ProductionRegistrations_UseOneStateAndOneSessionCorrelationOwner()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILogger<ProcessingBackgroundService>>(NullLogger<ProcessingBackgroundService>.Instance);
        services.AddSingleton((ConfigService)RuntimeHelpers.GetUninitializedObject(typeof(ConfigService)));
        services.AddSingleton((AdministrativeAreaResolverService)RuntimeHelpers.GetUninitializedObject(typeof(AdministrativeAreaResolverService)));
        services.AddSingleton((ImmichDbRepository)RuntimeHelpers.GetUninitializedObject(typeof(ImmichDbRepository)));
        services.AddSingleton((ImmichReverseGeo.Overture.Services.OverturePlacesService)RuntimeHelpers.GetUninitializedObject(typeof(ImmichReverseGeo.Overture.Services.OverturePlacesService)));
        services.AddSingleton((SkippedAssetsRepository)RuntimeHelpers.GetUninitializedObject(typeof(SkippedAssetsRepository)));
        services.AddProcessingServices();
        using var provider = services.BuildServiceProvider();

        var state = provider.GetRequiredService<ProcessingState>();
        var sameState = provider.GetRequiredService<ProcessingState>();
        var adapter = provider.GetRequiredService<ProcessingStateEventReporter>();
        var reporter = provider.GetRequiredService<IProcessingEventReporter>();

        Assert.AreSame(state, sameState);
        Assert.AreSame(adapter, reporter);
        Assert.AreEqual(1, provider.GetServices<ProcessingStateEventReporter>().Count());

        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual);
        Assert.IsTrue(adapter.Arm(request));
        var startedAtUtc = DateTimeOffset.UtcNow;
        var session = await reporter.OpenRunAsync(request, startedAtUtc);
        await session.DetermineEligibilityAsync(3);

        Assert.AreEqual(3L, state.TotalUnprocessed);
        Assert.IsFalse(adapter.Arm(new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Scheduled)));

        await session.FinishAsync(new ProcessingRunResult(request, startedAtUtc, DateTimeOffset.UtcNow, 0, 0, 0, 0, ProcessingRunOutcome.Completed, null));
        var concreteService = provider.GetRequiredService<ProcessingBackgroundService>();
        var hostedService = provider.GetRequiredService<IHostedService>();
        Assert.AreSame(concreteService, hostedService);
        Assert.AreEqual(1, services.Count(descriptor => descriptor.ServiceType == typeof(IHostedService)));
    }
}
