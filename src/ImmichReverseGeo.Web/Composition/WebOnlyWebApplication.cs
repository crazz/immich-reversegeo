using System;
using System.Collections.Generic;
using ImmichReverseGeo.Core.ApplicationRole;
using Microsoft.AspNetCore.Builder;

namespace ImmichReverseGeo.Web.Composition;

internal static class WebOnlyWebApplication
{
    internal static WebApplicationBuilder CreateBuilder(
        DeploymentMode deploymentMode,
        IReadOnlyList<string> arguments,
        Func<string, string?> environmentVariableReader)
    {
        if (!ReferenceEquals(deploymentMode, DeploymentMode.WebOnly))
        {
            throw new ArgumentException("Web-only composition requires the resolved Web-only deployment mode.", nameof(deploymentMode));
        }

        return WebApplicationComposition.CreateBuilder(
            deploymentMode,
            arguments,
            environmentVariableReader,
            static (services, context) => services.AddWebOnlyWebComposition(context));
    }

    internal static void Run(
        DeploymentMode deploymentMode,
        IReadOnlyList<string> arguments,
        Func<string, string?> environmentVariableReader)
    {
        var builder = CreateBuilder(deploymentMode, arguments, environmentVariableReader);
        WebApplicationComposition.Build(builder).Run();
    }
}
