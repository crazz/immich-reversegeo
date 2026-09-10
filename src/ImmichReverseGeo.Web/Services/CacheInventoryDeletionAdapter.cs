using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.WorkerJobs;

namespace ImmichReverseGeo.Web.Services;

internal sealed class CacheInventoryDeletionOperations(
    CacheDeletionCommand command,
    ICacheInventoryInvalidator invalidator)
{
    internal async Task<CacheDeletionOperationResult> DeleteAsync(CacheDeletionTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        CacheDeletionOperationResult result = await command.DeleteAsync(target).ConfigureAwait(false);
        InvalidateDeleted(result);
        return result;
    }

    internal async Task<CacheDeletionOperationResult> DeleteAllAsync(
        CacheMutationSource source,
        IEnumerable<CacheDeletionTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        CacheDeletionOperationResult result = await command
            .DeleteAllAsync(source, targets)
            .ConfigureAwait(false);
        InvalidateDeleted(result);
        return result;
    }

    private void InvalidateDeleted(CacheDeletionOperationResult result)
    {
        foreach (CacheDeletionTargetResult target in result.Targets)
        {
            if (target is
                {
                    Disposition: CacheDeletionTargetDisposition.Deleted,
                    Source: { } source,
                    Iso3: { } iso3
                })
            {
                invalidator.InvalidateKey(source, iso3);
            }
        }
    }
}

internal sealed class CacheInventoryDeletionPageControllerFactory(
    CacheInventoryDeletionOperations operations)
{
    internal CacheDeletionPageController Create(
        Func<Task> stateChanged,
        Func<Task> reloadStatus)
    {
        ArgumentNullException.ThrowIfNull(stateChanged);
        ArgumentNullException.ThrowIfNull(reloadStatus);
        return new CacheDeletionPageController(
            operations.DeleteAsync,
            operations.DeleteAllAsync,
            stateChanged,
            reloadStatus);
    }
}
