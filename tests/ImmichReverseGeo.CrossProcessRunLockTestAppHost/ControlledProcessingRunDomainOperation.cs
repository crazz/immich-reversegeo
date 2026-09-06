using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Tests.CrossProcessRunExclusion;
using ImmichReverseGeo.Web.ProcessingRunLocking;
using ImmichReverseGeo.Web.Services;
using Npgsql;

namespace ImmichReverseGeo.CrossProcessRunLockTestAppHost;

internal sealed class ControlledProcessingRunDomainOperation(
    CrossProcessRunLockAppHostOptions options,
    NpgsqlDataSource dataSource) : IProcessingRunDomainOperation
{
    internal const string EnteredLogMessage = "Change32 protected operation entered";

    public async Task ExecuteAsync(
        IProcessingRunEventSession session,
        Func<Task> executeProductionDomainAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(executeProductionDomainAsync);

        await using var releasePipe = new NamedPipeClientStream(
            ".",
            options.ReleasePipeName,
            PipeDirection.In,
            PipeOptions.Asynchronous);
        await releasePipe.ConnectAsync(cancellationToken).ConfigureAwait(false);

        var owner = await FindLockOwnerAsync(cancellationToken).ConfigureAwait(false);
        if (owner is null || !string.Equals(owner.ApplicationName, options.ApplicationName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The test-owned PostgreSQL run-lock owner could not be verified.");
        }

        await executeProductionDomainAsync().ConfigureAwait(false);
        await PublishMarkerAsync(owner, cancellationToken).ConfigureAwait(false);
        await session.ReportLogAsync(ProcessingLogLevel.Information, EnteredLogMessage, cancellationToken).ConfigureAwait(false);

        var release = new byte[1];
        var received = await releasePipe.ReadAsync(release, cancellationToken).ConfigureAwait(false);
        if (received != 1)
        {
            throw new IOException("The test release gate closed without a release byte.");
        }

        switch (options.Scenario)
        {
            case CrossProcessRunLockScenario.HeldSuccess:
                return;
            case CrossProcessRunLockScenario.DomainFailure:
                throw new InvalidOperationException("Controlled Change32 domain failure.");
            case CrossProcessRunLockScenario.CooperativeCancel:
            case CrossProcessRunLockScenario.OwnershipLoss:
                throw new InvalidOperationException("The controlled operation received an unexpected release.");
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private async Task<PostgresLockOwner?> FindLockOwnerAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            return await PostgresLockOwnerQuery.FindAsync(
                connection,
                ProcessingRunLockIdentity.Key,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new InvalidOperationException("The test-owned PostgreSQL run-lock owner could not be queried.");
        }
    }

    private async Task PublishMarkerAsync(PostgresLockOwner owner, CancellationToken cancellationToken)
    {
        var temporaryPath = Path.Combine(options.ResourceRoot, $".{Path.GetFileName(options.MarkerPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            try
            {
                await using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    useAsync: true))
                {
                    await JsonSerializer.SerializeAsync(
                        stream,
                        new PostLockMarker(owner.ProcessId, owner.ApplicationName),
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                File.Move(temporaryPath, options.MarkerPath, overwrite: false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OutOfMemoryException)
            {
                throw;
            }
            catch (Exception)
            {
                throw new InvalidOperationException("The test-owned lock marker could not be published.");
            }
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
            }
        }
    }

    private sealed record PostLockMarker(
        [property: JsonPropertyName("backendProcessId")] int BackendProcessId,
        [property: JsonPropertyName("applicationName")] string ApplicationName);
}
