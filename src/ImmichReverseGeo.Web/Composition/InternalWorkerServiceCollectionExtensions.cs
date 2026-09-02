using System;
using Microsoft.Extensions.DependencyInjection;

namespace ImmichReverseGeo.Web.Composition;

/// <summary>
/// Registers the builder-neutral execution graph consumed by the future internal-worker host.
/// </summary>
internal static class InternalWorkerServiceCollectionExtensions
{
    internal static IServiceCollection AddInternalWorkerComposition(
        this IServiceCollection services,
        ApplicationCompositionContext context)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);

        services.AddSharedComposition(context);
        services.AddReusableHeavyComposition();
        return services;
    }
}
