using System;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;

namespace ImmichReverseGeo.Web.Services;

public interface IAppearanceDocument
{
    Task SetDataThemeAsync(string theme);
}

public interface IBrowserColorScheme
{
    string? CurrentScheme { get; }

    Task EnsureReadyAsync();

    IDisposable Subscribe(Action<string?> onChanged);
}

public sealed class AppearanceApplier : IAsyncDisposable
{
    private readonly IAppearanceDocument _document;
    private readonly IBrowserColorScheme _browser;
    private IDisposable? _schemeSubscription;
    private bool _sessionModeCommitted;

    public AppearanceApplier(IAppearanceDocument document, IBrowserColorScheme browser)
    {
        _document = document;
        _browser = browser;
    }

    public string SavedMode { get; private set; } = AppearanceModes.Auto;

    public string? LastChangeError { get; private set; }

    public async Task InitializeAsync(string mode)
    {
        await _browser.EnsureReadyAsync().ConfigureAwait(false);

        // A successful Settings change during layout init must not be overwritten by the
        // earlier config snapshot that started this InitializeAsync call.
        if (!_sessionModeCommitted)
        {
            SavedMode = AppearanceModes.NormalizeMode(mode);
        }

        await ApplyCurrentModeAsync().ConfigureAwait(false);
        SyncSchemeSubscription();
    }

    public async Task<bool> TryChangeModeAsync(string mode, Func<string, Task> persistAsync)
    {
        LastChangeError = null;
        string normalized = AppearanceModes.NormalizeMode(mode);

        // Resolve Auto against the browser scheme before applying; do not apply until persist succeeds.
        await _browser.EnsureReadyAsync().ConfigureAwait(false);

        try
        {
            await persistAsync(normalized).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LastChangeError = ex.Message;
            return false;
        }

        SavedMode = normalized;
        _sessionModeCommitted = true;
        await ApplyCurrentModeAsync().ConfigureAwait(false);
        SyncSchemeSubscription();
        return true;
    }

    public ValueTask DisposeAsync()
    {
        _schemeSubscription?.Dispose();
        _schemeSubscription = null;
        return ValueTask.CompletedTask;
    }

    private async Task ApplyCurrentModeAsync()
    {
        string theme = ResolveTheme(SavedMode, _browser.CurrentScheme);
        await _document.SetDataThemeAsync(theme).ConfigureAwait(false);
    }

    private void SyncSchemeSubscription()
    {
        _schemeSubscription?.Dispose();
        _schemeSubscription = null;

        if (!string.Equals(SavedMode, AppearanceModes.Auto, StringComparison.Ordinal))
        {
            return;
        }

        _schemeSubscription = _browser.Subscribe(scheme =>
        {
            _ = ApplyCurrentModeAsync();
        });
    }

    internal static string ResolveTheme(string mode, string? browserScheme)
    {
        if (string.Equals(mode, AppearanceModes.Light, StringComparison.Ordinal))
        {
            return AppearanceThemes.Light;
        }

        if (string.Equals(mode, AppearanceModes.Dark, StringComparison.Ordinal))
        {
            return AppearanceThemes.Dark;
        }

        if (string.Equals(browserScheme, AppearanceThemes.Dark, StringComparison.OrdinalIgnoreCase))
        {
            return AppearanceThemes.Dark;
        }

        return AppearanceThemes.Light;
    }
}
