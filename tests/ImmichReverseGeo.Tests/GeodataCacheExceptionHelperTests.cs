using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class GeodataCacheExceptionHelperTests
{
    [TestMethod]
    public async Task AdministrativeAreaResolverGadmCaches_ActiveTokenCancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var preparation = AdministrativeAreaResolverCacheHelper.PrepareGadmCachesAsync(
            ["USA", "PRI"],
            cts.Token,
            async (_, token) =>
            {
                entered.TrySetResult();
                await release.Task;
                token.ThrowIfCancellationRequested();
                return true;
            },
            (_, _) => Assert.Fail("Active caller cancellation must not become cache unavailability."));

        await entered.Task;
        cts.Cancel();
        release.SetResult(true);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => preparation);
    }

    [TestMethod]
    public async Task AdministrativeAreaResolverGadmCaches_OutOfMemoryPropagates()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var preparation = AdministrativeAreaResolverCacheHelper.PrepareGadmCachesAsync(
            ["USA"],
            CancellationToken.None,
            async (_, _) =>
            {
                entered.TrySetResult();
                await release.Task;
                throw new OutOfMemoryException("controlled");
            },
            (_, _) => Assert.Fail("OutOfMemoryException must not become cache unavailability."));

        await entered.Task;
        release.SetResult(true);

        await Assert.ThrowsExactlyAsync<OutOfMemoryException>(() => preparation);
    }

    [TestMethod]
    public async Task AdministrativeAreaResolverGadmCaches_OrdinaryUnavailableTerritoryContinuesToNextCache()
    {
        var attemptedCodes = new List<string>();
        var unavailableCodes = new List<string>();

        var readyCodes = await AdministrativeAreaResolverCacheHelper.PrepareGadmCachesAsync(
            ["USA", "PRI"],
            CancellationToken.None,
            (code, _) =>
            {
                attemptedCodes.Add(code);
                return code == "USA"
                    ? Task.FromException<bool>(new InvalidOperationException("controlled unavailable cache"))
                    : Task.FromResult(true);
            },
            (code, ex) =>
            {
                Assert.IsInstanceOfType<InvalidOperationException>(ex);
                unavailableCodes.Add(code);
            });

        CollectionAssert.AreEqual(new[] { "USA", "PRI" }, attemptedCodes);
        CollectionAssert.AreEqual(new[] { "USA" }, unavailableCodes);
        CollectionAssert.AreEqual(new[] { "PRI" }, readyCodes.ToArray());
    }

    [TestMethod]
    public async Task LookupRunBoundary_OutOfMemoryPropagates()
    {
        await Assert.ThrowsExactlyAsync<OutOfMemoryException>(() =>
            LookupRunExceptionHelper.ExecuteAsync(() => Task.FromException(new OutOfMemoryException("controlled"))));
    }

    [TestMethod]
    public async Task LookupCacheHelper_OutOfMemoryPropagates()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var ensure = LookupCacheHelper.TryEnsureAsync<bool>(async () =>
        {
            entered.TrySetResult();
            await release.Task;
            throw new OutOfMemoryException("controlled");
        });

        await entered.Task;
        release.SetResult(true);

        await Assert.ThrowsExactlyAsync<OutOfMemoryException>(() => ensure);
    }

    [TestMethod]
    public async Task LookupCacheHelper_OrdinaryCacheFailureReturnsUnavailableResult()
    {
        var result = await LookupCacheHelper.TryEnsureAsync<bool>(
            () => Task.FromException<bool>(new InvalidOperationException("controlled unavailable cache")));

        Assert.IsFalse(result.IsAvailable);
        Assert.IsFalse(result.Value);
        Assert.IsInstanceOfType<InvalidOperationException>(result.Error);
        Assert.AreEqual("controlled unavailable cache", result.Error.Message);
    }

    [TestMethod]
    public async Task LookupComponentBoundary_OutOfMemoryEscapesWithoutSettingError()
    {
        var component = new ImmichReverseGeo.Web.Components.Pages.Lookup();

        await Assert.ThrowsExactlyAsync<OutOfMemoryException>(() =>
            component.RunLookupAsync(() => Task.FromException(new OutOfMemoryException("controlled"))));

        var errorField = typeof(ImmichReverseGeo.Web.Components.Pages.Lookup).GetField(
            "_error",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(errorField);
        Assert.IsNull(errorField.GetValue(component));
    }
}
