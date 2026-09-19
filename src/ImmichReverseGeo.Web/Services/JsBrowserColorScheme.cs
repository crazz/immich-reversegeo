using System;
using System.Threading.Tasks;
using Microsoft.JSInterop;

namespace ImmichReverseGeo.Web.Services;

public sealed class JsBrowserColorScheme(IJSRuntime js) : IBrowserColorScheme, IAsyncDisposable
{
    private DotNetObjectReference<JsBrowserColorScheme>? _self;
    private Action<string?>? _onChanged;
    private bool _ready;
    private bool _subscribed;

    public string? CurrentScheme { get; private set; }

    public async Task EnsureReadyAsync()
    {
        if (_ready)
        {
            return;
        }

        CurrentScheme = await js.InvokeAsync<string?>("appearanceTheme.getPrefersColorScheme")
            .ConfigureAwait(false);
        _ready = true;
    }

    public IDisposable Subscribe(Action<string?> onChanged)
    {
        _onChanged = onChanged;
        _self ??= DotNetObjectReference.Create(this);
        if (!_subscribed)
        {
            _subscribed = true;
            _ = js.InvokeVoidAsync("appearanceTheme.subscribePrefersColorScheme", _self);
        }

        return new Subscription(() =>
        {
            _onChanged = null;
            if (_subscribed)
            {
                _subscribed = false;
                _ = js.InvokeVoidAsync("appearanceTheme.unsubscribePrefersColorScheme");
            }
        });
    }

    [JSInvokable]
    public void OnBrowserSchemeChanged(string? scheme)
    {
        CurrentScheme = scheme;
        _onChanged?.Invoke(scheme);
    }

    public async ValueTask DisposeAsync()
    {
        if (_subscribed)
        {
            _subscribed = false;
            try
            {
                await js.InvokeVoidAsync("appearanceTheme.unsubscribePrefersColorScheme")
                    .ConfigureAwait(false);
            }
            catch (JSDisconnectedException)
            {
            }
        }

        _self?.Dispose();
        _self = null;
        _onChanged = null;
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
