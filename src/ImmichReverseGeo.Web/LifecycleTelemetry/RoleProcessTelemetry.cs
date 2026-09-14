using System;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Web.LifecycleTelemetry;

// Observes boundaries selected by the role owner. It never controls the host or
// adds exit facts. The optional provider belongs to this process scope so the
// final event can still be written after the host has disposed its services.
internal sealed class RoleProcessTelemetry : IDisposable
{
    private readonly object _gate = new();
    private readonly ILogger _logger;
    private readonly RoleLogContext _context;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory? _ownedFactory;
    private readonly long? _startedAt;
    private long? _stoppingAt;
    private bool _ready;
    private bool _stopping;
    private bool _stopped;

    internal RoleProcessTelemetry(ILogger logger, RoleLogContext context, TimeProvider time)
        : this(logger, context, time, null)
    {
    }

    private RoleProcessTelemetry(ILogger logger, RoleLogContext context, TimeProvider time, ILoggerFactory? ownedFactory)
    {
        _logger = logger;
        _context = context;
        _time = time;
        _ownedFactory = ownedFactory;
        _startedAt = LifecycleElapsed.Timestamp(time);
        LifecycleEventCatalog.ModeSelected(logger, context);
        LifecycleEventCatalog.RoleStarting(logger, context);
    }

    internal static RoleProcessTelemetry CreateProduction(RoleLogContext context)
    {
        ILoggerFactory? factory = null;
        ILogger logger = NullLogger.Instance;
        try
        {
            factory = LoggerFactory.Create(builder => builder.AddConsole(
                options => options.LogToStandardErrorThreshold = LogLevel.Trace));
            logger = factory.CreateLogger(LifecycleEventCatalog.Category);
        }
        catch
        {
            // Logger initialization cannot replace the role's exit policy.
        }

        return new(logger, context, TimeProvider.System, factory);
    }

    internal void Ready()
    {
        lock (_gate)
        {
            if (_ready || _stopping)
            {
                return;
            }

            _ready = true;
            LifecycleEventCatalog.RoleReady(_logger, _context,
                LifecycleElapsed.Milliseconds(_time, _startedAt, LifecycleElapsed.Timestamp(_time)));
        }
    }

    internal void Stopping(RoleStopReason reason)
    {
        lock (_gate)
        {
            if (_stopping)
            {
                return;
            }

            _stopping = true;
            _stoppingAt = LifecycleElapsed.Timestamp(_time);
            LifecycleEventCatalog.RoleStopping(_logger, _context, reason);
        }
    }

    internal void Stopping(WorkerProcessExitFact fact, bool fatal = false)
    {
        lock (_gate)
        {
            Stopping(!fatal && ReferenceEquals(fact, WorkerProcessExitFact.Completed()) ? RoleStopReason.Completed
                : !fatal && ReferenceEquals(fact, WorkerProcessExitFact.ShutdownCancelled()) ? RoleStopReason.HostShutdown
                : _ready ? RoleStopReason.FatalFailure : RoleStopReason.StartupFailure);
        }
    }

    internal void Failed()
    {
        lock (_gate)
        {
            Stopping(_ready ? RoleStopReason.FatalFailure : RoleStopReason.StartupFailure);
        }
    }

    internal void Stopped(WorkerProcessExitFact fact, bool fatal = false) =>
        Stopped(!fatal && ReferenceEquals(fact, WorkerProcessExitFact.Completed()) ? RoleStopReason.Completed
            : !fatal && ReferenceEquals(fact, WorkerProcessExitFact.ShutdownCancelled()) ? RoleStopReason.HostShutdown
            : RoleStopReason.FatalFailure);

    // The reason supplied here projects the final outcome; it is intentionally
    // independent of the first reason already captured by Stopping.
    internal void Stopped(RoleStopReason finalReason)
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            Stopping(finalReason);
            _stopped = true;
            long? now = LifecycleElapsed.Timestamp(_time);
            LifecycleEventCatalog.RoleStopped(_logger, _context, finalReason,
                LifecycleElapsed.Milliseconds(_time, _startedAt, now),
                LifecycleElapsed.Milliseconds(_time, _stoppingAt, now));
        }
    }

    public void Dispose()
    {
        try
        {
            _ownedFactory?.Dispose();
        }
        catch
        {
        }
    }
}
