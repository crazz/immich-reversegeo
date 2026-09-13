using ImmichReverseGeo.Core.ApplicationRole;
using ImmichReverseGeo.Web.LifecycleTelemetry;
using Microsoft.Extensions.Logging;
using Role = ImmichReverseGeo.Core.ApplicationRole.ApplicationRole;

namespace ImmichReverseGeo.Tests.LifecycleTelemetry;

internal sealed class RoleLogCapture : IDisposable
{
    private readonly string _role;
    private readonly string? _mode;
    internal RecordingLifecycleLogs Logs { get; } = new();
    internal RoleProcessTelemetry Telemetry { get; }

    internal RoleLogCapture(Role role, DeploymentMode? mode, TimeProvider? time = null)
    {
        var context = new RoleLogContext(role, mode, 42066);
        _role = context.ApplicationRole;
        _mode = context.DeploymentMode;
        Telemetry = new(Logs.CreateLogger(LifecycleEventCatalog.Category), context, time ?? TimeProvider.System);
    }

    internal void AssertCompleted(string stopReason, string outcome, bool ready = true)
    {
        int[] expected = (_mode is null ? Array.Empty<int>() : new[] { 6601 })
            .Concat(new[] { 6602 })
            .Concat(ready ? new[] { 6603 } : [])
            .Concat(new[] { 6604, 6605 }).ToArray();
        var entries = Logs.Entries;
        CollectionAssert.AreEqual(expected, entries.Select(entry => entry.Event.Id).ToArray());
        foreach (var entry in entries)
        {
            Assert.AreEqual(_role, entry["application_role"]);
            Assert.AreEqual(_mode, entry["deployment_mode"]);
            Assert.AreEqual(42066, entry["process_id"]);
            Assert.AreEqual(LifecycleEventCatalog.Category, entry.Category);
            Assert.IsNull(entry.Exception);
            Assert.HasCount(0, entry.Scopes);
            Assert.IsFalse(entry.Rendered.Contains("secret", StringComparison.OrdinalIgnoreCase));
        }

        Assert.AreEqual(stopReason, entries.Single(entry => entry.Event.Id == 6604)["stop_reason"]);
        var stopped = entries.Single(entry => entry.Event.Id == 6605);
        Assert.AreEqual(outcome, stopped["process_outcome"]);
        Assert.AreEqual(outcome == "failed" ? LogLevel.Warning : LogLevel.Information, stopped.Level);
        Assert.IsTrue((long)stopped["process_duration_ms"]! >= 0);
        Assert.IsTrue((long)stopped["stop_duration_ms"]! >= 0);
    }

    public void Dispose() => Telemetry.Dispose();
}
