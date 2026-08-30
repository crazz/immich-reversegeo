using ImmichReverseGeo.Core.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ImmichReverseGeo.Web.Services;

internal static class ProcessingServiceRegistration
{
    internal static IServiceCollection AddProcessingServices(this IServiceCollection services)
    {
        services.AddSingleton<ProcessingState>();
        services.AddSingleton<ProcessingStateEventReporter>();
        services.AddSingleton<IProcessingEventReporter>(sp => sp.GetRequiredService<ProcessingStateEventReporter>());
        services.AddSingleton<ProcessingBackgroundService>();
        services.AddHostedService(sp => sp.GetRequiredService<ProcessingBackgroundService>());
        return services;
    }
}
