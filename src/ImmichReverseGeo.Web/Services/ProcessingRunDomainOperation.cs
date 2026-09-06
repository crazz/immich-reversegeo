using System;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Processing;

namespace ImmichReverseGeo.Web.Services;

internal interface IProcessingRunDomainOperation
{
    Task ExecuteAsync(
        IProcessingRunEventSession session,
        Func<Task> executeProductionDomainAsync,
        CancellationToken cancellationToken);
}

internal sealed class DefaultProcessingRunDomainOperation : IProcessingRunDomainOperation
{
    internal static DefaultProcessingRunDomainOperation Instance { get; } = new();

    public Task ExecuteAsync(
        IProcessingRunEventSession session,
        Func<Task> executeProductionDomainAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(executeProductionDomainAsync);
        return executeProductionDomainAsync();
    }
}
