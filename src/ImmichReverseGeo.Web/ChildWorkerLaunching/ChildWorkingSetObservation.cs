using System;

namespace ImmichReverseGeo.Web.ChildWorkerLaunching;

// Ordered solely for the catalog's deterministic unavailable-reason precedence.
internal enum ChildWorkingSetUnavailable
{
    NoSample, ProcessExited, SampleFailed, AccessDenied, NotSupported
}

internal readonly record struct ChildWorkingSetObservation
{
    private ChildWorkingSetObservation(long? bytes, ChildWorkingSetUnavailable reason)
    {
        Bytes = bytes;
        Reason = reason;
    }

    internal long? Bytes { get; }
    internal ChildWorkingSetUnavailable Reason { get; }

    internal static ChildWorkingSetObservation Available(long bytes) => bytes >= 0
        ? new(bytes, ChildWorkingSetUnavailable.NoSample)
        : Unavailable(ChildWorkingSetUnavailable.SampleFailed);

    internal static ChildWorkingSetObservation Unavailable(ChildWorkingSetUnavailable reason) =>
        new(null, Enum.IsDefined(reason) ? reason : ChildWorkingSetUnavailable.SampleFailed);
}

internal readonly record struct ChildWorkingSetSummary(
    long? PeakBytes, long SuccessfulSamples, ChildWorkingSetUnavailable UnavailableReason)
{
    internal static ChildWorkingSetSummary NoSample => new(null, 0, ChildWorkingSetUnavailable.NoSample);
}
