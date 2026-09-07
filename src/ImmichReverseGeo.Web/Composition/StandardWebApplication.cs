using System;
using System.Collections.Generic;
using ImmichReverseGeo.Core.ApplicationRole;
using Microsoft.AspNetCore.Builder;

namespace ImmichReverseGeo.Web.Composition;

internal static class StandardWebApplication
{
    internal static WebApplicationBuilder CreateBuilder(
        DeploymentMode deploymentMode,
        IReadOnlyList<string> arguments,
        Func<string, string?> environmentVariableReader)
    {
        if (!ReferenceEquals(deploymentMode, DeploymentMode.Standard))
        {
            throw new ArgumentException("Standard Web composition requires the resolved Standard deployment mode.", nameof(deploymentMode));
        }

        return WebApplicationComposition.CreateBuilder(
            deploymentMode,
            arguments,
            environmentVariableReader,
            static (services, context) => services.AddStandardWebComposition(context));
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
