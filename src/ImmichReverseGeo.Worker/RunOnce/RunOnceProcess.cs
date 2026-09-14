using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.ApplicationRole;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;

namespace ImmichReverseGeo.Web.RunOnce;

internal static class RunOnceProcess
{
    internal static int Run(
        DeploymentMode deploymentMode,
        IReadOnlyList<string> selectedArguments,
        TextWriter standardOutput,
        TextWriter standardError,
        Func<DeploymentMode, IReadOnlyList<string>, Func<string, string?>, TextWriter, TextWriter, WorkerProcessExitOutcomeAccumulator, Task<int>> runAsync,
        Func<string, string?> environmentVariableReader)
    {
        ArgumentNullException.ThrowIfNull(deploymentMode);
        ArgumentNullException.ThrowIfNull(selectedArguments);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);
        ArgumentNullException.ThrowIfNull(runAsync);
        ArgumentNullException.ThrowIfNull(environmentVariableReader);

        var outcomes = new WorkerProcessExitOutcomeAccumulator();
        try
        {
            runAsync(
                deploymentMode,
                selectedArguments,
                environmentVariableReader,
                standardOutput,
                standardError,
                outcomes).GetAwaiter().GetResult();
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch
        {
            outcomes.Add(WorkerProcessExitFact.StartupInfrastructure());
        }

        if (!outcomes.HasFact)
        {
            outcomes.Add(WorkerProcessExitFact.StartupInfrastructure());
        }

        return RunOnceProcessExitBoundary.Complete(outcomes.Fact, standardError);
    }
}

internal static class RunOnceProcessExitBoundary
{
    internal const string FinalSummaryMarker = "run-once-exit-summary";
    internal const int MaximumLength = 128;

    internal static int Complete(WorkerProcessExitFact fact, TextWriter standardError)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentNullException.ThrowIfNull(standardError);

        if (fact.ExitCode != WorkerProcessExitCodes.Completed)
        {
            string summary = $"{FinalSummaryMarker} outcome={fact.Diagnostic.Token} code={fact.ExitCode}";
            if (summary.Length > MaximumLength)
            {
                throw new InvalidOperationException("The predefined Run-once exit diagnostic exceeds its safe bound.");
            }

            try
            {
                standardError.WriteLine(summary);
                standardError.Flush();
            }
            catch (OutOfMemoryException)
            {
                throw;
            }
            catch
            {
            }
        }

        return fact.ExitCode;
    }
}
