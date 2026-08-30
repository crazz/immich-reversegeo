using System;

namespace ImmichReverseGeo.Core.Models;

/// <summary>
/// Identifies one accepted processing run and the source that invoked it.
/// </summary>
public sealed record ProcessingRunRequest
{
    /// <summary>
    /// Gets the stable, non-empty identity of the accepted processing run.
    /// </summary>
    public Guid RunId { get; }

    /// <summary>
    /// Gets the defined source that invoked the processing run.
    /// </summary>
    public ProcessingRunTrigger Trigger { get; }

    /// <summary>
    /// Initializes a processing run request with a stable identity and defined trigger.
    /// </summary>
    /// <param name="runId">The non-empty identity of the accepted processing run.</param>
    /// <param name="trigger">The source that invoked the processing run.</param>
    /// <exception cref="ArgumentException"><paramref name="runId"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="trigger"/> is undefined.</exception>
    public ProcessingRunRequest(Guid runId, ProcessingRunTrigger trigger)
    {
        if (runId == Guid.Empty)
        {
            throw new ArgumentException("A processing run ID must not be empty.", nameof(runId));
        }

        if (!Enum.IsDefined(trigger))
        {
            throw new ArgumentOutOfRangeException(nameof(trigger), trigger, "The processing run trigger must be defined.");
        }

        RunId = runId;
        Trigger = trigger;
    }
}
