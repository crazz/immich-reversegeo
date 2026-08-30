namespace ImmichReverseGeo.Core.Models;

/// <summary>
/// Identifies how an accepted processing run was invoked.
/// </summary>
public enum ProcessingRunTrigger
{
    Manual,
    Scheduled,
    RunOnce
}
