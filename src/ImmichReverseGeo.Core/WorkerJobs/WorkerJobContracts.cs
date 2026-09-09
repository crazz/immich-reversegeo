using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;

namespace ImmichReverseGeo.Core.WorkerJobs;

public enum WorkerJobKind
{
    ProcessAssets,
    CoordinateLookup,
    CacheMutation
}

public static class WorkerJobKindNames
{
    public const string ProcessAssets = "ProcessAssets";
    public const string CoordinateLookup = "CoordinateLookup";
    public const string CacheMutation = "CacheMutation";

    public static string Format(WorkerJobKind kind) => kind switch
    {
        WorkerJobKind.ProcessAssets => ProcessAssets,
        WorkerJobKind.CoordinateLookup => CoordinateLookup,
        WorkerJobKind.CacheMutation => CacheMutation,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static bool TryParse(string value, out WorkerJobKind kind)
    {
        kind = value switch
        {
            ProcessAssets => WorkerJobKind.ProcessAssets,
            CoordinateLookup => WorkerJobKind.CoordinateLookup,
            CacheMutation => WorkerJobKind.CacheMutation,
            _ => default
        };
        return value is ProcessAssets or CoordinateLookup or CacheMutation;
    }
}

public enum WorkerJobCapabilityFamily
{
    Processing,
    Lookup,
    CacheMaintenance
}

public enum WorkerJobResourceClass
{
    ExclusiveHeavyWorker,
    GeodataRead,
    GeodataMutation
}

public enum WorkerJobRequestOrigin
{
    Manual,
    Scheduled,
    RunOnce
}

public sealed record WorkerJobArbitrationMetadata(
    WorkerJobCapabilityFamily CapabilityFamily,
    WorkerJobResourceClass ResourceClass,
    bool IsHeavy,
    bool IsCancellable,
    bool IsGeodataBearing);

public interface IWorkerJobRequest;

public interface IWorkerJobResult;

public sealed record ProcessAssetsRequest : IWorkerJobRequest
{
    public ProcessingRunRequest ProcessingRequest { get; }

    public ProcessAssetsRequest(ProcessingRunRequest processingRequest)
    {
        ArgumentNullException.ThrowIfNull(processingRequest);
        ProcessingRequest = processingRequest;
    }
}

public sealed record ProcessAssetsResult : IWorkerJobResult
{
    public string Trigger { get; }
    public DateTimeOffset StartedAtUtc { get; }
    public DateTimeOffset EndedAtUtc { get; }
    public long ProcessedCount { get; }
    public long UpdatedCount { get; }
    public long SkippedCount { get; }
    public long FailedCount { get; }

    public ProcessAssetsResult(
        string trigger,
        DateTimeOffset startedAtUtc,
        DateTimeOffset endedAtUtc,
        long processedCount,
        long updatedCount,
        long skippedCount,
        long failedCount)
    {
        WorkerJobProtocolV2.RequireTrigger(trigger, nameof(trigger));
        WorkerJobProtocolV2.RequireUtc(startedAtUtc, nameof(startedAtUtc));
        WorkerJobProtocolV2.RequireUtc(endedAtUtc, nameof(endedAtUtc));
        if (endedAtUtc < startedAtUtc)
        {
            throw new ArgumentException("The terminal timestamp must not precede the start timestamp.", nameof(endedAtUtc));
        }

        WorkerJobProtocolV2.RequireCounts(processedCount, updatedCount, skippedCount, failedCount);
        Trigger = trigger;
        StartedAtUtc = startedAtUtc;
        EndedAtUtc = endedAtUtc;
        ProcessedCount = processedCount;
        UpdatedCount = updatedCount;
        SkippedCount = skippedCount;
        FailedCount = failedCount;
    }
}

public sealed record WorkerJobDescriptor
{
    public WorkerJobKind Kind { get; }
    public Type RequestType { get; }
    public Type ResultType { get; }
    public WorkerJobArbitrationMetadata Arbitration { get; }

    public WorkerJobDescriptor(
        WorkerJobKind kind,
        Type requestType,
        Type resultType,
        WorkerJobArbitrationMetadata arbitration)
    {
        ArgumentNullException.ThrowIfNull(requestType);
        ArgumentNullException.ThrowIfNull(resultType);
        ArgumentNullException.ThrowIfNull(arbitration);
        if (!typeof(IWorkerJobRequest).IsAssignableFrom(requestType))
        {
            throw new ArgumentException("The descriptor request type must be a worker-job request.", nameof(requestType));
        }

        if (!typeof(IWorkerJobResult).IsAssignableFrom(resultType))
        {
            throw new ArgumentException("The descriptor result type must be a worker-job result.", nameof(resultType));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Kind = kind;
        RequestType = requestType;
        ResultType = resultType;
        Arbitration = arbitration;
    }
}

public static class WorkerJobDescriptors
{
    public static WorkerJobDescriptor ProcessAssets { get; } = new(
        WorkerJobKind.ProcessAssets,
        typeof(ProcessAssetsRequest),
        typeof(ProcessAssetsResult),
        new WorkerJobArbitrationMetadata(
            WorkerJobCapabilityFamily.Processing,
            WorkerJobResourceClass.ExclusiveHeavyWorker,
            IsHeavy: true,
            IsCancellable: true,
            IsGeodataBearing: true));

    public static WorkerJobDescriptor CoordinateLookup { get; } = new(
        WorkerJobKind.CoordinateLookup,
        typeof(CoordinateLookupRequest),
        typeof(CoordinateLookupResult),
        new WorkerJobArbitrationMetadata(
            WorkerJobCapabilityFamily.Lookup,
            WorkerJobResourceClass.ExclusiveHeavyWorker,
            IsHeavy: true,
            IsCancellable: true,
            IsGeodataBearing: true));

    public static IReadOnlyList<WorkerJobDescriptor> Registered { get; } =
        Array.AsReadOnly([ProcessAssets, CoordinateLookup]);

    public static WorkerJobKind CacheMutation => WorkerJobKind.CacheMutation;
}

public sealed record WorkerJobContext
{
    public Guid JobId { get; }
    public WorkerJobKind JobKind { get; }
    public WorkerJobRequestOrigin Origin { get; }

    public WorkerJobContext(Guid jobId, WorkerJobKind jobKind, WorkerJobRequestOrigin origin)
    {
        if (jobId == Guid.Empty)
        {
            throw new ArgumentException("A job ID must not be empty.", nameof(jobId));
        }

        JobId = jobId;
        JobKind = jobKind;
        Origin = origin;
    }
}

public abstract record WorkerJobDispatch
{
    public WorkerJobContext Context { get; }
    public WorkerJobDescriptor Descriptor { get; }
    public bool IsCancellable => Descriptor.Arbitration.IsCancellable;

    protected WorkerJobDispatch(
        WorkerJobContext context,
        WorkerJobDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(descriptor);
        if (context.JobKind != descriptor.Kind)
        {
            throw new ArgumentException(
                "The dispatch context and descriptor must name the same job kind.",
                nameof(descriptor));
        }

        Context = context;
        Descriptor = descriptor;
    }
}

public sealed record ProcessAssetsWorkerJobDispatch : WorkerJobDispatch
{
    public ProcessAssetsRequest Request { get; }

    public ProcessAssetsWorkerJobDispatch(ProcessingRunRequest request)
        : this(request, WorkerJobDescriptors.ProcessAssets)
    {
    }

    internal ProcessAssetsWorkerJobDispatch(
        ProcessingRunRequest request,
        WorkerJobDescriptor descriptor)
        : base(
            new WorkerJobContext(
                RequireRequest(request).RunId,
                WorkerJobKind.ProcessAssets,
                Origin(request.Trigger)),
            RequireProcessAssetsDescriptor(descriptor))
    {
        Request = new ProcessAssetsRequest(request);
    }

    private static ProcessingRunRequest RequireRequest(ProcessingRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request;
    }

    private static WorkerJobDescriptor RequireProcessAssetsDescriptor(
        WorkerJobDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.Kind != WorkerJobKind.ProcessAssets
            || descriptor.RequestType != typeof(ProcessAssetsRequest)
            || descriptor.ResultType != typeof(ProcessAssetsResult))
        {
            throw new ArgumentException(
                "The dispatch descriptor must use the ProcessAssets schema.",
                nameof(descriptor));
        }

        return descriptor;
    }

    private static WorkerJobRequestOrigin Origin(ProcessingRunTrigger trigger) => trigger switch
    {
        ProcessingRunTrigger.Manual => WorkerJobRequestOrigin.Manual,
        ProcessingRunTrigger.Scheduled => WorkerJobRequestOrigin.Scheduled,
        ProcessingRunTrigger.RunOnce => WorkerJobRequestOrigin.RunOnce,
        _ => throw new ArgumentOutOfRangeException(nameof(trigger))
    };
}

public sealed record CoordinateLookupWorkerJobDispatch : WorkerJobDispatch
{
    public CoordinateLookupRequest Request { get; }

    public CoordinateLookupWorkerJobDispatch(Guid jobId, CoordinateLookupRequest request)
        : this(jobId, request, WorkerJobDescriptors.CoordinateLookup)
    {
    }

    internal CoordinateLookupWorkerJobDispatch(
        Guid jobId,
        CoordinateLookupRequest request,
        WorkerJobDescriptor descriptor)
        : base(
            new WorkerJobContext(jobId, WorkerJobKind.CoordinateLookup, WorkerJobRequestOrigin.Manual),
            RequireCoordinateLookupDescriptor(descriptor))
    {
        ArgumentNullException.ThrowIfNull(request);
        Request = request;
    }

    private static WorkerJobDescriptor RequireCoordinateLookupDescriptor(WorkerJobDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.Kind != WorkerJobKind.CoordinateLookup
            || descriptor.RequestType != typeof(CoordinateLookupRequest)
            || descriptor.ResultType != typeof(CoordinateLookupResult))
        {
            throw new ArgumentException(
                "The dispatch descriptor must use the CoordinateLookup schema.",
                nameof(descriptor));
        }

        return descriptor;
    }
}

public interface IWorkerJobEventReporter
{
    ValueTask ReportAsync(WorkerJobHandlerEvent @event, CancellationToken cancellationToken);
}

public interface IWorkerJobHandler<TRequest, TResult>
    where TRequest : IWorkerJobRequest
    where TResult : IWorkerJobResult
{
    ValueTask<TResult> ExecuteAsync(
        WorkerJobContext context,
        TRequest request,
        IWorkerJobEventReporter eventReporter,
        CancellationToken cancellationToken);
}
