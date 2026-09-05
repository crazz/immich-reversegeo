using System;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using Microsoft.Extensions.Hosting;

namespace ImmichReverseGeo.Web.Services;

internal static class WorkerHostShutdownBudget
{
    internal static readonly TimeSpan CleanupReserve = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan Minimum = ChildWorkerCancellationPolicy.Grace + CleanupReserve;
    internal static readonly TimeSpan Maximum = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    internal const string ValidationMessage =
        "Host shutdown timeout must be at least 30 seconds and fit the host timer range to allow worker cancellation and resource cleanup.";

    internal static bool IsValid(HostOptions options)
    {
        return options.ShutdownTimeout >= Minimum && options.ShutdownTimeout <= Maximum;
    }
}
