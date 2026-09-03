using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Processing;

namespace ImmichReverseGeo.Web.WorkerHost.WorkerNdjsonOutput;

internal sealed class WorkerNdjsonProcessingEventReporter : ProcessingEventReporter
{
    private readonly WorkerNdjsonEmitter _emitter;

    internal WorkerNdjsonProcessingEventReporter(WorkerNdjsonEmitter emitter)
    {
        _emitter = emitter;
    }

    protected override ValueTask AcceptAsync(ProcessingEvent processingEvent, CancellationToken cancellationToken)
    {
        return _emitter.SubmitAsync(processingEvent, cancellationToken);
    }
}
