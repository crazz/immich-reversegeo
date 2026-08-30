using System;
using System.Threading;
using System.Threading.Tasks;
using Cronos;

namespace ImmichReverseGeo.Web.Services;

public sealed record ProcessingScheduleSnapshot(bool Enabled, string Cron);

public interface IProcessingScheduleConfiguration
{
    Task<ProcessingScheduleSnapshot> GetSnapshotAsync();
}

internal abstract record ProcessingSchedulePlan;

internal sealed record DisabledRetry(TimeSpan RetryAfter) : ProcessingSchedulePlan
{
    public static DisabledRetry Instance { get; } = new(TimeSpan.FromMinutes(1));
}

internal sealed record InvalidRetry(TimeSpan RetryAfter) : ProcessingSchedulePlan
{
    public static InvalidRetry Instance { get; } = new(TimeSpan.FromMinutes(5));
}

internal sealed record Due(DateTimeOffset DueAtUtc) : ProcessingSchedulePlan;

internal static class ProcessingScheduleCalculator
{
    internal static ProcessingSchedulePlan Calculate(
        bool enabled,
        string cron,
        DateTimeOffset utcNow)
    {
        if (utcNow.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Schedule evaluation requires a zero-offset UTC instant.", nameof(utcNow));
        }

        if (!enabled)
        {
            return DisabledRetry.Instance;
        }

        try
        {
            var expression = CronExpression.Parse(cron, CronFormat.Standard);
            var next = expression.GetNextOccurrence(utcNow.UtcDateTime, TimeZoneInfo.Utc);
            return next is null
                ? InvalidRetry.Instance
                : new Due(new DateTimeOffset(DateTime.SpecifyKind(next.Value, DateTimeKind.Utc)));
        }
        catch (CronFormatException)
        {
            return InvalidRetry.Instance;
        }
        catch (ArgumentException)
        {
            return InvalidRetry.Instance;
        }
    }
}

internal enum ScheduledTriggerResult
{
    RejectedAlreadyRunning,
    AcceptedAfterTerminal
}

// Change 12 is implemented directly by ProcessingBackgroundService to avoid a DI back-edge.
// Block 13 replaces this temporary owner while preserving Scheduled identity, accepted-after-terminal
// completion, contention, hosted-token, clock/wait, config-reevaluation, and next-run visibility semantics.
internal interface IScheduledRunTrigger
{
    Task<ScheduledTriggerResult> TriggerScheduledAsync(CancellationToken stoppingToken);
}

internal sealed class ProcessingScheduleLoop(
    IProcessingScheduleConfiguration configuration,
    TimeProvider timeProvider,
    Action<string> appendLog,
    IScheduledRunTrigger trigger)
{
    internal async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var snapshot = await configuration.GetSnapshotAsync().ConfigureAwait(false);
            var plan = ProcessingScheduleCalculator.Calculate(
                snapshot.Enabled,
                snapshot.Cron,
                timeProvider.GetUtcNow().ToUniversalTime());

            if (plan is DisabledRetry disabled)
            {
                await Task.Delay(disabled.RetryAfter, timeProvider, stoppingToken).ConfigureAwait(false);
                continue;
            }

            if (plan is InvalidRetry invalid)
            {
                await Task.Delay(invalid.RetryAfter, timeProvider, stoppingToken).ConfigureAwait(false);
                continue;
            }

            var due = (Due)plan;
            var delay = due.DueAtUtc - timeProvider.GetUtcNow().ToUniversalTime();
            if (delay > TimeSpan.Zero)
            {
                appendLog($"Next run scheduled at {due.DueAtUtc.UtcDateTime:u}");
                await Task.Delay(delay, timeProvider, stoppingToken).ConfigureAwait(false);
            }

            stoppingToken.ThrowIfCancellationRequested();
            await trigger.TriggerScheduledAsync(stoppingToken).ConfigureAwait(false);
        }
    }
}
