using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using ImmichReverseGeo.Core.ApplicationRole;

namespace ImmichReverseGeo.Web.WorkerCommandInvocation;

/// <summary>
/// Describes immutable, shell-free child-process start intent.
/// </summary>
[DebuggerDisplay("Child process start descriptor.")]
internal sealed class ChildProcessStartDescriptor
{
    internal ChildProcessStartDescriptor(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        ChildProcessEnvironmentPolicy environmentPolicy)
    {
        ArgumentNullException.ThrowIfNull(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(workingDirectory);

        var copiedArguments = new string[arguments.Count];
        for (var index = 0; index < arguments.Count; index++)
        {
            copiedArguments[index] = arguments[index] ?? throw new ArgumentNullException(nameof(arguments));
        }

        ExecutablePath = executablePath;
        Arguments = new ReadOnlyCollection<string>(copiedArguments);
        WorkingDirectory = workingDirectory;
        EnvironmentPolicy = environmentPolicy;
    }

    internal string ExecutablePath { get; }

    internal IReadOnlyList<string> Arguments { get; }

    internal string WorkingDirectory { get; }

    internal ChildProcessEnvironmentPolicy EnvironmentPolicy { get; }

    internal bool RedirectStandardInput { get; } = true;

    internal bool RedirectStandardOutput { get; } = true;

    internal bool RedirectStandardError { get; } = true;

    internal bool UseShellExecute { get; } = false;

    internal bool CreateNoWindow { get; } = true;

    public override string ToString()
    {
        return "Child process start descriptor.";
    }
}

internal enum ChildProcessEnvironmentPolicy
{
    InheritCurrent,
    InheritCurrentAndRemoveReservedProtocolVersion
}

internal static class ChildProcessEnvironmentPolicyDetails
{
    internal const string ReservedProtocolVersionVariable = "IMMICH_REVERSEGEO_INTERNAL_WORKER_PROTOCOL_VERSION";

    internal static bool RemovesReservedProtocolVersion(ChildProcessEnvironmentPolicy policy)
    {
        return policy == ChildProcessEnvironmentPolicy.InheritCurrentAndRemoveReservedProtocolVersion;
    }
}

/// <summary>
/// A production-only validated worker invocation wrapping general start intent.
/// </summary>
[DebuggerDisplay("Worker command invocation.")]
internal sealed class WorkerCommandInvocation
{
    internal const string TrustedWebAssemblyIdentity = "ImmichReverseGeo.Web";

    private WorkerCommandInvocation(ChildProcessStartDescriptor descriptor)
    {
        Descriptor = descriptor;
    }

    internal ChildProcessStartDescriptor Descriptor { get; }

    public override string ToString()
    {
        return "Worker command invocation.";
    }

    internal static WorkerCommandInvocationResolution Resolve(WorkerCommandRuntimeFacts? facts)
    {
        if (facts is null || facts.HasObservationFailure)
        {
            return WorkerCommandInvocationResolution.Fail(WorkerCommandInvocationFailureCategory.RuntimeObservationFailure);
        }

        if (string.IsNullOrEmpty(facts.ProcessPath))
        {
            return WorkerCommandInvocationResolution.Fail(WorkerCommandInvocationFailureCategory.ProcessPathUnavailable);
        }

        if (!facts.PathSemantics.IsAbsolute(facts.ProcessPath))
        {
            return WorkerCommandInvocationResolution.Fail(WorkerCommandInvocationFailureCategory.RequiredPathInvalid);
        }

        if (facts.ProcessTarget.Kind == WorkerTargetObservationKind.Missing)
        {
            return WorkerCommandInvocationResolution.Fail(WorkerCommandInvocationFailureCategory.RequiredTargetMissing);
        }

        if (facts.ProcessTarget.Kind is not WorkerTargetObservationKind.File and not WorkerTargetObservationKind.Ambiguous)
        {
            return WorkerCommandInvocationResolution.Fail(WorkerCommandInvocationFailureCategory.RequiredTargetWrongType);
        }

        if (string.IsNullOrEmpty(facts.WorkingDirectory))
        {
            return WorkerCommandInvocationResolution.Fail(WorkerCommandInvocationFailureCategory.WorkingDirectoryUnavailable);
        }

        if (!facts.PathSemantics.IsAbsolute(facts.WorkingDirectory))
        {
            return WorkerCommandInvocationResolution.Fail(WorkerCommandInvocationFailureCategory.WorkingDirectoryInvalid);
        }

        if (facts.WorkingDirectoryTarget.Kind == WorkerTargetObservationKind.Missing)
        {
            return WorkerCommandInvocationResolution.Fail(WorkerCommandInvocationFailureCategory.WorkingDirectoryMissing);
        }

        if (facts.WorkingDirectoryTarget.Kind is not WorkerTargetObservationKind.Directory and not WorkerTargetObservationKind.Ambiguous)
        {
            return WorkerCommandInvocationResolution.Fail(WorkerCommandInvocationFailureCategory.WorkingDirectoryWrongType);
        }

        if (!string.Equals(facts.KnownWebApplicationIdentity, TrustedWebAssemblyIdentity, StringComparison.Ordinal)
            || !string.Equals(facts.EntryAssemblySimpleIdentity, TrustedWebAssemblyIdentity, StringComparison.Ordinal))
        {
            return WorkerCommandInvocationResolution.Fail(WorkerCommandInvocationFailureCategory.EntryApplicationIdentityMismatch);
        }

        if (string.IsNullOrEmpty(facts.EntryAssemblyLocation) || !facts.PathSemantics.IsAbsolute(facts.EntryAssemblyLocation))
        {
            return WorkerCommandInvocationResolution.Fail(WorkerCommandInvocationFailureCategory.RequiredPathInvalid);
        }

        if (facts.EntryAssemblyTarget.Kind == WorkerTargetObservationKind.Missing)
        {
            return WorkerCommandInvocationResolution.Fail(WorkerCommandInvocationFailureCategory.RequiredTargetMissing);
        }

        if (facts.EntryAssemblyTarget.Kind is not WorkerTargetObservationKind.File and not WorkerTargetObservationKind.Ambiguous)
        {
            return WorkerCommandInvocationResolution.Fail(WorkerCommandInvocationFailureCategory.RequiredTargetWrongType);
        }

        if (!facts.PathSemantics.FileNameEquals(facts.EntryAssemblyLocation, $"{TrustedWebAssemblyIdentity}.dll"))
        {
            return WorkerCommandInvocationResolution.Fail(WorkerCommandInvocationFailureCategory.EntryApplicationIdentityMismatch);
        }

        if (facts.HasAmbiguousObservation)
        {
            return WorkerCommandInvocationResolution.Fail(WorkerCommandInvocationFailureCategory.AmbiguousLayout);
        }

        if (facts.PathSemantics.IsDotnetHost(facts.ProcessPath))
        {
            return Succeed(facts, new[] { facts.EntryAssemblyLocation, ApplicationRoleSelector.InternalWorkerSelector });
        }

        if (facts.PathSemantics.IsWebAppHost(facts.ProcessPath, TrustedWebAssemblyIdentity))
        {
            return Succeed(facts, new[] { ApplicationRoleSelector.InternalWorkerSelector });
        }

        if (facts.PathSemantics.IsWebAppHostCandidate(facts.ProcessPath, TrustedWebAssemblyIdentity))
        {
            return WorkerCommandInvocationResolution.Fail(WorkerCommandInvocationFailureCategory.ApphostExecutableIdentityMismatch);
        }

        return WorkerCommandInvocationResolution.Fail(WorkerCommandInvocationFailureCategory.UnsupportedLayout);
    }

    private static WorkerCommandInvocationResolution.Success Succeed(WorkerCommandRuntimeFacts facts, IReadOnlyList<string> arguments)
    {
        var descriptor = new ChildProcessStartDescriptor(
            facts.ProcessPath!,
            arguments,
            facts.WorkingDirectory!,
            ChildProcessEnvironmentPolicy.InheritCurrentAndRemoveReservedProtocolVersion);
        return WorkerCommandInvocationResolution.Succeed(new WorkerCommandInvocation(descriptor));
    }
}

internal abstract class WorkerCommandInvocationResolution
{
    private WorkerCommandInvocationResolution()
    {
    }

    internal static Success Succeed(WorkerCommandInvocation invocation)
    {
        return new Success(invocation);
    }

    internal static Failure Fail(WorkerCommandInvocationFailureCategory category)
    {
        return new Failure(category);
    }

    internal sealed class Success : WorkerCommandInvocationResolution
    {
        internal Success(WorkerCommandInvocation invocation)
        {
            Invocation = invocation;
        }

        internal WorkerCommandInvocation Invocation { get; }

        public override string ToString()
        {
            return "Worker command invocation resolved.";
        }
    }

    internal sealed class Failure : WorkerCommandInvocationResolution
    {
        internal Failure(WorkerCommandInvocationFailureCategory category)
        {
            Category = category;
        }

        internal WorkerCommandInvocationFailureCategory Category { get; }

        internal string Code => WorkerCommandInvocationFailureDetails.Code(Category);

        internal string Remediation => WorkerCommandInvocationFailureDetails.Remediation;

        public override string ToString()
        {
            return "Worker command invocation resolution failed.";
        }
    }
}

internal enum WorkerCommandInvocationFailureCategory
{
    RuntimeObservationFailure,
    ProcessPathUnavailable,
    RequiredPathInvalid,
    RequiredTargetMissing,
    RequiredTargetWrongType,
    WorkingDirectoryUnavailable,
    WorkingDirectoryInvalid,
    WorkingDirectoryMissing,
    WorkingDirectoryWrongType,
    EntryApplicationIdentityMismatch,
    ApphostExecutableIdentityMismatch,
    UnsupportedLayout,
    AmbiguousLayout
}

internal static class WorkerCommandInvocationFailureDetails
{
    internal const string Remediation = "Correct the required runtime layout fact and retry resolution.";

    internal static string Code(WorkerCommandInvocationFailureCategory category)
    {
        return category switch
        {
            WorkerCommandInvocationFailureCategory.RuntimeObservationFailure => "runtime-observation-failure",
            WorkerCommandInvocationFailureCategory.ProcessPathUnavailable => "process-path-unavailable",
            WorkerCommandInvocationFailureCategory.RequiredPathInvalid => "required-path-invalid",
            WorkerCommandInvocationFailureCategory.RequiredTargetMissing => "required-target-missing",
            WorkerCommandInvocationFailureCategory.RequiredTargetWrongType => "required-target-wrong-type",
            WorkerCommandInvocationFailureCategory.WorkingDirectoryUnavailable => "working-directory-unavailable",
            WorkerCommandInvocationFailureCategory.WorkingDirectoryInvalid => "working-directory-invalid",
            WorkerCommandInvocationFailureCategory.WorkingDirectoryMissing => "working-directory-missing",
            WorkerCommandInvocationFailureCategory.WorkingDirectoryWrongType => "working-directory-wrong-type",
            WorkerCommandInvocationFailureCategory.EntryApplicationIdentityMismatch => "entry-application-identity-mismatch",
            WorkerCommandInvocationFailureCategory.ApphostExecutableIdentityMismatch => "apphost-executable-identity-mismatch",
            WorkerCommandInvocationFailureCategory.UnsupportedLayout => "unsupported-layout",
            WorkerCommandInvocationFailureCategory.AmbiguousLayout => "ambiguous-layout",
            _ => throw new InvalidOperationException("Unknown worker command invocation failure category.")
        };
    }

}
