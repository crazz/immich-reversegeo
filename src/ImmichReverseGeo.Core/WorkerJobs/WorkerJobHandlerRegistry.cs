using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ImmichReverseGeo.Core.WorkerJobs;

public interface IWorkerJobHandlerRegistration
{
    WorkerJobDescriptor Descriptor { get; }
    Type DeclaredRequestType { get; }
    Type DeclaredResultType { get; }
    IWorkerJobHandlerAdapter Resolve(IServiceProvider services);
}

public interface IWorkerJobHandlerAdapter
{
    WorkerJobDescriptor Descriptor { get; }

    ValueTask<IWorkerJobResult> ExecuteAsync(
        WorkerJobContext context,
        IWorkerJobRequest request,
        IWorkerJobEventReporter eventReporter,
        CancellationToken cancellationToken);
}

public sealed class WorkerJobHandlerRegistration<TRequest, TResult> :
    IWorkerJobHandlerRegistration
    where TRequest : IWorkerJobRequest
    where TResult : IWorkerJobResult
{
    private readonly Func<IServiceProvider, IWorkerJobHandler<TRequest, TResult>> _resolve;

    public WorkerJobHandlerRegistration(
        WorkerJobDescriptor descriptor,
        Func<IServiceProvider, IWorkerJobHandler<TRequest, TResult>> resolve)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(resolve);
        Descriptor = descriptor;
        _resolve = resolve;
    }

    public WorkerJobDescriptor Descriptor { get; }

    public Type DeclaredRequestType => typeof(TRequest);

    public Type DeclaredResultType => typeof(TResult);

    public IWorkerJobHandlerAdapter Resolve(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        IWorkerJobHandler<TRequest, TResult> handler = _resolve(services)
            ?? throw new InvalidOperationException("The worker-job handler was not resolved.");
        return new WorkerJobHandlerAdapter<TRequest, TResult>(Descriptor, handler);
    }
}

public sealed class WorkerJobHandlerAdapter<TRequest, TResult> : IWorkerJobHandlerAdapter
    where TRequest : IWorkerJobRequest
    where TResult : IWorkerJobResult
{
    private readonly IWorkerJobHandler<TRequest, TResult> _handler;

    public WorkerJobHandlerAdapter(
        WorkerJobDescriptor descriptor,
        IWorkerJobHandler<TRequest, TResult> handler)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(handler);
        if (descriptor.RequestType != typeof(TRequest)
            || descriptor.ResultType != typeof(TResult))
        {
            throw new ArgumentException(
                "The handler types do not match the worker-job descriptor.",
                nameof(descriptor));
        }

        Descriptor = descriptor;
        _handler = handler;
    }

    public WorkerJobDescriptor Descriptor { get; }

    public async ValueTask<IWorkerJobResult> ExecuteAsync(
        WorkerJobContext context,
        IWorkerJobRequest request,
        IWorkerJobEventReporter eventReporter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(eventReporter);
        if (context.JobKind != Descriptor.Kind || request is not TRequest typedRequest)
        {
            throw new ArgumentException(
                "The request does not match the registered worker-job kind.",
                nameof(request));
        }

        TResult result = await _handler
            .ExecuteAsync(context, typedRequest, eventReporter, cancellationToken)
            .ConfigureAwait(false);
        return result
            ?? throw new InvalidOperationException("The worker-job handler returned no result.");
    }
}

public sealed class WorkerJobHandlerRegistry
{
    private readonly IReadOnlyDictionary<WorkerJobKind, IWorkerJobHandlerRegistration> _registrations;

    public WorkerJobHandlerRegistry(IEnumerable<IWorkerJobHandlerRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        var byKind = new Dictionary<WorkerJobKind, IWorkerJobHandlerRegistration>();
        foreach (IWorkerJobHandlerRegistration registration in registrations)
        {
            ArgumentNullException.ThrowIfNull(registration);
            WorkerJobDescriptor descriptor = registration.Descriptor
                ?? throw new ArgumentException("A worker-job registration requires a descriptor.", nameof(registrations));
            if (descriptor.RequestType != registration.DeclaredRequestType
                || descriptor.ResultType != registration.DeclaredResultType)
            {
                throw new ArgumentException(
                    "A worker-job registration does not match its descriptor types.",
                    nameof(registrations));
            }

            if (!MatchesCanonicalSchema(descriptor))
            {
                throw new ArgumentException(
                    "A worker-job registration does not match a concrete supported job-kind schema.",
                    nameof(registrations));
            }

            if (!byKind.TryAdd(descriptor.Kind, registration))
            {
                throw new ArgumentException(
                    "Only one worker-job handler may be registered for each kind.",
                    nameof(registrations));
            }
        }

        _registrations = new ReadOnlyDictionary<WorkerJobKind, IWorkerJobHandlerRegistration>(byKind);
        SupportedJobDescriptors = new ReadOnlyCollection<WorkerJobDescriptor>(
            byKind.Values
                .Select(static registration => registration.Descriptor)
                .OrderBy(static descriptor => descriptor.Kind)
                .ToArray());
        SupportedJobKinds = new ReadOnlyCollection<WorkerJobKind>(
            SupportedJobDescriptors.Select(static descriptor => descriptor.Kind).ToArray());
    }

    public IReadOnlyList<WorkerJobDescriptor> SupportedJobDescriptors { get; }

    public IReadOnlyList<WorkerJobKind> SupportedJobKinds { get; }

    public bool IsRegistered(WorkerJobKind kind) => _registrations.ContainsKey(kind);

    public WorkerJobOutputMessage CreateReady(long sequence, DateTimeOffset timestampUtc) =>
        new(
            WorkerJobProtocolV2.LifecycleCategory,
            WorkerJobProtocolV2.ReadyType,
            sequence,
            timestampUtc,
            null,
            null,
            new WorkerJobReadyPayload(SupportedJobKinds));

    public bool TryResolve(
        WorkerJobKind kind,
        IServiceProvider services,
        out IWorkerJobHandlerAdapter? handler)
    {
        ArgumentNullException.ThrowIfNull(services);
        handler = null;
        if (!_registrations.TryGetValue(kind, out IWorkerJobHandlerRegistration? registration))
        {
            return false;
        }

        handler = registration.Resolve(services);
        return true;
    }

    private static bool MatchesCanonicalSchema(WorkerJobDescriptor descriptor) =>
        descriptor.Kind switch
        {
            WorkerJobKind.ProcessAssets =>
                descriptor.RequestType == typeof(ProcessAssetsRequest)
                && descriptor.ResultType == typeof(ProcessAssetsResult),
            WorkerJobKind.CoordinateLookup =>
                descriptor.RequestType == typeof(CoordinateLookupRequest)
                && descriptor.ResultType == typeof(CoordinateLookupResult),
            WorkerJobKind.CacheMutation => false,
            _ => false
        };
}
