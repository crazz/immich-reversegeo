using System;
using System.Threading;
using System.Threading.Tasks;

namespace ImmichReverseGeo.Web.ChildWorkerLaunching;

// Owned by one child session. This observes current process memory; it does not
// own process control and never exposes platform diagnostics to the log catalog.
internal sealed class ChildWorkingSetSampler
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(1000);
    private readonly IChildProcess _process;
    private readonly object _sampleGate = new();
    private readonly ITimer? _timer;
    private readonly TaskCompletionSource<ChildWorkingSetSummary> _final =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _finishing;
    private long? _peak;
    private long _count;
    private ChildWorkingSetUnavailable _reason;

    internal ChildWorkingSetSampler(IChildProcess process, TimeProvider clock)
    {
        _process = process;
        lock (_sampleGate)
        {
            SampleUnderGate();
        }

        try
        {
            _timer = clock.CreateTimer(static state => ((ChildWorkingSetSampler)state!).Tick(), this, Interval, Interval);
        }
        catch
        {
            lock (_sampleGate)
            {
                RetainFailure(ChildWorkingSetUnavailable.SampleFailed);
            }
        }
    }

    internal Task<ChildWorkingSetSummary> CompleteAsync()
    {
        if (Interlocked.Exchange(ref _finishing, 1) == 0)
        {
            _ = CompleteCoreAsync();
        }

        return _final.Task;
    }

    private async Task CompleteCoreAsync()
    {
        // A caller may still hold an existing session ownership lock. Neither
        // timer disposal nor joining an in-flight sample can run under that lock.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            if (_timer is not null)
            {
                await _timer.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            lock (_sampleGate)
            {
                RetainFailure(ChildWorkingSetUnavailable.SampleFailed);
            }
        }

        // The gate also joins a callback from a faulty timer implementation.
        // Every later callback observes _finishing and cannot touch the process.
        ChildWorkingSetSummary summary;
        lock (_sampleGate)
        {
            SampleUnderGate();
            summary = new(_peak, _count, _reason);
        }

        _final.TrySetResult(summary);
    }

    private void Tick()
    {
        lock (_sampleGate)
        {
            if (Volatile.Read(ref _finishing) == 0)
            {
                SampleUnderGate();
            }
        }
    }

    private void SampleUnderGate()
    {
        ChildWorkingSetObservation sample;
        try
        {
            sample = _process.ReadWorkingSet();
        }
        catch
        {
            sample = ChildWorkingSetObservation.Unavailable(ChildWorkingSetUnavailable.SampleFailed);
        }

        if (sample.Bytes is >= 0)
        {
            _peak = _peak is null ? sample.Bytes : Math.Max(_peak.Value, sample.Bytes.Value);
            if (_count < long.MaxValue)
            {
                _count++;
            }
        }
        else
        {
            RetainFailure(sample.Reason == ChildWorkingSetUnavailable.NoSample
                ? ChildWorkingSetUnavailable.SampleFailed : sample.Reason);
        }
    }

    private void RetainFailure(ChildWorkingSetUnavailable reason)
    {
        if (reason > _reason)
        {
            _reason = reason;
        }
    }
}
