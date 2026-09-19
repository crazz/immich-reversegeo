using System.Threading.Tasks;
using Microsoft.JSInterop;

namespace ImmichReverseGeo.Web.Services;

public sealed class JsAppearanceDocument(IJSRuntime js) : IAppearanceDocument
{
    public Task SetDataThemeAsync(string theme)
    {
        return js.InvokeVoidAsync("appearanceTheme.setDataTheme", theme).AsTask();
    }
}
