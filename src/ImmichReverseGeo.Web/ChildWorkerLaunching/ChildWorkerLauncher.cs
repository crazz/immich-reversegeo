using System;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.WorkerCommandInvocation;
using WorkerInvocation = ImmichReverseGeo.Web.WorkerCommandInvocation.WorkerCommandInvocation;

namespace ImmichReverseGeo.Web.ChildWorkerLaunching;

internal sealed class ChildWorkerLauncher : IChildWorkerLauncher
{
    private readonly IChildProcessFactory _processFactory;
    private readonly Func<ChildWorkerObserverArmingAcknowledgements> _createObserverArming;

    internal ChildWorkerLauncher(IChildProcessFactory processFactory)
        : this(processFactory, static () => new ChildWorkerObserverArmingAcknowledgements())
    {
    }

    internal ChildWorkerLauncher(
        IChildProcessFactory processFactory,
        Func<ChildWorkerObserverArmingAcknowledgements> createObserverArming)
    {
        _processFactory = processFactory ?? throw new ArgumentNullException(nameof(processFactory));
        _createObserverArming = createObserverArming ?? throw new ArgumentNullException(nameof(createObserverArming));
    }

    public async ValueTask<ChildWorkerLaunchResult> LaunchAsync(
        WorkerInvocation invocation,
        ProcessingRunRequest request,
        IWorkerProtocolEventSink eventSink,
        ChildWorkerLauncherOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(eventSink);
        var dispatch = new ProcessAssetsWorkerJobDispatch(request);
        return await LaunchAsync(
            invocation,
            dispatch,
            new ProcessAssetsWorkerJobEventSink(request, eventSink),
            options,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ChildWorkerLaunchResult> LaunchAsync(
        WorkerInvocation invocation,
        WorkerJobDispatch dispatch,
        IWorkerJobEventSink eventSink,
        ChildWorkerLauncherOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        return await LaunchDescriptorAsync(
            invocation.Descriptor,
            dispatch,
            eventSink,
            options,
            cancellationToken,
            invocation.ProtocolVersion).ConfigureAwait(false);
    }

    internal async ValueTask<ChildWorkerLaunchResult> LaunchDescriptorAsync(
        ChildProcessStartDescriptor descriptor,
        ProcessingRunRequest request,
        IWorkerProtocolEventSink eventSink,
        ChildWorkerLauncherOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(eventSink);
        var dispatch = new ProcessAssetsWorkerJobDispatch(request);
        return await LaunchDescriptorAsync(
            descriptor,
            dispatch,
            new ProcessAssetsWorkerJobEventSink(request, eventSink),
            options,
            cancellationToken,
            InternalWorkerProtocolVersion.V1).ConfigureAwait(false);
    }

    internal async ValueTask<ChildWorkerLaunchResult> LaunchDescriptorAsync(
        ChildProcessStartDescriptor descriptor,
        ProcessingRunRequest request,
        IWorkerProtocolEventSink eventSink,
        ChildWorkerLauncherOptions options,
        CancellationToken cancellationToken,
        InternalWorkerProtocolVersion protocolVersion)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(eventSink);
        var dispatch = new ProcessAssetsWorkerJobDispatch(request);
        return await LaunchDescriptorAsync(
            descriptor,
            dispatch,
            new ProcessAssetsWorkerJobEventSink(request, eventSink),
            options,
            cancellationToken,
            protocolVersion).ConfigureAwait(false);
    }

    internal async ValueTask<ChildWorkerLaunchResult> LaunchDescriptorAsync(
        ChildProcessStartDescriptor descriptor,
        WorkerJobDispatch dispatch,
        IWorkerJobEventSink eventSink,
        ChildWorkerLauncherOptions options,
        CancellationToken cancellationToken,
        InternalWorkerProtocolVersion protocolVersion)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(dispatch);
        ArgumentNullException.ThrowIfNull(eventSink);
        ArgumentNullException.ThrowIfNull(options);
        if (protocolVersion == InternalWorkerProtocolVersion.V1
            && dispatch is not ProcessAssetsWorkerJobDispatch)
        {
            throw new NotSupportedException(
                "Protocol v1 supports only the ProcessAssets job dispatch.");
        }

        if (dispatch.Context.JobKind is not WorkerJobKind.ProcessAssets
            and not WorkerJobKind.CoordinateLookup)
        {
            throw new NotSupportedException(
                "The worker-job dispatch kind is not registered for child launch.");
        }

        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        ChildWorkerObserverArmingAcknowledgements observerArming;
        try
        {
            observerArming = _createObserverArming()
                ?? throw new InvalidOperationException(
                    "Observer arming acknowledgements were not created.");
        }
        catch
        {
            return new ChildWorkerLaunchResult.StartFailed(
                ChildWorkerStartFailureCategory.ProcessStartFailed);
        }

        IChildProcess? process;
        try
        {
            process = await _processFactory
                .StartAsync(descriptor, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            return new ChildWorkerLaunchResult.StartFailed(
                ChildWorkerStartFailureCategory.ProcessStartFailed);
        }

        if (process is null)
        {
            return new ChildWorkerLaunchResult.StartFailed(
                ChildWorkerStartFailureCategory.ProcessStartFailed);
        }

        ChildWorkerSession session = await ChildWorkerSession.CreateAsync(
            process,
            dispatch,
            eventSink,
            options,
            observerArming,
            protocolVersion).ConfigureAwait(false);
        return new ChildWorkerLaunchResult.Started(session);
    }
}
