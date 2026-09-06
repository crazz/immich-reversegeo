using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests;

internal sealed class AlwaysHasWorkScheduledRunGate : IScheduledRunWorkGate
{
    internal static AlwaysHasWorkScheduledRunGate Instance { get; } = new();

    public Task<bool> HasWorkAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(true);
    }
}
