using ImmichReverseGeo.Core.ApplicationRole;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using ImmichReverseGeo.Web.LifecycleTelemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Role = ImmichReverseGeo.Core.ApplicationRole.ApplicationRole;

namespace ImmichReverseGeo.Tests.LifecycleTelemetry;

[TestClass]
[TestCategory("Change66")]
public sealed class RoleLifetimeTests
{
    [TestMethod]
    public void FirstStopTimestampSurvivesLateFailureRepeatedSignalsAndWallClockIsNeverRead()
    {
        var clock = new Clock();
        using var capture = new RoleLogCapture(Role.RunOnce, DeploymentMode.RunOnce, clock);
        clock.Now = 7;
        capture.Telemetry.Ready();
        capture.Telemetry.Ready();
        clock.Now = 12;
        capture.Telemetry.Stopping(RoleStopReason.Completed);
        clock.Now = 20;
        capture.Telemetry.Failed();
        capture.Telemetry.Stopping(RoleStopReason.HostShutdown);
        clock.Now = 31;
        capture.Telemetry.Stopped(WorkerProcessExitFact.CleanupInfrastructure());
        capture.Telemetry.Stopped(WorkerProcessExitFact.Completed());
        capture.AssertCompleted("completed", "failed");
        Assert.AreEqual(7L, capture.Logs.Entries.Single(entry => entry.Event.Id == 6603)["startup_duration_ms"]);
        Assert.AreEqual(31L, capture.Logs.Entries.Last()["process_duration_ms"]);
        Assert.AreEqual(19L, capture.Logs.Entries.Last()["stop_duration_ms"]);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RealListeningWebHostLogsReadinessAndShutdownAfterProviderCleanup(bool webOnly)
    {
        DeploymentMode mode = webOnly ? DeploymentMode.WebOnly : DeploymentMode.Standard;
        using var capture = new RoleLogCapture(Role.Web, mode);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        builder.Services.AddSingleton(_ => new DisposalProbe(disposed));
        var app = builder.Build();
        app.Services.GetRequiredService<DisposalProbe>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var startedRegistration = app.Lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        Task run = Task.Run(() => WebRoleLifetime.Run(() => app, capture.Telemetry));
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
            Assert.HasCount(1, addresses);
            Assert.IsFalse(disposed.Task.IsCompleted);
        }
        finally
        {
            app.Lifetime.StopApplication();
            await run.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.IsTrue(disposed.Task.IsCompletedSuccessfully);
        capture.AssertCompleted("host-shutdown", "cancelled");
        Assert.AreEqual("web-listening", capture.Logs.Entries.Single(entry => entry.Event.Id == 6603)["readiness_kind"]);
    }

    [TestMethod]
    public void WebBuildFailureKeepsExceptionAndNeverClaimsReadiness()
    {
        using var capture = new RoleLogCapture(Role.Web, DeploymentMode.Standard);
        var failure = new InvalidOperationException("secret build detail");
        Exception thrown = Assert.ThrowsExactly<InvalidOperationException>(
            () => WebRoleLifetime.Run(() => throw failure, capture.Telemetry));
        Assert.AreSame(failure, thrown);
        capture.AssertCompleted("startup-failure", "failed", ready: false);
    }

    private sealed class DisposalProbe(TaskCompletionSource disposed) : IDisposable
    {
        public void Dispose() => disposed.TrySetResult();
    }

    private sealed class Clock : TimeProvider
    {
        internal long Now { get; set; }
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Now;
        public override DateTimeOffset GetUtcNow() => throw new AssertFailedException("UTC is not a duration clock.");
    }
}
