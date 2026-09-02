using System;
using System.IO;
using ImmichReverseGeo.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace ImmichReverseGeo.Web.Composition;

internal static class WebServiceCollectionExtensions
{
    internal static IServiceCollection AddWebComposition(
        this IServiceCollection services,
        ApplicationCompositionContext context)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);

        services.AddSharedComposition(context);

        // Transitional Web dependency until Change 55 moves heavy work out of this root.
        services.AddReusableHeavyComposition();

        services.AddRazorComponents()
            .AddInteractiveServerComponents();

        var dataProtectionDirectory = Path.Combine(context.ConfigDirectory, "dataprotection-keys");
        Directory.CreateDirectory(dataProtectionDirectory);
        services.AddDataProtection()
            .SetApplicationName("ImmichReverseGeo")
            .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionDirectory));

        services.AddProcessingControlPlaneServices();
        return services;
    }
}
