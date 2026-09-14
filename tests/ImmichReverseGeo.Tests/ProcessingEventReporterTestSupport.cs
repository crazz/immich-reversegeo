using System.Collections.Concurrent;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;

namespace ImmichReverseGeo.Tests;

internal sealed class RecordingProcessingEventReporter : ProcessingEventReporter
{
    private readonly object _sync = new();
    private readonly List<ProcessingEvent> _events = [];
    private readonly List<ProcessingEvent> _attempts = [];
    private SemaphoreSlim? _capacity;
    public Func<ProcessingEvent, CancellationToken, ValueTask>? BeforeAcceptAsync { get; set; }
    public Func<ProcessingEvent, CancellationToken, ValueTask>? AfterAcceptAsync { get; set; }
    public Func<ProcessingEvent, Exception?>? FailureFactory { get; set; }

    public IReadOnlyList<ProcessingEvent> EventsFor(ProcessingRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_sync)
        {
            return _events.Where(processingEvent => ReferenceEquals(processingEvent.Request, request)).ToArray();
        }
    }

    public void SetCapacity(int capacity)
    {
        if (capacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = new SemaphoreSlim(capacity, Math.Max(1, capacity));
    }

    public void ReleaseCapacity()
    {
        _capacity?.Release();
    }

    public IReadOnlyList<ProcessingEvent> Attempts
    {
        get
        {
            lock (_sync)
            {
                return _attempts.ToArray();
            }
        }
    }

    public IReadOnlyList<ProcessingEvent> Events
    {
        get
        {
            lock (_sync)
            {
                return _events.ToArray();
            }
        }
    }

    protected override async ValueTask AcceptAsync(ProcessingEvent processingEvent, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _attempts.Add(processingEvent);
        }

        if (_capacity is not null)
        {
            await _capacity.WaitAsync(cancellationToken);
        }

        if (BeforeAcceptAsync is not null)
        {
            await BeforeAcceptAsync(processingEvent, cancellationToken);
        }

        var failure = FailureFactory?.Invoke(processingEvent);
        if (failure is not null)
        {
            throw failure;
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _events.Add(processingEvent);
        }

        if (AfterAcceptAsync is not null)
        {
            await AfterAcceptAsync(processingEvent, cancellationToken);
        }
    }
}
