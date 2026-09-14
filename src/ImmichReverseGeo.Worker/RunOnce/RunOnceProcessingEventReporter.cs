using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Processing;

namespace ImmichReverseGeo.Web.RunOnce;

internal sealed class RunOnceProcessingEventReporter(
    TextWriter standardOutput,
    TextWriter standardError) : ProcessingEventReporter
{
    protected override ValueTask AcceptAsync(
        ProcessingEvent processingEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processingEvent);

        return processingEvent switch
        {
            RunStarted => WriteBestEffortAsync(standardOutput, "Run started."),
            EligibilityDetermined eligibility when eligibility.EligibleCount == 0 =>
                WriteBestEffortAsync(standardOutput, "Eligible assets: 0. Nothing to process."),
            EligibilityDetermined eligibility =>
                WriteBestEffortAsync(standardOutput, $"Eligible assets: {eligibility.EligibleCount}."),
            ProgressChanged progress => WriteBestEffortAsync(
                standardOutput,
                $"Progress: processed={progress.Progress.ProcessedCount} updated={progress.Progress.UpdatedCount} skipped={progress.Progress.SkippedCount} failed={progress.Progress.FailedCount}."),
            ActivityStarted => WriteBestEffortAsync(standardOutput, "Processing activity started."),
            ActivityEnded => WriteBestEffortAsync(standardOutput, "Processing activity finished."),
            LogEmitted { Level: ProcessingLogLevel.Warning } =>
                WriteBestEffortAsync(standardError, "Processing reported a warning message."),
            LogEmitted { Level: ProcessingLogLevel.Error } =>
                WriteBestEffortAsync(standardError, "Processing reported an error message."),
            LogEmitted => WriteBestEffortAsync(standardOutput, "Processing reported an informational message."),
            RunFinished finished when finished.Result.Outcome == Core.Models.ProcessingRunOutcome.Completed =>
                WriteBestEffortAsync(standardOutput, TerminalSummary("Run completed", finished)),
            RunFinished finished when finished.Result.Outcome == Core.Models.ProcessingRunOutcome.Cancelled =>
                WriteBestEffortAsync(standardError, TerminalSummary("Run cancelled", finished)),
            RunFinished finished => WriteBestEffortAsync(standardError, TerminalSummary("Run failed", finished)),
            _ => ValueTask.CompletedTask
        };
    }

    private static string TerminalSummary(string prefix, RunFinished finished)
    {
        var result = finished.Result;
        return $"{prefix}: processed={result.ProcessedCount} updated={result.UpdatedCount} skipped={result.SkippedCount} failed={result.FailedCount}.";
    }

    private static async ValueTask WriteBestEffortAsync(TextWriter writer, string message)
    {
        try
        {
            await writer.WriteLineAsync(message).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch
        {
        }
    }
}
