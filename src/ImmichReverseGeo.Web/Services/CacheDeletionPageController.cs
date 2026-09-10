using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.WorkerJobs;

namespace ImmichReverseGeo.Web.Services;

internal enum CacheDeletionPagePhase
{
    Idle,
    Confirming,
    Deleting,
    Reloading,
    Completed,
    Busy,
    Unavailable,
    Failed
}

internal sealed record CacheDeletionConfirmation(
    long Generation,
    CacheMutationSource Source,
    IReadOnlyList<CacheDeletionTarget> Targets,
    bool IsBatch,
    string Prompt);

internal sealed record CacheDeletionPageState(
    CacheDeletionPagePhase Phase,
    CacheMutationSource? Source,
    string? Status,
    string? Error,
    string? ReloadError,
    CacheDeletionOperationResult? Result,
    CacheDeletionConfirmation? Confirmation)
{
    internal static CacheDeletionPageState Idle { get; } = new(
        CacheDeletionPagePhase.Idle,
        null,
        null,
        null,
        null,
        null,
        null);

    internal bool IsActive => Phase is
        CacheDeletionPagePhase.Deleting or
        CacheDeletionPagePhase.Reloading;

    internal bool ControlsDisabled => IsActive || Phase == CacheDeletionPagePhase.Confirming;
}

internal sealed class CacheDeletionPageControllerFactory
{
    private readonly CacheDeletionCommand _command;

    public CacheDeletionPageControllerFactory(CacheDeletionCommand command)
    {
        _command = command ?? throw new ArgumentNullException(nameof(command));
    }

    internal CacheDeletionPageController Create(
        Func<Task> stateChanged,
        Func<Task> reloadStatus)
    {
        ArgumentNullException.ThrowIfNull(stateChanged);
        ArgumentNullException.ThrowIfNull(reloadStatus);
        return new CacheDeletionPageController(
            _command.DeleteAsync,
            _command.DeleteAllAsync,
            stateChanged,
            reloadStatus);
    }
}

internal sealed class CacheDeletionPageController : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Func<CacheDeletionTarget, Task<CacheDeletionOperationResult>> _delete;
    private readonly Func<CacheMutationSource, IEnumerable<CacheDeletionTarget>,
        Task<CacheDeletionOperationResult>> _deleteAll;
    private readonly Func<Task> _stateChanged;
    private readonly Func<Task> _reloadStatus;
    private readonly TaskCompletionSource _disposedCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CacheDeletionPageState _state = CacheDeletionPageState.Idle;
    private CacheDeletionConfirmation? _pendingConfirmation;
    private Task _currentAttempt = Task.CompletedTask;
    private long _generation;
    private bool _attemptInProgress;
    private bool _disposeStarted;
    private bool _disposed;

    internal CacheDeletionPageController(
        Func<CacheDeletionTarget, Task<CacheDeletionOperationResult>> delete,
        Func<CacheMutationSource, IEnumerable<CacheDeletionTarget>,
            Task<CacheDeletionOperationResult>> deleteAll,
        Func<Task> stateChanged,
        Func<Task> reloadStatus)
    {
        _delete = delete ?? throw new ArgumentNullException(nameof(delete));
        _deleteAll = deleteAll ?? throw new ArgumentNullException(nameof(deleteAll));
        _stateChanged = stateChanged ?? throw new ArgumentNullException(nameof(stateChanged));
        _reloadStatus = reloadStatus ?? throw new ArgumentNullException(nameof(reloadStatus));
    }

    internal CacheDeletionPageState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    internal void RequestDelete(CacheDeletionTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        string sourceName = SourceName(target.Source);
        RequestConfirmation(
            target.Source,
            isBatch: false,
            targets: Array.AsReadOnly(new[] { target }),
            prompt: $"Delete the {sourceName} cache for {target.Iso3}? The cache can be downloaded again later.");
    }

    internal void RequestDeleteAll(
        CacheMutationSource source,
        IEnumerable<CacheDeletionTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        CacheDeletionTarget[] snapshot = targets.ToArray();
        IReadOnlyList<CacheDeletionTarget> immutableSnapshot = Array.AsReadOnly(snapshot);
        RequestConfirmation(
            source,
            isBatch: true,
            targets: immutableSnapshot,
            prompt: $"Delete all {SourceName(source)} cache files? This is a best-effort operation; "
            + "successful deletions are kept and any failures are reported.");
    }

    private void RequestConfirmation(
        CacheMutationSource source,
        bool isBatch,
        IReadOnlyList<CacheDeletionTarget> targets,
        string prompt)
    {
        lock (_gate)
        {
            if (_disposed || _attemptInProgress)
            {
                return;
            }

            long generation = ++_generation;
            _pendingConfirmation = new CacheDeletionConfirmation(
                generation,
                source,
                targets,
                isBatch,
                prompt);
            _state = new CacheDeletionPageState(
                CacheDeletionPagePhase.Confirming,
                source,
                null,
                null,
                null,
                null,
                _pendingConfirmation);
        }

        Notify();
    }

    internal void DismissConfirmation(long generation)
    {
        lock (_gate)
        {
            if (_disposed
                || _attemptInProgress
                || _pendingConfirmation?.Generation != generation)
            {
                return;
            }

            _generation++;
            _pendingConfirmation = null;
            _state = CacheDeletionPageState.Idle;
        }

        Notify();
    }

    internal Task ConfirmAsync(long generation)
    {
        CacheDeletionConfirmation? confirmation;
        lock (_gate)
        {
            if (_disposed
                || _attemptInProgress
                || _pendingConfirmation?.Generation != generation)
            {
                return Task.CompletedTask;
            }

            confirmation = _pendingConfirmation;
            _pendingConfirmation = null;
        }

        return confirmation.IsBatch
            ? StartAttemptAsync(
                confirmation.Source,
                isBatch: true,
                execute: () => _deleteAll(confirmation.Source, confirmation.Targets))
            : StartAttemptAsync(
                confirmation.Source,
                isBatch: false,
                execute: () => _delete(confirmation.Targets[0]));
    }

    public async ValueTask DisposeAsync()
    {
        Task attempt;
        bool owner;
        lock (_gate)
        {
            owner = !_disposeStarted;
            if (owner)
            {
                _disposeStarted = true;
                _disposed = true;
                _generation++;
                _pendingConfirmation = null;
                attempt = _currentAttempt;
            }
            else
            {
                attempt = Task.CompletedTask;
            }
        }

        if (owner)
        {
            try
            {
                await attempt.ConfigureAwait(false);
            }
            finally
            {
                _disposedCompletion.TrySetResult();
            }
        }

        await _disposedCompletion.Task.ConfigureAwait(false);
    }

    private async Task StartAttemptAsync(
        CacheMutationSource source,
        bool isBatch,
        Func<Task<CacheDeletionOperationResult>> execute)
    {
        TaskCompletionSource completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        long generation;
        lock (_gate)
        {
            if (_disposed || _attemptInProgress || _pendingConfirmation is not null)
            {
                return;
            }

            _attemptInProgress = true;
            generation = ++_generation;
            _currentAttempt = completion.Task;
            _state = new CacheDeletionPageState(
                CacheDeletionPagePhase.Deleting,
                source,
                "Deleting cache data…",
                null,
                null,
                null,
                null);
        }

        Notify();
        try
        {
            await RunAttemptAsync(generation, source, isBatch, execute).ConfigureAwait(false);
        }
        finally
        {
            completion.TrySetResult();
        }
    }

    private async Task RunAttemptAsync(
        long generation,
        CacheMutationSource source,
        bool isBatch,
        Func<Task<CacheDeletionOperationResult>> execute)
    {
        CacheDeletionOperationResult? result = null;
        try
        {
            result = await execute().ConfigureAwait(false);
        }
        catch
        {
            CompleteUnexpectedFailure(generation, source);
            return;
        }

        if (result.Disposition != CacheDeletionOperationDisposition.Completed)
        {
            CompleteRejected(generation, source, result);
            return;
        }

        string status = FormatCompleted(source, result, isBatch);
        lock (_gate)
        {
            if (!CanMutate(generation))
            {
                return;
            }

            _state = new CacheDeletionPageState(
                CacheDeletionPagePhase.Reloading,
                source,
                status,
                null,
                null,
                result,
                null);
        }

        Notify();
        string? reloadError = null;
        if (CanInvokeCallback(generation))
        {
            try
            {
                await _reloadStatus().ConfigureAwait(false);
            }
            catch
            {
                reloadError = "Cache status could not be refreshed. The deletion result is final; reload the page to read the current cache status.";
            }
        }

        lock (_gate)
        {
            if (!CanMutate(generation))
            {
                return;
            }

            _attemptInProgress = false;
            _state = new CacheDeletionPageState(
                CacheDeletionPagePhase.Completed,
                source,
                status,
                null,
                reloadError,
                result,
                null);
        }

        Notify();
    }

    private void CompleteRejected(
        long generation,
        CacheMutationSource source,
        CacheDeletionOperationResult result)
    {
        lock (_gate)
        {
            if (!CanMutate(generation))
            {
                return;
            }

            _attemptInProgress = false;
            _state = new CacheDeletionPageState(
                result.Disposition == CacheDeletionOperationDisposition.Busy
                    ? CacheDeletionPagePhase.Busy
                    : CacheDeletionPagePhase.Unavailable,
                source,
                result.Disposition == CacheDeletionOperationDisposition.Busy
                    ? "Cache deletion could not start because another operation is active."
                    : "Cache deletion is unavailable.",
                result.Disposition == CacheDeletionOperationDisposition.Unavailable
                    ? FormatUnavailable(result)
                    : null,
                null,
                result,
                null);
        }

        Notify();
    }

    private void CompleteUnexpectedFailure(long generation, CacheMutationSource source)
    {
        lock (_gate)
        {
            if (!CanMutate(generation))
            {
                return;
            }

            _attemptInProgress = false;
            _state = new CacheDeletionPageState(
                CacheDeletionPagePhase.Failed,
                source,
                "Cache deletion failed.",
                "The cache deletion could not be completed. Check the application logs and try again.",
                null,
                null,
                null);
        }

        Notify();
    }

    private bool CanInvokeCallback(long generation)
    {
        lock (_gate)
        {
            return CanMutate(generation);
        }
    }

    private bool CanMutate(long generation) => !_disposed && generation == _generation;

    private void Notify()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        _ = InvokeSafelyAsync(_stateChanged);
    }

    private static async Task InvokeSafelyAsync(Func<Task> callback)
    {
        try
        {
            await callback().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static string FormatCompleted(
        CacheMutationSource source,
        CacheDeletionOperationResult result,
        bool isBatch)
    {
        string sourceName = SourceName(source);
        if (result.Targets.Count == 0)
        {
            return $"No {sourceName} cache files were selected for deletion.";
        }

        if (!isBatch && result.Targets.Count == 1)
        {
            CacheDeletionTargetResult target = result.Targets[0];
            return target.Disposition switch
            {
                CacheDeletionTargetDisposition.Deleted =>
                    $"Deleted the {sourceName} cache for {target.Iso3}.",
                CacheDeletionTargetDisposition.Missing =>
                    $"The {sourceName} cache for {target.Iso3} is already absent.",
                CacheDeletionTargetDisposition.Invalid =>
                    $"The selected {sourceName} cache target is invalid.",
                _ => $"The {sourceName} cache for {target.Iso3} could not be deleted."
            };
        }

        string counts = $"{result.DeletedCount} deleted, {result.MissingCount} already absent, "
            + $"{result.InvalidCount} invalid, {result.FailedCount} failed";
        if (result.FailedCount + result.InvalidCount > 0
            && result.DeletedCount + result.MissingCount > 0)
        {
            return $"{sourceName} cache deletion partially completed: {counts}.";
        }

        if (result.FailedCount > 0 || result.InvalidCount == result.Targets.Count)
        {
            return $"{sourceName} cache deletion did not complete: {counts}.";
        }

        return $"{sourceName} cache deletion completed: {counts}.";
    }

    private static string FormatUnavailable(CacheDeletionOperationResult result)
    {
        if (string.IsNullOrWhiteSpace(result.Code))
        {
            return result.Message ?? "The application is not accepting cache deletion requests.";
        }

        return $"{result.Message ?? "The application is not accepting cache deletion requests."} ({result.Code})";
    }

    private static string SourceName(CacheMutationSource source) => source switch
    {
        CacheMutationSource.Overture => "Overture",
        CacheMutationSource.Gadm => "GADM",
        _ => "administrative area"
    };
}
