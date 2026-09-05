using System;
using ImmichReverseGeo.Core.Models;

namespace ImmichReverseGeo.Web.Services;

internal enum ProcessingRunFinalizationOrigin
{
    WorkerTerminal,
    ControlPlane
}

internal enum ProcessingRunFinalizationDisposition
{
    Committed,
    ExistingWinner,
    RejectedBeforeCommit
}

internal sealed record ProcessingRunFinalizationReceipt
{
    internal ProcessingRunFinalizationReceipt(
        ProcessingRunRequest request,
        ProcessingRunResult result,
        ProcessingRunFinalizationOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);

        if (!ReferenceEquals(result.Request, request))
        {
            throw new ArgumentException("The finalization result must retain the exact request instance.", nameof(result));
        }

        if (!Enum.IsDefined(origin))
        {
            throw new ArgumentOutOfRangeException(nameof(origin), origin, "The finalization origin must be defined.");
        }

        Request = request;
        Result = result;
        Origin = origin;
    }

    internal ProcessingRunRequest Request { get; }
    internal ProcessingRunResult Result { get; }
    internal ProcessingRunFinalizationOrigin Origin { get; }
}

internal readonly record struct ProcessingRunFinalizationAttempt(
    ProcessingRunFinalizationDisposition Disposition,
    ProcessingRunFinalizationReceipt? Receipt);
