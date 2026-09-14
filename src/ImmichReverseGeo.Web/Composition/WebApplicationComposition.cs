using System;
using System.Collections.Generic;
using System.Linq;
using ImmichReverseGeo.Core.ApplicationRole;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;

namespace ImmichReverseGeo.Web.Composition;

internal static class WebApplicationComposition
{
    internal static WebApplicationBuilder CreateBuilder(
        DeploymentMode deploymentMode,
        IReadOnlyList<string> arguments,
        Func<string, string?> environmentVariableReader,
        Action<Microsoft.Extensions.DependencyInjection.IServiceCollection, ApplicationCompositionContext> addComposition)
    {
        ArgumentNullException.ThrowIfNull(deploymentMode);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(environmentVariableReader);
        ArgumentNullException.ThrowIfNull(addComposition);

        var builder = WebApplication.CreateBuilder(arguments.ToArray());
        var environment = builder.Environment.IsDevelopment()
            ? CompositionEnvironment.Development
            : CompositionEnvironment.Production;
        var context = ApplicationCompositionContext.Create(
            environment,
            builder.Environment.ContentRootPath,
            environmentVariableReader("DATA_DIR"),
            environmentVariableReader("CONFIG_DIR"),
            deploymentMode);

        addComposition(builder.Services, context);
        return builder;
    }

    internal static WebApplication Build(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var app = builder.Build();

        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error");
        }

        app.UseAntiforgery();
        app.MapStaticAssets();
        app.MapRazorComponents<ImmichReverseGeo.Web.Components.App>()
            .AddInteractiveServerRenderMode();
        return app;
    }

}
