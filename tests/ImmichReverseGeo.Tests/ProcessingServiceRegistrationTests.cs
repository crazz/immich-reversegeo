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
    public async Task AddProcessingServices_ExecutorCollaboratorAndHostedAliasesPreserveReferenceIdentity()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILogger<ProcessingBackgroundService>>(NullLogger<ProcessingBackgroundService>.Instance);
        services.AddSingleton((ConfigService)RuntimeHelpers.GetUninitializedObject(typeof(ConfigService)));
        services.AddSingleton((AdministrativeAreaResolverService)RuntimeHelpers.GetUninitializedObject(typeof(AdministrativeAreaResolverService)));
        services.AddSingleton((ImmichDbRepository)RuntimeHelpers.GetUninitializedObject(typeof(ImmichDbRepository)));
        var places = (ImmichReverseGeo.Overture.Services.OverturePlacesService)RuntimeHelpers.GetUninitializedObject(typeof(ImmichReverseGeo.Overture.Services.OverturePlacesService));
        services.AddSingleton(places);
        services.AddSingleton((SkippedAssetsRepository)RuntimeHelpers.GetUninitializedObject(typeof(SkippedAssetsRepository)));
        services.AddProcessingServices();
        using var provider = services.BuildServiceProvider();

        var state = provider.GetRequiredService<ProcessingState>();
        var sameState = provider.GetRequiredService<ProcessingState>();
        var adapter = provider.GetRequiredService<ProcessingStateEventReporter>();
        var reporter = provider.GetRequiredService<IProcessingEventReporter>();

        Assert.AreSame(state, sameState);
        Assert.AreSame(adapter, reporter);
        Assert.AreSame(provider.GetRequiredService<ConfigService>(), provider.GetRequiredService<IProcessingRunConfiguration>());
        Assert.AreSame(provider.GetRequiredService<ImmichDbRepository>(), provider.GetRequiredService<IProcessingAssetRepository>());
        Assert.AreSame(provider.GetRequiredService<SkippedAssetsRepository>(), provider.GetRequiredService<IProcessingSkippedStore>());
        Assert.AreSame(provider.GetRequiredService<AdministrativeAreaResolverService>(), provider.GetRequiredService<IProcessingAdministrativeResolver>());
        var infrastructureAdapter = provider.GetRequiredService<ProcessingInfrastructureLookup>();
        Assert.AreSame(infrastructureAdapter, provider.GetRequiredService<IProcessingInfrastructureLookup>());
        var wrappedPlacesField = typeof(ProcessingInfrastructureLookup)
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Single(field => field.FieldType == typeof(ImmichReverseGeo.Overture.Services.OverturePlacesService));
        Assert.AreSame(provider.GetRequiredService<ImmichReverseGeo.Overture.Services.OverturePlacesService>(), wrappedPlacesField.GetValue(infrastructureAdapter));
        Assert.AreSame(places, wrappedPlacesField.GetValue(infrastructureAdapter));
        Assert.AreSame(provider.GetRequiredService<ProcessingRunDelay>(), provider.GetRequiredService<IProcessingRunDelay>());
        Assert.AreSame(TimeProvider.System, provider.GetRequiredService<TimeProvider>());
        Assert.AreSame(provider.GetRequiredService<ProcessingRunExecutor>(), provider.GetRequiredService<IProcessingRunExecutor>());
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
