using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ImmichReverseGeo.Web.LifecycleTelemetry;

internal static class WebRoleLifetime
{
    internal static void Run(Func<IHost> buildHost, RoleProcessTelemetry telemetry)
    {
        bool failed = false;
        try
        {
            IHost host = buildHost();
            System.Threading.CancellationTokenRegistration ready = default;
            System.Threading.CancellationTokenRegistration stopping = default;
            try
            {
                try
                {
                    var lifetime = host.Services.GetService<IHostApplicationLifetime>();
                    if (lifetime is not null)
                    {
                        ready = lifetime.ApplicationStarted.Register(telemetry.Ready);
                        stopping = lifetime.ApplicationStopping.Register(
                            () => telemetry.Stopping(RoleStopReason.HostShutdown));
                    }
                }
                catch
                {
                    // Observing a role cannot replace the host's startup policy.
                }

                // Run owns shutdown and host disposal. Keep final logging outside it.
                host.Run();
            }
            finally
            {
                ready.Dispose();
                stopping.Dispose();
            }
        }
        catch
        {
            failed = true;
            telemetry.Failed();
            throw;
        }
        finally
        {
            telemetry.Stopped(failed ? RoleStopReason.FatalFailure : RoleStopReason.HostShutdown);
        }
    }
}
