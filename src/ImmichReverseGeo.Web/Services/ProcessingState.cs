using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ImmichReverseGeo.Web.Services;

/// <summary>
/// Singleton shared between ProcessingBackgroundService and Blazor UI pages.
/// All mutations are thread-safe via Interlocked or lock.
/// UI subscribes to OnChanged to receive real-time updates.
/// </summary>
public class ProcessingState
{
    private volatile bool _isRunning;
    private long _totalUnprocessed;
    private readonly object _stateLock = new();
    private readonly object _activityLock = new();
    private DateTime? _lastRunStarted;
    private DateTime? _lastRunCompleted;
    private string? _lastError;
    private readonly Dictionary<string, int> _activityCounts = new(StringComparer.Ordinal);

    public bool IsRunning => _isRunning;
    public long TotalUnprocessed => Volatile.Read(ref _totalUnprocessed);
    public long ProcessedThisRun => Volatile.Read(ref _progress).Processed;
    public long ErrorsThisRun => Volatile.Read(ref _progress).Errors;
    public long SkippedThisRun => Volatile.Read(ref _progress).Skipped;
    public DateTime? LastRunStarted
    {
        get { lock (_stateLock) { return _lastRunStarted; } }
    }

    public DateTime? LastRunCompleted
    {
        get { lock (_stateLock) { return _lastRunCompleted; } }
    }

    public string? LastError
    {
        get { lock (_stateLock) { return _lastError; } }
    }

    // Ring buffer of last 100 log lines
    private readonly Queue<string> _recentLog = new(capacity: 100);
    public IReadOnlyCollection<string> RecentLog => _recentLog;

    private volatile string? _currentActivity;
    /// <summary>Short description of what the run is doing right now (e.g. "Downloading ARE boundaries…").</summary>
    public string? CurrentActivity => _currentActivity;

    public void SetActivity(string? activity)
    {
        lock (_activityLock)
        {
            _currentActivity = activity;
            if (activity is null)
            {
                _activityCounts.Clear();
            }
        }
        Notify();
    }

    public IDisposable BeginActivity(string activity)
    {
        lock (_activityLock)
        {
            _activityCounts.TryGetValue(activity, out var currentCount);
            _activityCounts[activity] = currentCount + 1;
            _currentActivity = activity;
        }

        Notify();
        return new ActivityScope(this, activity);
    }

    public event Action? OnChanged;

    /// <summary>
    /// Called immediately when a run is triggered, before the background task starts,
    /// so the UI disables the Run Now button on the same render cycle.
    /// </summary>
    public void MarkPending()
    {
        _isRunning = true;
        Notify();
    }

    internal void ClearPending()
    {
        _isRunning = false;
        Notify();
    }

    public void StartRun(long totalUnprocessed)
    {
        _isRunning = true;
        Interlocked.Exchange(ref _progress, ProgressSnapshot.Empty);
        Volatile.Write(ref _totalUnprocessed, totalUnprocessed);
        lock (_stateLock)
        {
            _lastRunStarted = DateTime.UtcNow;
            _lastError = null;
        }
        Notify();
    }

    public void IncrementProcessed()
    {
        UpdateProgress(snapshot => snapshot with { Processed = checked(snapshot.Processed + 1) });
        Notify();
    }

    public void IncrementError(string message)
    {
        UpdateProgress(snapshot => snapshot with { Errors = checked(snapshot.Errors + 1) });
        lock (_stateLock)
        {
            _lastError = message;
        }
        AppendLog($"[ERROR] {message}");
        Notify();
    }

    public void IncrementSkipped()
    {
        UpdateProgress(snapshot => snapshot with { Skipped = checked(snapshot.Skipped + 1) });
        Notify();
    }

    internal void ApplyProgress(long updatedCount, long skippedCount, long failedCount)
    {
        Interlocked.Exchange(ref _progress, new ProgressSnapshot(updatedCount, skippedCount, failedCount));
        Notify();
    }

    internal void ReportErrorDiagnostic(string message)
    {
        lock (_stateLock)
        {
            _lastError = message;
        }

        AppendLog($"[ERROR] {message}");
    }

    internal void RestoreFatalFailureSnapshot(long updatedCount, long skippedCount, long failedCount, string fatalMessage)
    {
        var errors = checked(failedCount + 1);
        Interlocked.Exchange(ref _progress, new ProgressSnapshot(updatedCount, skippedCount, errors));
        _isRunning = false;
        lock (_activityLock)
        {
            _currentActivity = null;
            _activityCounts.Clear();
        }

        lock (_stateLock)
        {
            _lastError = fatalMessage;
            _lastRunCompleted = DateTime.UtcNow;
        }

        var summary = $"Run complete. Processed={updatedCount} Skipped={skippedCount} Errors={errors}";
        lock (_recentLog)
        {
            var retained = _recentLog.ToList();
            if (retained.Count > 0 && retained[^1].Contains("Run complete. Processed=", StringComparison.Ordinal))
            {
                retained.RemoveAt(retained.Count - 1);
            }

            if (retained.Count > 0 && retained[^1].EndsWith($"[ERROR] {fatalMessage}", StringComparison.Ordinal))
            {
                retained.RemoveAt(retained.Count - 1);
            }

            retained.Add($"[{DateTime.UtcNow:HH:mm:ss}] [ERROR] {fatalMessage}");
            retained.Add($"[{DateTime.UtcNow:HH:mm:ss}] {summary}");
            _recentLog.Clear();
            foreach (var line in retained.TakeLast(100))
            {
                _recentLog.Enqueue(line);
            }
        }

        Notify();
    }

    public void CompleteRun()
    {
        _isRunning = false;
        lock (_activityLock)
        {
            _currentActivity = null;
            _activityCounts.Clear();
        }
        lock (_stateLock)
        {
            _lastRunCompleted = DateTime.UtcNow;
        }
        Notify();
    }

    public void AppendLog(string line)
    {
        lock (_recentLog)
        {
            if (_recentLog.Count >= 100)
            {
                _recentLog.Dequeue();
            }

            _recentLog.Enqueue($"[{DateTime.UtcNow:HH:mm:ss}] {line}");
        }
        Notify();
    }

    public IReadOnlyList<string> GetRecentLog()
    {
        lock (_recentLog)
        {
            return _recentLog.ToArray();
        }
    }

    internal ProgressSnapshot ReadProgressSnapshot() => Volatile.Read(ref _progress);

    private void UpdateProgress(Func<ProgressSnapshot, ProgressSnapshot> update)
    {
        while (true)
        {
            var current = Volatile.Read(ref _progress);
            var next = update(current);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _progress, next, current), current))
            {
                return;
            }
        }
    }

    private ProgressSnapshot _progress = ProgressSnapshot.Empty;
    internal sealed record ProgressSnapshot(long Processed, long Skipped, long Errors)
    {
        public static ProgressSnapshot Empty { get; } = new(0, 0, 0);
    }

    private void Notify() => OnChanged?.Invoke();

    private void EndActivity(string activity)
    {
        lock (_activityLock)
        {
            if (_activityCounts.TryGetValue(activity, out var count))
            {
                if (count <= 1)
                {
                    _activityCounts.Remove(activity);
                }
                else
                {
                    _activityCounts[activity] = count - 1;
                }
            }

            _currentActivity = _activityCounts.Count > 0
                ? _activityCounts.Keys.Last()
                : null;
        }

        Notify();
    }

    private sealed class ActivityScope(ProcessingState state, string activity) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                state.EndActivity(activity);
            }
        }
    }
}
