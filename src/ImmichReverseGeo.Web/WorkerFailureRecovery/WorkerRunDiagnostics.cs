using System;
using System.Text;
using System.Text.RegularExpressions;

namespace ImmichReverseGeo.Web.WorkerFailureRecovery;

internal static partial class WorkerRunDiagnostics
{
    internal const int ExcerptLimit = 1024;
    private const int InspectionLimit = 65_536;
    private const string Redacted = "[redacted]";
    private const string Truncated = "[truncated]";

    internal static string Describe(WorkerRunFailureCategory category)
    {
        return category switch
        {
            WorkerRunFailureCategory.Terminal => "The worker reported its final result.",
            WorkerRunFailureCategory.CommandResolution => "Worker command unavailable. Check the application installation before starting another run.",
            WorkerRunFailureCategory.ProcessStart => "Worker could not start. Check container resources and executable permissions before starting another run.",
            WorkerRunFailureCategory.ReadyTimeout => "Worker startup timed out. Check available resources and the application installation before starting another run.",
            WorkerRunFailureCategory.PreReadyEndOfStream or WorkerRunFailureCategory.StartupCrash or WorkerRunFailureCategory.ExitObservation
                or WorkerRunFailureCategory.ReadyRejected => "Worker startup did not complete. Check the application installation and available resources before starting another run.",
            WorkerRunFailureCategory.ExecuteSerialization or WorkerRunFailureCategory.ExecuteWrite or WorkerRunFailureCategory.ExecuteFlush
                => "The worker could not receive the run request. Check the application installation before starting another run.",
            WorkerRunFailureCategory.MalformedFrame or WorkerRunFailureCategory.InvalidEncoding or WorkerRunFailureCategory.OversizedFrame
                or WorkerRunFailureCategory.UnknownOrIncompatible or WorkerRunFailureCategory.Readiness or WorkerRunFailureCategory.Sequence
                or WorkerRunFailureCategory.Correlation or WorkerRunFailureCategory.Lifecycle or WorkerRunFailureCategory.ProgressConsistency
                or WorkerRunFailureCategory.TerminalConsistency or WorkerRunFailureCategory.ActivityCardinality
                => "Worker communication was invalid. Check that all application files come from the same release before starting another run.",
            WorkerRunFailureCategory.ProjectionFailure => "The worker result could not be applied. Review the current run status before starting another run.",
            WorkerRunFailureCategory.OutputTransport => "Worker output could not be read completely. Check available resources before starting another run.",
            WorkerRunFailureCategory.ManagedCancellation => "The worker stopped after cancellation.",
            WorkerRunFailureCategory.ForcedTermination => "The worker was forcibly stopped after its cancellation deadline. Changes already saved remain saved.",
            WorkerRunFailureCategory.KillRejected => "The worker could not be forcibly stopped. Check process permissions and container health before starting another run.",
            WorkerRunFailureCategory.BusyWithoutTerminal => "The worker ended without a confirmed result. Review active processing before starting another run.",
            WorkerRunFailureCategory.InvalidInput => "The worker rejected its input. Check that all application files come from the same release before starting another run.",
            WorkerRunFailureCategory.ExecutionFailure => "The worker could not finish processing. Review application health before starting another run.",
            WorkerRunFailureCategory.Infrastructure => "Worker infrastructure failed. Check available resources and the application installation before starting another run.",
            _ => "The worker ended without a confirmed result. Changes already saved remain saved. Review application health before starting another run."
        };
    }

internal static string Describe(WorkerRunFailureCategory category, WorkerRunTransportPhase phase)
    {
        return $"{Describe(category)} [{category.ToString().ToLowerInvariant()}/{phase.ToString().ToLowerInvariant()}]";
    }

    internal static string DescribeAnomalies(WorkerRunAnomaly anomalies)
    {
        // A closed summary intentionally excludes stderr, exception messages, exit text, and run payloads.
        return anomalies.HasFlag(WorkerRunAnomaly.ForcedTermination)
            ? "The worker required forced termination. Its recorded result is unchanged; changes already saved remain saved."
            : "The worker reported a result, then encountered a communication or cleanup inconsistency. Its recorded result is unchanged.";
    }

    /// <summary>Optional diagnostic export only. Primary UI messages never call this renderer.</summary>
    internal static string RenderExcerpt(string text, bool tailWasTruncated = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        var wasTruncated = tailWasTruncated || text.Length > InspectionLimit;
        // Work on bounded input before regex scanning, including untrusted in-memory callers.
        var input = text.AsSpan(0, Math.Min(text.Length, InspectionLimit));
        var normalized = new StringBuilder(input.Length);
        foreach (var character in input)
        {
            if (character is '\n' or '\r')
            {
                normalized.Append('\n');
            }
            else if (!char.IsControl(character) && char.GetUnicodeCategory(character) != System.Globalization.UnicodeCategory.Format)
            {
                normalized.Append(character);
            }
        }

        if (SensitiveLine().IsMatch(normalized.ToString()))
        {
            return wasTruncated ? Redacted + "\n" + Truncated : Redacted;
        }

        var lines = normalized.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var output = new StringBuilder();
        foreach (var line in lines)
        {
            // Redact the entire line, including continuations that resemble payloads, rather than
            // attempting to preserve a value's delimiters or reconstruct a command/request frame.
            var safe = SensitiveLine().IsMatch(line) ? Redacted : line;
            if (output.Length > 0)
            {
                output.Append('\n');
            }
            output.Append(safe);
            if (output.Length > ExcerptLimit)
            {
                wasTruncated = true;
                break;
            }
        }

        if (wasTruncated)
        {
            var contentLimit = ExcerptLimit - Truncated.Length - 1;
            if (output.Length > contentLimit)
            {
                output.Length = contentLimit;
            }
            if (output.Length > 0 && char.IsHighSurrogate(output[^1]))
            {
                output.Length--;
            }
            output.Append('\n').Append(Truncated);
        }
        return output.ToString();
    }

    [GeneratedRegex(@"(?ix) [=:{\[\]}] | :// | \b(?:password|passwd|pwd|credential\w*|secret\w*|token\w*|api[\s_-]*key|authorization|bearer|connection[\s_-]*string|user[\s_-]*id|username|private[\s_-]*key|execute|run[\s_-]*id|protocol|payload|request|select|insert|update|delete)\b | --\w | [A-Za-z0-9_+/\-]{25,}", RegexOptions.CultureInvariant, 100)]
    private static partial Regex SensitiveLine();
}
