using System;
using System.Threading.Tasks;

namespace ImmichReverseGeo.Web.ProcessingRunLocking;

internal static class ProcessingRunLockSessionCleanup
{
    internal static async Task<bool> DisposeAsync(
        IProcessingRunLockSession session,
        bool clearPoolBeforeDisposal)
    {
        bool infrastructureFailure = false;
        bool clearAttempted = false;

        if (clearPoolBeforeDisposal)
        {
            clearAttempted = true;
            infrastructureFailure = !TryClearPool(session);
        }

        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            infrastructureFailure = true;
            if (!clearAttempted)
            {
                _ = TryClearPool(session);
            }
        }

        return infrastructureFailure;
    }

    internal static bool IsFatal(Exception exception) => exception is OutOfMemoryException;

    private static bool TryClearPool(IProcessingRunLockSession session)
    {
        try
        {
            session.ClearPool();
            return true;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return false;
        }
    }
}
