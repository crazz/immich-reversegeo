using System;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;

namespace ImmichReverseGeo.Web.Services;

internal interface IProcessingWorkDetector
{
    Task<ProcessingWorkDetectionResult> DetectAsync(
        ProcessingWorkDetectionRequest request,
        CancellationToken cancellationToken);
}

internal enum ProcessingWorkDetectionPurpose
{
    ScheduledLaunch
}

internal enum ProcessingWorkDetectionCoverage
{
    FullEligibility
}

internal enum ProcessingWorkDetectorKind
{
    CountBacked,
    Existence
}

internal sealed record ProcessingWorkDetectionSnapshot
{
    public static ProcessingWorkDetectionSnapshot Current { get; } = new(
        ProcessingWorkDetectionPurpose.ScheduledLaunch,
        ProcessingWorkDetectionCoverage.FullEligibility);

    public ProcessingWorkDetectionSnapshot(
        ProcessingWorkDetectionPurpose purpose,
        ProcessingWorkDetectionCoverage coverage)
    {
        if (purpose != ProcessingWorkDetectionPurpose.ScheduledLaunch)
        {
            throw new ArgumentOutOfRangeException(nameof(purpose));
        }
        if (coverage != ProcessingWorkDetectionCoverage.FullEligibility)
        {
            throw new ArgumentOutOfRangeException(nameof(coverage));
        }
        Purpose = purpose;
        Coverage = coverage;
    }

    public ProcessingWorkDetectionPurpose Purpose { get; }
    public ProcessingWorkDetectionCoverage Coverage { get; }
}

internal sealed record ProcessingWorkDetectionRequest
{
    public ProcessingWorkDetectionRequest(
        ProcessingRunTrigger trigger,
        ProcessingWorkDetectionSnapshot snapshot)
    {
        if (trigger != ProcessingRunTrigger.Scheduled)
        {
            throw new ArgumentOutOfRangeException(nameof(trigger));
        }
        ArgumentNullException.ThrowIfNull(snapshot);
        Trigger = trigger;
        Snapshot = snapshot;
    }

    public ProcessingRunTrigger Trigger { get; }
    public ProcessingWorkDetectionSnapshot Snapshot { get; }
}

internal sealed record ProcessingWorkDetectionDiagnostics
{
    public ProcessingWorkDetectionDiagnostics(
        ProcessingWorkDetectorKind implementationKind,
        ProcessingWorkDetectionCoverage coverage,
        bool usedFallback)
    {
        if (implementationKind is not (ProcessingWorkDetectorKind.CountBacked or ProcessingWorkDetectorKind.Existence))
        {
            throw new ArgumentOutOfRangeException(nameof(implementationKind));
        }
        if (coverage != ProcessingWorkDetectionCoverage.FullEligibility)
        {
            throw new ArgumentOutOfRangeException(nameof(coverage));
        }
        ImplementationKind = implementationKind;
        Coverage = coverage;
        UsedFallback = usedFallback;
    }

    public ProcessingWorkDetectorKind ImplementationKind { get; }
    public ProcessingWorkDetectionCoverage Coverage { get; }
    public bool UsedFallback { get; }
}

internal sealed record ProcessingWorkDetectionResult
{
    public ProcessingWorkDetectionResult(bool hasWork, ProcessingWorkDetectionDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        HasWork = hasWork;
        Diagnostics = diagnostics;
    }

    public bool HasWork { get; }
    public ProcessingWorkDetectionDiagnostics Diagnostics { get; }
}
