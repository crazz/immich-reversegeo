using System;
using System.Collections.Generic;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.ApplicationRole;
using ImmichReverseGeo.Web.Composition;
using ImmichReverseGeo.Web.RunOnce;
using ImmichReverseGeo.Web.WorkerHost;

var workerErrorWriter = Console.Error;

ApplicationRoleStartup.Begin(
    args,
    Environment.GetEnvironmentVariable,
    workerErrorWriter,
    RunWebApplication,
    RunInternalWorker,
    RunOnce,
    _ => Environment.ExitCode = InternalWorkerProcess.CompleteInvalidInvocation(workerErrorWriter),
    code => Environment.ExitCode = code);

void RunInternalWorker(IReadOnlyList<string> selectedArguments)
{
    Environment.ExitCode = InternalWorkerProcess.Run(
        selectedArguments,
        workerErrorWriter,
        Environment.GetEnvironmentVariable,
        static (protocolVersion, outcomes) =>
            InternalWorkerHost.RunProductionAsync(outcomes, protocolVersion));
}

void RunWebApplication(ImmichReverseGeo.Core.ApplicationRole.DeploymentMode deploymentMode, IReadOnlyList<string> selectedArguments)
{
    if (ReferenceEquals(deploymentMode, ImmichReverseGeo.Core.ApplicationRole.DeploymentMode.Standard))
    {
        StandardWebApplication.Run(deploymentMode, selectedArguments, Environment.GetEnvironmentVariable);
        return;
    }

    WebOnlyWebApplication.Run(deploymentMode, selectedArguments, Environment.GetEnvironmentVariable);
}

void RunOnce(ImmichReverseGeo.Core.ApplicationRole.DeploymentMode deploymentMode, IReadOnlyList<string> selectedArguments)
{
    Environment.ExitCode = RunOnceProcess.Run(
        deploymentMode,
        selectedArguments,
        Console.Out,
        workerErrorWriter,
        RunOnceApplication.RunProductionAsync,
        Environment.GetEnvironmentVariable);
}
