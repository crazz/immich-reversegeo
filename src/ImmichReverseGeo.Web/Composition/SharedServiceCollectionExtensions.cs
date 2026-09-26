using System;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ImmichReverseGeo.Web.Composition;

internal static class SharedServiceCollectionExtensions
{
    /// <summary>
    /// Registers lightweight services shared by Web, internal-worker, and run-once hosts.
    /// </summary>
    internal static IServiceCollection AddSharedComposition(
        this IServiceCollection services,
        ApplicationCompositionContext context)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);

        services.AddLogging();
        services.AddHttpClient();
        services.AddSingleton(new StorageOptions(
            context.DataDirectory,
            context.BundledDataDirectory));
        services.AddSingleton(sp => new ConfigService(
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ConfigService>>(),
            context.ConfigDirectory));
        services.AddSingleton<CountryCodeService>();
        services.AddSingleton<CityResolverProfileCatalogService>();
        services.AddSingleton<NpgsqlDataSource>(sp =>
        {
            var db = sp.GetRequiredService<ConfigService>().GetDbSettings();
            var connectionString = new NpgsqlConnectionStringBuilder
            {
                Host = db.Host,
                Port = db.Port,
                Username = db.Username,
                Password = db.Password,
                Database = db.Database,
                GssEncryptionMode = GssEncryptionMode.Disable
            };
            var builder = new NpgsqlDataSourceBuilder(connectionString.ConnectionString);
            return builder.Build();
        });

        return services;
    }
}
