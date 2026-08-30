using System;

namespace ImmichReverseGeo.Core.Models;

/// <summary>
/// Provides the immutable terminal summary of one processing run.
/// </summary>
/// <remarks>
/// <para><see cref="ProcessedCount"/> counts assets that reached a terminal per-asset disposition.</para>
/// <para><see cref="UpdatedCount"/> counts successful Immich location writes.</para>
/// <para><see cref="SkippedCount"/> and <see cref="FailedCount"/> count per-asset dispositions.</para>
/// <para>An empty run has zero for all counts. A fatal <see cref="ProcessingRunOutcome.Failed"/> outcome does not increment <see cref="FailedCount"/>.</para>
/// </remarks>
public sealed record ProcessingRunResult
{
    /// <summary>
    /// Gets the request that originated this processing run.
    /// </summary>
    public ProcessingRunRequest Request { get; }

    /// <summary>
    /// Gets when execution began, expressed with a zero UTC offset.
    /// </summary>
    public DateTimeOffset StartedAtUtc { get; }

    /// <summary>
    /// Gets when execution ended, expressed with a zero UTC offset.
    /// </summary>
    public DateTimeOffset EndedAtUtc { get; }

    /// <summary>
    /// Gets the number of assets that reached one terminal per-asset disposition.
    /// </summary>
    public long ProcessedCount { get; }

    /// <summary>
    /// Gets the number of successful Immich location writes.
    /// </summary>
    public long UpdatedCount { get; }

    /// <summary>
    /// Gets the number of actively evaluated assets deliberately left without an update.
    /// </summary>
    public long SkippedCount { get; }

    /// <summary>
    /// Gets the number of handled per-asset processing exceptions.
    /// </summary>
    public long FailedCount { get; }

    /// <summary>
    /// Gets the terminal outcome of the processing run.
    /// </summary>
    public ProcessingRunOutcome Outcome { get; }

    /// <summary>
    /// Gets failure detail for a fatal processing run failure; otherwise <see langword="null"/>.
    /// </summary>
    public string? FailureMessage { get; }

    /// <summary>
    /// Initializes a validated immutable terminal processing run summary.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A timestamp is not UTC, the end precedes the start, or failure detail does not match the outcome.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A count is negative, its aggregate overflows, or <paramref name="outcome"/> is undefined.</exception>
    public ProcessingRunResult(
        ProcessingRunRequest request,
        DateTimeOffset startedAtUtc,
        DateTimeOffset endedAtUtc,
        long processedCount,
        long updatedCount,
        long skippedCount,
        long failedCount,
        ProcessingRunOutcome outcome,
        string? failureMessage)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (startedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The start timestamp must have a zero UTC offset.", nameof(startedAtUtc));
        }

        if (endedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The end timestamp must have a zero UTC offset.", nameof(endedAtUtc));
        }

        if (endedAtUtc < startedAtUtc)
        {
            throw new ArgumentException("The end timestamp must not precede the start timestamp.", nameof(endedAtUtc));
        }

        ValidateNonNegative(processedCount, nameof(processedCount));
        ValidateNonNegative(updatedCount, nameof(updatedCount));
        ValidateNonNegative(skippedCount, nameof(skippedCount));
        ValidateNonNegative(failedCount, nameof(failedCount));

        long classifiedCount;
        try
        {
            classifiedCount = checked(updatedCount + skippedCount + failedCount);
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(nameof(processedCount), "The terminal per-asset counts overflow.");
        }

        if (processedCount != classifiedCount)
        {
            throw new ArgumentException("Processed count must equal updated, skipped, and failed counts combined.", nameof(processedCount));
        }

        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "The processing run outcome must be defined.");
        }

        if (outcome == ProcessingRunOutcome.Failed)
        {
            if (string.IsNullOrWhiteSpace(failureMessage))
            {
                throw new ArgumentException("A failed processing run must have a failure message.", nameof(failureMessage));
            }
        }
        else if (failureMessage is not null)
        {
            throw new ArgumentException("Only a failed processing run may have a failure message.", nameof(failureMessage));
        }

        Request = request;
        StartedAtUtc = startedAtUtc;
        EndedAtUtc = endedAtUtc;
        ProcessedCount = processedCount;
        UpdatedCount = updatedCount;
        SkippedCount = skippedCount;
        FailedCount = failedCount;
        Outcome = outcome;
        FailureMessage = failureMessage;
    }

    private static void ValidateNonNegative(long value, string parameterName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "The count must not be negative.");
        }
    }
}
