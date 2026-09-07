using System;
using System.Collections.Generic;
using System.IO;
using ImmichReverseGeo.Core.ApplicationRole;
using Role = ImmichReverseGeo.Core.ApplicationRole.ApplicationRole;

namespace ImmichReverseGeo.Web.ApplicationRole;

public static class ApplicationRoleStartup
{
    public static void Begin(
        IReadOnlyList<string> arguments,
        Func<string, string?> environmentVariableReader,
        TextWriter errorWriter,
        Action<DeploymentMode, IReadOnlyList<string>> webContinuation,
        Action<IReadOnlyList<string>> internalWorkerContinuation,
        Action<DeploymentMode, IReadOnlyList<string>> runOnceContinuation,
        Action<int> privateSelectionExitCodeSink,
        Action<int> deploymentModeExitCodeSink)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(environmentVariableReader);
        ArgumentNullException.ThrowIfNull(errorWriter);
        ArgumentNullException.ThrowIfNull(webContinuation);
        ArgumentNullException.ThrowIfNull(internalWorkerContinuation);
        ArgumentNullException.ThrowIfNull(runOnceContinuation);
        ArgumentNullException.ThrowIfNull(privateSelectionExitCodeSink);
        ArgumentNullException.ThrowIfNull(deploymentModeExitCodeSink);

        var privateSelection = ApplicationRoleSelector.Select(arguments, PublicApplicationRole.Web);

        if (privateSelection is ApplicationRoleSelectionResult.Failure privateFailure)
        {
            errorWriter.WriteLine(privateFailure.Diagnostic);
            privateSelectionExitCodeSink(2);
            return;
        }

        var privateSuccess = (ApplicationRoleSelectionResult.Success)privateSelection;

        if (ReferenceEquals(privateSuccess.Role, Role.InternalWorker))
        {
            internalWorkerContinuation(privateSuccess.Arguments);
            return;
        }

        var deploymentModeResolution = DeploymentModeResolver.Resolve(environmentVariableReader);

        if (deploymentModeResolution is DeploymentModeResolution.Failure failure)
        {
            errorWriter.WriteLine(failure.Diagnostic);
            deploymentModeExitCodeSink(2);
            return;
        }

        var mode = ((DeploymentModeResolution.Success)deploymentModeResolution).Mode;
        var publicRoleCandidate = ReferenceEquals(mode, DeploymentMode.RunOnce)
            ? PublicApplicationRole.RunOnce
            : PublicApplicationRole.Web;
        var publicSelection = ApplicationRoleSelector.Select(arguments, publicRoleCandidate);
        var publicSuccess = (ApplicationRoleSelectionResult.Success)publicSelection;

        if (ReferenceEquals(publicSuccess.Role, Role.Web))
        {
            webContinuation(mode, publicSuccess.Arguments);
            return;
        }

        runOnceContinuation(mode, publicSuccess.Arguments);
    }

    public static void Begin(
        IReadOnlyList<string> arguments,
        TextWriter errorWriter,
        Action<IReadOnlyList<string>> webContinuation,
        Action<int> exitCodeSink)
    {
        Begin(arguments, PublicApplicationRole.Web, errorWriter, webContinuation, _ => { }, exitCodeSink);
    }

    public static void Begin(
        IReadOnlyList<string> arguments,
        TextWriter errorWriter,
        Action<IReadOnlyList<string>> webContinuation,
        Action<IReadOnlyList<string>> internalWorkerContinuation,
        Action<int> exitCodeSink)
    {
        Begin(arguments, PublicApplicationRole.Web, errorWriter, webContinuation, internalWorkerContinuation, exitCodeSink);
    }

    public static void Begin(
        IReadOnlyList<string> arguments,
        PublicApplicationRole publicRoleCandidate,
        TextWriter errorWriter,
        Action<IReadOnlyList<string>> webContinuation,
        Action<int> exitCodeSink)
    {
        Begin(arguments, publicRoleCandidate, errorWriter, webContinuation, _ => { }, exitCodeSink);
    }

    public static void Begin(
        IReadOnlyList<string> arguments,
        PublicApplicationRole publicRoleCandidate,
        TextWriter errorWriter,
        Action<IReadOnlyList<string>> webContinuation,
        Action<IReadOnlyList<string>> internalWorkerContinuation,
        Action<int> exitCodeSink)
    {
        var selection = ApplicationRoleSelector.Select(arguments, publicRoleCandidate);

        if (selection is ApplicationRoleSelectionResult.Failure failure)
        {
            errorWriter.WriteLine(failure.Diagnostic);
            exitCodeSink(2);
            return;
        }

        var success = (ApplicationRoleSelectionResult.Success)selection;

        if (ReferenceEquals(success.Role, Role.Web))
        {
            webContinuation(success.Arguments);
            return;
        }

        if (ReferenceEquals(success.Role, Role.InternalWorker))
        {
            internalWorkerContinuation(success.Arguments);
            return;
        }

        if (ReferenceEquals(success.Role, Role.RunOnce))
        {
            return;
        }
    }
}
