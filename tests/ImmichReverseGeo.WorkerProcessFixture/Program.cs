using System.Text;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;

namespace ImmichReverseGeo.WorkerProcessFixture;

internal static class Program
{
    private const int MaximumDiagnosticCharacters = 220;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 3
            && string.Equals(args[0], "--hold-cache-candidate", StringComparison.Ordinal))
        {
            using ICacheCandidateLease lease =
                new CacheCandidateOwnership().Acquire(args[1]);
            await File.WriteAllTextAsync(args[1], "held candidate").ConfigureAwait(false);
            await File.WriteAllTextAsync(args[2], "ready").ConfigureAwait(false);
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            return 0;
        }

        var standardError = Console.OpenStandardError();
        if (!FixtureOptions.TryParse(args, out var options, out var error))
        {
            await TryWriteDiagnosticAsync(standardError, "fixture-usage", error).ConfigureAwait(false);
            return WorkerProcessExitCodes.InvalidInput;
        }

        var protocolSelection = InternalWorkerProtocolVersionSelector.Select(
            Environment.GetEnvironmentVariable);
        if (protocolSelection is not InternalWorkerProtocolVersionSelection.Success selected)
        {
            await TryWriteDiagnosticAsync(
                standardError,
                "fixture-input",
                InternalWorkerProtocolVersionSelector.InvalidSelectionDiagnostic).ConfigureAwait(false);
            return WorkerProcessExitCodes.InvalidInput;
        }

        try
        {
            if (options!.UsesProductionCacheHost)
            {
                return await ProductionCacheHostFixture.RunAsync(
                    options,
                    selected.Version).ConfigureAwait(false);
            }

            if (options!.UsesProductionCoordinateHost)
            {
                return await ProductionCoordinateHostFixture.RunAsync(
                    options,
                    selected.Version).ConfigureAwait(false);
            }

            var runner = new FixtureRunner(
                options,
                selected.Version,
                Console.OpenStandardInput(),
                Console.OpenStandardOutput(),
                standardError);
            return await runner.RunAsync().ConfigureAwait(false);
        }
        catch (FixtureInputException exception)
        {
            await TryWriteDiagnosticAsync(standardError, "fixture-input", exception.Message).ConfigureAwait(false);
            return WorkerProcessExitCodes.InvalidInput;
        }
        catch (Exception exception)
        {
            await TryWriteDiagnosticAsync(
                standardError,
                "fixture-failure",
                $"{exception.GetType().Name}: {exception.Message}").ConfigureAwait(false);
            return WorkerProcessExitCodes.InfrastructureFailure;
        }
    }

    private static async Task TryWriteDiagnosticAsync(Stream standardError, string category, string detail)
    {
        try
        {
            var boundedDetail = detail.Length <= MaximumDiagnosticCharacters
                ? detail
                : detail[..MaximumDiagnosticCharacters];
            var bytes = Encoding.UTF8.GetBytes($"{category}: {boundedDetail}\n");
            await standardError.WriteAsync(bytes).ConfigureAwait(false);
            await standardError.FlushAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }
}
