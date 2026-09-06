using System;
using System.Threading;
using System.Threading.Tasks;

namespace ImmichReverseGeo.Web.Services;

internal interface IScheduledRunWorkGate
{
    Task<bool> HasWorkAsync(CancellationToken cancellationToken);
}

internal sealed class CountBackedScheduledRunWorkGate : IScheduledRunWorkGate
{
    private readonly Func<CancellationToken, Task<long>> _getUnprocessedCount;

    public CountBackedScheduledRunWorkGate(Func<CancellationToken, Task<long>> getUnprocessedCount)
    {
        _getUnprocessedCount = getUnprocessedCount ?? throw new ArgumentNullException(nameof(getUnprocessedCount));
    }

    public async Task<bool> HasWorkAsync(CancellationToken cancellationToken)
    {
        // This Web-side count is advisory; an eligible worker repeats the authoritative
        // count under its own advisory lock before processing.
        long unprocessedCount = await _getUnprocessedCount(cancellationToken);
        return unprocessedCount > 0;
    }
}
