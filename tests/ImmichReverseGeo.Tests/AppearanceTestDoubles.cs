using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests;

internal sealed class NoopAppearanceDocument : IAppearanceDocument
{
    public Task SetDataThemeAsync(string theme) => Task.CompletedTask;
}

internal sealed class NoopBrowserColorScheme : IBrowserColorScheme
{
    public string? CurrentScheme => null;

    public Task EnsureReadyAsync() => Task.CompletedTask;

    public IDisposable Subscribe(Action<string?> onChanged) => new NoopDisposable();

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
