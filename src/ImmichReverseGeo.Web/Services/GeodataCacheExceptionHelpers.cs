using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ImmichReverseGeo.Web.Services;

internal static class AdministrativeAreaResolverCacheHelper
{
    internal static async Task<IReadOnlyList<string>> PrepareGadmCachesAsync(
        IReadOnlyList<string> candidateCodes,
        CancellationToken ct,
        Func<string, CancellationToken, Task<bool>> prepareCacheAsync,
        Action<string, Exception> cacheUnavailable)
    {
        var readyCodes = new List<string>();
        foreach (var code in candidateCodes)
        {
            try
            {
                if (await prepareCacheAsync(code, ct))
                {
                    readyCodes.Add(code);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OutOfMemoryException)
            {
                throw;
            }
            catch (Exception ex)
            {
                cacheUnavailable(code, ex);
            }
        }

        return readyCodes;
    }
}

internal static class LookupCacheHelper
{
    internal static async Task<LookupCacheEnsureResult<T>> TryEnsureAsync<T>(Func<Task<T>> ensureAsync)
    {
        try
        {
            return new LookupCacheEnsureResult<T>(true, await ensureAsync(), null);
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new LookupCacheEnsureResult<T>(false, default, ex);
        }
    }
}

internal static class LookupRunExceptionHelper
{
    internal static async Task ExecuteAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
    }
}

internal record LookupCacheEnsureResult<T>(bool IsAvailable, T? Value, Exception? Error);
