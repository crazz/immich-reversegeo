using System;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
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
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        IChildProcess? process;
        try
        {
            process = await _processFactory.StartAsync(invocation.Descriptor, CancellationToken.None);
        }
        catch
        {
            return new ChildWorkerLaunchResult.StartFailed(ChildWorkerStartFailureCategory.ProcessStartFailed);
        }

        if (process is null)
        {
            return new ChildWorkerLaunchResult.StartFailed(ChildWorkerStartFailureCategory.ProcessStartFailed);
        }

        try
        {
            var observerArming = _createObserverArming()
                ?? throw new InvalidOperationException("Observer arming acknowledgements were not created.");
            var session = await ChildWorkerSession.CreateAsync(process, request, eventSink, options, observerArming).ConfigureAwait(false);
            return new ChildWorkerLaunchResult.Started(session);
        }
        catch
        {
            try
            {
                await process.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }

            return new ChildWorkerLaunchResult.StartFailed(ChildWorkerStartFailureCategory.ProcessStartFailed);
        }
    }
}
