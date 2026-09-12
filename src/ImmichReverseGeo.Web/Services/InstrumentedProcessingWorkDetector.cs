using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Web.Services;

internal sealed class InstrumentedProcessingWorkDetector : IProcessingWorkDetector
{
    internal const double SlowThresholdMilliseconds = 1000;
    private static readonly EventId CompletedEvent = new(5901, "ProcessingWorkDetectorCompleted");
    private const string Strategy = "postgres-exists-v1";
    private const string DatabaseOperation = "eligibility-existence-probe";
    private const string Template = "Processing work detector completed: duration_ms={duration_ms}, outcome={outcome}, strategy={strategy}, trigger={trigger}, purpose={purpose}, coverage={coverage}, database_operation={database_operation}.";

    private readonly IProcessingWorkDetector _inner;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<InstrumentedProcessingWorkDetector> _logger;

    public InstrumentedProcessingWorkDetector(IProcessingWorkDetector inner, TimeProvider timeProvider,
        ILogger<InstrumentedProcessingWorkDetector> logger)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ProcessingWorkDetectionResult> DetectAsync(ProcessingWorkDetectionRequest request,
        CancellationToken cancellationToken)
    {
        ProcessingWorkDetectionResult? result = null;
        CompletionOutcome outcome = CompletionOutcome.Failed;
        long started = _timeProvider.GetTimestamp();
        try
        {
            result = await _inner.DetectAsync(request, cancellationToken);
            outcome = result.HasWork ? CompletionOutcome.HasWork : CompletionOutcome.NoWork;
            return result;
        }
        catch
        {
            outcome = cancellationToken.IsCancellationRequested ? CompletionOutcome.Cancelled : CompletionOutcome.Failed;
            throw;
        }
        finally
        {
            EmitCompletion(started, outcome, result);
        }
    }

    private void EmitCompletion(long started, CompletionOutcome outcome, ProcessingWorkDetectionResult? result)
    {
        try
        {
            double duration = _timeProvider.GetElapsedTime(started, _timeProvider.GetTimestamp()).TotalMilliseconds;
            LogLevel level = outcome == CompletionOutcome.Failed || duration >= SlowThresholdMilliseconds
                ? LogLevel.Warning : LogLevel.Information;
            var state = new CompletionState(duration, outcome, result?.Diagnostics.UsedFallback);
            _logger.Log(level, CompletedEvent, state, exception: null, static (value, _) => value.Message);
        }
        catch (Exception)
        {
            // A logging-provider failure must not replace the detector's result or original exception.
            // Make no retry or secondary log attempt from this observer.
        }
    }

    private enum CompletionOutcome { HasWork, NoWork, Cancelled, Failed }

    private sealed class CompletionState : IReadOnlyList<KeyValuePair<string, object?>>
    {
        private readonly KeyValuePair<string, object?>[] _fields;

        internal CompletionState(double duration, CompletionOutcome outcome, bool? fallbackUsed)
        {
            string outcomeName = outcome.ToString();
            List<KeyValuePair<string, object?>> fields =
            [
                new("duration_ms", duration),
                new("outcome", outcomeName),
                new("strategy", Strategy),
                new("trigger", "Scheduled"),
                new("purpose", "ScheduledLaunch"),
                new("coverage", "FullEligibility"),
                new("database_operation", DatabaseOperation)
            ];
            if (fallbackUsed.HasValue)
            {
                fields.Add(new("fallback_used", fallbackUsed.Value));
                // The production inner strategy is the finalized single-operation EXISTS detector.
                fields.Add(new("database_roundtrips", 1));
            }
            fields.Add(new("{OriginalFormat}", Template));
            _fields = fields.ToArray();
            Message = string.Create(CultureInfo.InvariantCulture,
                $"Processing work detector completed: duration_ms={duration}, outcome={outcomeName}, strategy={Strategy}, trigger=Scheduled, purpose=ScheduledLaunch, coverage=FullEligibility, database_operation={DatabaseOperation}.");
        }

        internal string Message { get; }
        public override string ToString() => Message;
        public int Count => _fields.Length;
        public KeyValuePair<string, object?> this[int index] => _fields[index];
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, object?>>)_fields).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
