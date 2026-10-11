using System.Text.Json;
using ImmichReverseGeo.Core.Models;

namespace ImmichReverseGeo.Web.Services;

public sealed class SettingsSaveState
{
    private string? _savedSnapshot;
    private string? _savingSnapshot;

    public bool IsLoaded => _savedSnapshot is not null;
    public bool IsSaving => _savingSnapshot is not null;
    public string? Error { get; private set; }

    public void Load(AppConfig config)
    {
        _savedSnapshot = JsonSerializer.Serialize(config);
        Error = null;
    }

    public bool HasChanges(AppConfig config) => IsLoaded && JsonSerializer.Serialize(config) != _savedSnapshot;

    public AppConfig? BeginSave(AppConfig config)
    {
        if (IsSaving || !HasChanges(config))
        {
            return null;
        }

        Error = null;
        _savingSnapshot = JsonSerializer.Serialize(config);
        return JsonSerializer.Deserialize<AppConfig>(_savingSnapshot)!;
    }

    public void CompleteSave()
    {
        _savedSnapshot = _savingSnapshot ?? _savedSnapshot;
        _savingSnapshot = null;
    }

    public void FailSave(string message)
    {
        Error = message;
        _savingSnapshot = null;
    }
}
