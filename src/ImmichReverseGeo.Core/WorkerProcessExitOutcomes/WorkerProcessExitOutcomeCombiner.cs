using System;

namespace ImmichReverseGeo.Core.WorkerProcessExitOutcomes;

/// <summary>
/// Accumulates managed orderly outcomes independently of observation order.
/// </summary>
public sealed class WorkerProcessExitOutcomeAccumulator
{
    private readonly object _gate = new();
    private WorkerProcessExitFact _fact = WorkerProcessExitFact.Completed();
    private bool _hasFact;

    /// <summary>Gets whether managed control flow has observed a mapped fact.</summary>
    public bool HasFact
    {
        get
        {
            lock (_gate)
            {
                return _hasFact;
            }
        }
    }

    /// <summary>Gets the current deterministic closed fact.</summary>
    public WorkerProcessExitFact Fact
    {
        get
        {
            lock (_gate)
            {
                return _fact;
            }
        }
    }

    /// <summary>Adds one managed orderly closed fact.</summary>
    public void Add(WorkerProcessExitFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);

        lock (_gate)
        {
            _fact = _hasFact ? WorkerProcessExitFact.Combine(_fact, fact) : fact;
            _hasFact = true;
        }
    }
}
