using System;
using System.Threading;
using System.Threading.Tasks;

namespace ImmichReverseGeo.Web.Services;

public static class ScheduledRunWaiter
{
    private static readonly TimeSpan MaximumDelay = TimeSpan.FromDays(1);

    public static async Task WaitUntilAsync(
        DateTimeOffset scheduledUtc,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = scheduledUtc - timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            var delay = remaining < MaximumDelay ? remaining : MaximumDelay;
            await Task.Delay(delay, timeProvider, cancellationToken);
        }
    }
}
