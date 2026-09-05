using System.Text;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;

namespace ImmichReverseGeo.WorkerProcessFixture;

internal static class Program
{
    private const int MaximumDiagnosticCharacters = 220;

    private static async Task<int> Main(string[] args)
    {
        var standardError = Console.OpenStandardError();
        if (!FixtureOptions.TryParse(args, out var options, out var error))
        {
            await TryWriteDiagnosticAsync(standardError, "fixture-usage", error).ConfigureAwait(false);
            return WorkerProcessExitCodes.InvalidInput;
        }

        try
        {
            var runner = new FixtureRunner(
                options!,
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
            await TryWriteDiagnosticAsync(standardError, "fixture-failure", exception.GetType().Name).ConfigureAwait(false);
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
