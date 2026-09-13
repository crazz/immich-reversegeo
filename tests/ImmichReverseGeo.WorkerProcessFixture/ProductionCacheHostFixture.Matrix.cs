using System.Text.Json;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Gadm.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.WorkerProcessFixture;

internal enum CacheMatrixStage
{
    Success, Preparation, Transfer, Export, Validation, Metadata, Readability,
    HandleClose, PreReplace, BeforePublicationCancel, AfterPublicationCancel
}

internal static class CacheMatrixStages
{
    internal static bool TryParse(string value, out CacheMatrixStage stage)
    {
        CacheMatrixStage? selected = value switch
        {
            "success" => CacheMatrixStage.Success,
            "preparation" => CacheMatrixStage.Preparation,
            "transfer" => CacheMatrixStage.Transfer,
            "export" => CacheMatrixStage.Export,
            "validation" => CacheMatrixStage.Validation,
            "metadata" => CacheMatrixStage.Metadata,
            "readability" => CacheMatrixStage.Readability,
            "handle-close" => CacheMatrixStage.HandleClose,
            "pre-replace" => CacheMatrixStage.PreReplace,
            "before-publication-cancel" => CacheMatrixStage.BeforePublicationCancel,
            "after-publication-cancel" => CacheMatrixStage.AfterPublicationCancel,
            _ => null
        };
        stage = selected.GetValueOrDefault();
        return selected.HasValue;
    }
}

internal static partial class ProductionCacheHostFixture
{
    private static void ConfigureMatrix(IServiceCollection services, FixtureOptions options)
    {
        var probe = new CacheMatrixProbe(options.ResourceRoot, options.CacheStage!.Value);
        services.RemoveAll<GadmDivisionCacheService>();
        services.AddSingleton(sp => new GadmDivisionCacheService(
            sp.GetRequiredService<ILogger<GadmDivisionCacheService>>(), options.ResourceRoot,
            new GadmDivisionCacheTestHooks
            {
                CandidateOwnership = probe,
                FilePublisher = probe,
                DownloadOperation = async (_, destination, ct) =>
                {
                    if (probe.Stage == CacheMatrixStage.Transfer)
                    {
                        await File.WriteAllTextAsync(destination, "partial-source", ct).ConfigureAwait(false);
                        probe.Fail("transfer");
                    }
                    await CreateSourceAsync(options.ResourceRoot, destination, ct).ConfigureAwait(false);
                    probe.Record("downloaded", destination);
                },
                ExportOperation = (source, candidate, iso3, ct) =>
                {
                    if (probe.Stage == CacheMatrixStage.Export)
                    {
                        File.WriteAllText(candidate, "partial-export");
                        probe.Fail("export");
                    }
                    long rows = GadmCacheExporter.ExportGeoPackageToSqlite(source, candidate, iso3, ct);
                    probe.Record("exported", candidate);
                    if (probe.Stage == CacheMatrixStage.Validation)
                    {
                        File.WriteAllText(candidate, "invalid-sqlite-candidate");
                        probe.Record("validation", candidate);
                    }
                    else if (probe.Stage == CacheMatrixStage.Metadata)
                    {
                        using var connection = new SqliteConnection($"Data Source={candidate};Pooling=false");
                        connection.Open();
                        using var command = connection.CreateCommand();
                        command.CommandText = "UPDATE _meta SET value = 'USA' WHERE key = 'iso3'";
                        if (command.ExecuteNonQuery() != 1)
                        {
                            throw new FixtureInputException("The real exported cache has no unique identity metadata.");
                        }
                        probe.Record("metadata", candidate);
                    }
                    return rows;
                },
                ValidationOperation = probe.Stage == CacheMatrixStage.Readability
                    ? candidate =>
                    {
                        probe.Fail("readability", candidate);
                        return false;
                    } : null,
                BeforePublication = async ct =>
                {
                    probe.Record("validated");
                    if (probe.Stage == CacheMatrixStage.HandleClose)
                    {
                        // The existing validation/publication seam owns this real read
                        // handle. Its wrapper reports a close failure only after closing it.
                        using var held = new CacheCloseFailure(File.OpenRead(probe.Candidate!), probe);
                    }
                    if (probe.Stage == CacheMatrixStage.BeforePublicationCancel)
                    {
                        await probe.WaitForCancellationAsync("before-publication-cancel", ct).ConfigureAwait(false);
                    }
                },
                AfterPublication = async ct =>
                {
                    probe.Record("published");
                    if (probe.Stage == CacheMatrixStage.AfterPublicationCancel)
                    {
                        await probe.WaitForCancellationAsync("after-publication-cancel", ct).ConfigureAwait(false);
                    }
                }
            }));
    }

    private sealed class CacheMatrixProbe(string root, CacheMatrixStage stage) : ICacheCandidateOwnership, ICacheFilePublisher
    {
        private readonly CacheCandidateOwnership _ownership = new();
        internal CacheMatrixStage Stage => stage;
        internal string? Candidate { get; private set; }

        internal void Record(string phase, string? path = null) =>
            File.AppendAllText(Path.Combine(root, $"matrix-cache-{Environment.ProcessId}.ndjson"),
                JsonSerializer.Serialize(new { phase, path, processId = Environment.ProcessId }) + "\n");

        internal void Fail(string phase, string? path = null)
        {
            Record(phase, path);
            throw new IOException("matrix-cache-secret-password-payload");
        }

        internal async Task WaitForCancellationAsync(string phase, CancellationToken ct)
        {
            Record(phase);
            await File.WriteAllTextAsync(Path.Combine(root, $"matrix-cache-{Environment.ProcessId}-cancel.marker"), phase,
                CancellationToken.None).ConfigureAwait(false);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        }

        public ICacheCandidateLease Acquire(string path)
        {
            Record("candidate", path);
            if (path.EndsWith(".tmp", StringComparison.Ordinal))
            {
                Candidate = path;
            }
            if (stage == CacheMatrixStage.Preparation)
            {
                Fail("preparation", path);
            }
            return new RecordedLease(_ownership.Acquire(path), this);
        }

        public bool TryCleanupAbandoned(string path) => _ownership.TryCleanupAbandoned(path);

        public void Publish(string candidatePath, string finalPath)
        {
            // Prove the exporter/validator already closed their candidate handles.
            using (File.Open(candidatePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            Record("handles-closed", candidatePath);
            if (stage == CacheMatrixStage.PreReplace)
            {
                Fail("pre-replace", candidatePath);
            }
            new AtomicCacheFilePublisher().Publish(candidatePath, finalPath);
        }

        private sealed class RecordedLease(ICacheCandidateLease inner, CacheMatrixProbe probe) : ICacheCandidateLease
        {
            public string CandidatePath => inner.CandidatePath;
            public void Dispose()
            {
                inner.Dispose();
                probe.Record("released", CandidatePath);
            }
        }
    }

    private sealed class CacheCloseFailure(FileStream inner, CacheMatrixProbe probe) : IDisposable
    {
        public void Dispose()
        {
            inner.Dispose();
            probe.Fail("handle-close", inner.Name);
        }
    }
}
