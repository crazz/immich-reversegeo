using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;

namespace ImmichReverseGeo.Web.Services;

internal sealed class CacheInventoryOptions
{
    internal const int DefaultMaxVisitedEntriesPerSource = 1024;
    internal const int DefaultMaxLogicalCandidatesPerSource = 256;
    internal const int DefaultMaxTemporaryArtifactsPerIso = 8;
    internal const int DefaultMaxMetadataValueCharacters = 256;
    internal const int DefaultMaxSchemaObjects = 64;
    internal const int DefaultMaxSqliteValueBytes = 65_536;

    internal int MaxVisitedEntriesPerSource { get; init; } =
        DefaultMaxVisitedEntriesPerSource;
    internal int MaxLogicalCandidatesPerSource { get; init; } =
        DefaultMaxLogicalCandidatesPerSource;
    internal int MaxTemporaryArtifactsPerIso { get; init; } =
        DefaultMaxTemporaryArtifactsPerIso;
    internal int MaxMetadataValueCharacters { get; init; } =
        DefaultMaxMetadataValueCharacters;
    internal int MaxSchemaObjects { get; init; } = DefaultMaxSchemaObjects;
    internal int MaxSqliteValueBytes { get; init; } = DefaultMaxSqliteValueBytes;
    internal TimeSpan SqliteBusyTimeout { get; init; } = TimeSpan.FromSeconds(2);

    internal void Validate()
    {
        if (MaxVisitedEntriesPerSource <= 0
            || MaxLogicalCandidatesPerSource <= 0
            || MaxLogicalCandidatesPerSource > MaxVisitedEntriesPerSource
            || MaxTemporaryArtifactsPerIso <= 0
            || MaxMetadataValueCharacters <= 0
            || MaxSchemaObjects <= 0
            || MaxSqliteValueBytes <= MaxMetadataValueCharacters
            || MaxSqliteValueBytes > 1_048_576
            || SqliteBusyTimeout <= TimeSpan.Zero
            || SqliteBusyTimeout > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(CacheInventoryOptions));
        }
    }
}

internal enum CacheInventoryPathKind
{
    Missing,
    RegularFile,
    Directory,
    Unsafe
}

internal readonly record struct CacheInventoryPathObservation(
    CacheInventoryPathKind Kind,
    long SizeBytes,
    DateTimeOffset LastModifiedUtc);

internal interface ICacheInventoryFileSystem
{
    CacheInventoryPathObservation Observe(string path);

    IEnumerable<string> EnumerateImmediateEntries(string sourceDirectory);
}

internal sealed class PhysicalCacheInventoryFileSystem : ICacheInventoryFileSystem
{
    public CacheInventoryPathObservation Observe(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return new CacheInventoryPathObservation(
                    CacheInventoryPathKind.Unsafe,
                    0,
                    DateTimeOffset.UnixEpoch);
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                return new CacheInventoryPathObservation(
                    CacheInventoryPathKind.Directory,
                    0,
                    new DateTimeOffset(Directory.GetLastWriteTimeUtc(path), TimeSpan.Zero));
            }

            var info = new FileInfo(path);
            return new CacheInventoryPathObservation(
                CacheInventoryPathKind.RegularFile,
                info.Length,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
        }
        catch (FileNotFoundException)
        {
            return Missing();
        }
        catch (DirectoryNotFoundException)
        {
            return Missing();
        }
    }

    public IEnumerable<string> EnumerateImmediateEntries(string sourceDirectory) =>
        Directory.EnumerateFileSystemEntries(
            sourceDirectory,
            "*",
            new EnumerationOptions
            {
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
                IgnoreInaccessible = false,
                AttributesToSkip = 0
            });

    private static CacheInventoryPathObservation Missing() =>
        new(CacheInventoryPathKind.Missing, 0, DateTimeOffset.UnixEpoch);
}

internal enum CacheInventoryMetadataStatus
{
    Available,
    Invalid,
    Unreadable
}

internal sealed record CacheInventoryMetadataResult(
    CacheInventoryMetadataStatus Status,
    DateTimeOffset? DownloadedUtc,
    string? DatasetVersion,
    CacheInventoryDiagnosticCode? DiagnosticCode);

internal interface ICacheInventoryMetadataReader
{
    Task<CacheInventoryMetadataResult> ReadAsync(
        string databasePath,
        CacheMutationSource source,
        CacheInventoryOptions options,
        CancellationToken cancellationToken);
}

internal sealed class CacheInventoryStorageScanner : ICacheInventoryStorageScanner
{
    private static readonly SourceDefinition[] Sources =
    [
        new(CacheMutationSource.Overture, "overture-divisions"),
        new(CacheMutationSource.Gadm, "gadm-divisions")
    ];

    private readonly string _dataRoot;
    private readonly ICacheInventoryFileSystem _fileSystem;
    private readonly ICacheInventoryMetadataReader _metadataReader;
    private readonly CacheInventoryOptions _options;
    private readonly TimeProvider _timeProvider;

    public CacheInventoryStorageScanner(
        StorageOptions storage,
        ICacheInventoryFileSystem fileSystem,
        ICacheInventoryMetadataReader metadataReader,
        CacheInventoryOptions options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _metadataReader = metadataReader ?? throw new ArgumentNullException(nameof(metadataReader));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _options.Validate();
        _dataRoot = Path.GetFullPath(storage.DataDir);
    }

    public async Task<CacheInventorySnapshot> ScanAsync(
        long generation,
        CancellationToken cancellationToken)
    {
        var results = ImmutableArray.CreateBuilder<CacheInventorySourceSnapshot>(Sources.Length);
        foreach (SourceDefinition source in Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await ScanSourceAsync(source, cancellationToken).ConfigureAwait(false));
        }

        return new CacheInventorySnapshot(
            generation,
            _timeProvider.GetUtcNow(),
            results.MoveToImmutable());
    }

    public async Task<CacheInventoryExactResult> ScanExactAsync(
        CacheMutationSource source,
        string iso3,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(source))
        {
            throw new ArgumentOutOfRangeException(nameof(source));
        }

        CacheMutationRequest.RequireCanonicalIso3(iso3, nameof(iso3));
        SourceDefinition definition = Sources.Single(candidate => candidate.Source == source);
        DiscoveryResult discovery = Discover(definition, cancellationToken);
        bool hasTemporary = discovery.Temporaries.TryGetValue(iso3, out HashSet<string>? temporaries)
            && temporaries.Count > 0;
        bool hasUnsafeTemporary = discovery.UnsafeTemporaryIsos.Contains(iso3);
        CacheInventoryEntry entry;
        if (discovery.Status is CacheInventorySourceStatus.Unsafe)
        {
            entry = UnavailableEntry(
                source,
                iso3,
                CacheInventoryEntryStatus.Unsafe,
                CacheInventoryDiagnosticCode.SourceUnsafe,
                hasTemporary);
        }
        else if (discovery.Status is CacheInventorySourceStatus.Unreadable)
        {
            entry = UnavailableEntry(
                source,
                iso3,
                CacheInventoryEntryStatus.Unreadable,
                CacheInventoryDiagnosticCode.SourceUnreadable,
                hasTemporary);
        }
        else if (hasUnsafeTemporary)
        {
            entry = UnavailableEntry(
                source,
                iso3,
                CacheInventoryEntryStatus.Unsafe,
                CacheInventoryDiagnosticCode.CandidateUnsafe,
                true);
        }
        else
        {
            entry = await InspectFinalAsync(
                definition,
                iso3,
                hasTemporary,
                cancellationToken).ConfigureAwait(false);
            if (entry.Status == CacheInventoryEntryStatus.Absent && !discovery.IsComplete)
            {
                entry = UnavailableEntry(
                    source,
                    iso3,
                    CacheInventoryEntryStatus.Unreadable,
                    discovery.DiagnosticCode ?? CacheInventoryDiagnosticCode.EnumerationTruncated,
                    hasTemporary);
            }
        }

        return new CacheInventoryExactResult(entry, discovery.IsComplete);
    }

    private async Task<CacheInventorySourceSnapshot> ScanSourceAsync(
        SourceDefinition source,
        CancellationToken cancellationToken)
    {
        DiscoveryResult discovery = Discover(source, cancellationToken);
        if (!discovery.IsComplete)
        {
            return new CacheInventorySourceSnapshot(
                source.Source,
                discovery.Status,
                discovery.DiagnosticCode,
                ImmutableArray<CacheInventoryEntry>.Empty);
        }

        var entries = ImmutableArray.CreateBuilder<CacheInventoryEntry>(discovery.Isos.Count);
        foreach (string iso3 in discovery.Isos.Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool hasTemporary = discovery.Temporaries.TryGetValue(
                iso3,
                out HashSet<string>? temporaries)
                && temporaries.Count > 0;
            if (discovery.UnsafeTemporaryIsos.Contains(iso3))
            {
                entries.Add(UnavailableEntry(
                    source.Source,
                    iso3,
                    CacheInventoryEntryStatus.Unsafe,
                    CacheInventoryDiagnosticCode.CandidateUnsafe,
                    hasTemporary));
                continue;
            }

            CacheInventoryEntry entry = await InspectFinalAsync(
                source,
                iso3,
                hasTemporary,
                cancellationToken).ConfigureAwait(false);
            if (entry.Status != CacheInventoryEntryStatus.Absent)
            {
                entries.Add(entry);
            }
        }

        return new CacheInventorySourceSnapshot(
            source.Source,
            CacheInventorySourceStatus.Ready,
            null,
            entries.ToImmutable());
    }

    private DiscoveryResult Discover(
        SourceDefinition source,
        CancellationToken cancellationToken)
    {
        string sourceDirectory = GetSourceDirectory(source);
        CacheInventoryPathObservation sourceObservation;
        try
        {
            sourceObservation = _fileSystem.Observe(sourceDirectory);
        }
        catch (UnauthorizedAccessException)
        {
            return DiscoveryResult.Failed(
                CacheInventorySourceStatus.Unreadable,
                CacheInventoryDiagnosticCode.SourceUnreadable);
        }
        catch (IOException)
        {
            return DiscoveryResult.Failed(
                CacheInventorySourceStatus.Unreadable,
                CacheInventoryDiagnosticCode.SourceUnreadable);
        }

        if (sourceObservation.Kind == CacheInventoryPathKind.Missing)
        {
            return DiscoveryResult.Complete();
        }

        if (sourceObservation.Kind != CacheInventoryPathKind.Directory)
        {
            return DiscoveryResult.Failed(
                CacheInventorySourceStatus.Unsafe,
                CacheInventoryDiagnosticCode.SourceUnsafe);
        }

        var isos = new HashSet<string>(StringComparer.Ordinal);
        var finals = new HashSet<string>(StringComparer.Ordinal);
        var temporaries = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var unsafeTemporaryIsos = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using IEnumerator<string> enumerator = _fileSystem
                .EnumerateImmediateEntries(sourceDirectory)
                .GetEnumerator();
            var visited = 0;
            while (enumerator.MoveNext())
            {
                cancellationToken.ThrowIfCancellationRequested();
                visited++;
                if (visited > _options.MaxVisitedEntriesPerSource)
                {
                    return DiscoveryResult.Truncated(
                        CacheInventoryDiagnosticCode.EnumerationTruncated);
                }

                string basename = Path.GetFileName(enumerator.Current);
                if (TryParseFinal(basename, out string? parsedFinalIso)
                    && parsedFinalIso is string finalIso)
                {
                    if (!AddLogicalIso(finalIso, isos))
                    {
                        return DiscoveryResult.Truncated(
                            CacheInventoryDiagnosticCode.LogicalCandidateLimitExceeded);
                    }

                    finals.Add(finalIso);
                    continue;
                }

                if (!TryParseTemporary(
                        source.Source,
                        basename,
                        out string? parsedTempIso,
                        out string? parsedIdentity)
                    || parsedTempIso is not string tempIso
                    || parsedIdentity is not string identity)
                {
                    continue;
                }

                if (!AddLogicalIso(tempIso, isos))
                {
                    return DiscoveryResult.Truncated(
                        CacheInventoryDiagnosticCode.LogicalCandidateLimitExceeded);
                }

                if (!temporaries.TryGetValue(tempIso, out HashSet<string>? identities))
                {
                    identities = new HashSet<string>(StringComparer.Ordinal);
                    temporaries.Add(tempIso, identities);
                }

                identities.Add(identity);
                if (identities.Count > _options.MaxTemporaryArtifactsPerIso)
                {
                    return DiscoveryResult.Truncated(
                        CacheInventoryDiagnosticCode.TemporaryArtifactLimitExceeded);
                }

                CacheInventoryPathObservation tempObservation = _fileSystem.Observe(
                    Path.Combine(sourceDirectory, basename));
                if (tempObservation.Kind == CacheInventoryPathKind.Unsafe)
                {
                    unsafeTemporaryIsos.Add(tempIso);
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            return DiscoveryResult.Failed(
                CacheInventorySourceStatus.Unreadable,
                CacheInventoryDiagnosticCode.SourceUnreadable);
        }
        catch (IOException)
        {
            return DiscoveryResult.Failed(
                CacheInventorySourceStatus.Unreadable,
                CacheInventoryDiagnosticCode.SourceUnreadable);
        }

        return DiscoveryResult.Complete(isos, finals, temporaries, unsafeTemporaryIsos);

        bool AddLogicalIso(string iso3, HashSet<string> target)
        {
            target.Add(iso3);
            return target.Count <= _options.MaxLogicalCandidatesPerSource;
        }
    }

    private async Task<CacheInventoryEntry> InspectFinalAsync(
        SourceDefinition source,
        string iso3,
        bool hasTemporary,
        CancellationToken cancellationToken)
    {
        string sourceDirectory = GetSourceDirectory(source);
        string path = Path.Combine(sourceDirectory, iso3 + ".db");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            CacheInventoryPathObservation sourceBefore;
            try
            {
                sourceBefore = _fileSystem.Observe(sourceDirectory);
            }
            catch (UnauthorizedAccessException)
            {
                return UnavailableEntry(
                    source.Source,
                    iso3,
                    CacheInventoryEntryStatus.Unreadable,
                    CacheInventoryDiagnosticCode.SourceUnreadable,
                    hasTemporary);
            }
            catch (IOException)
            {
                return UnavailableEntry(
                    source.Source,
                    iso3,
                    CacheInventoryEntryStatus.Unreadable,
                    CacheInventoryDiagnosticCode.SourceUnreadable,
                    hasTemporary);
            }

            if (sourceBefore.Kind is CacheInventoryPathKind.RegularFile
                or CacheInventoryPathKind.Unsafe)
            {
                return UnavailableEntry(
                    source.Source,
                    iso3,
                    CacheInventoryEntryStatus.Unsafe,
                    CacheInventoryDiagnosticCode.SourceUnsafe,
                    hasTemporary);
            }

            CacheInventoryPathObservation before;
            try
            {
                before = _fileSystem.Observe(path);
            }
            catch (UnauthorizedAccessException)
            {
                return UnavailableEntry(
                    source.Source,
                    iso3,
                    CacheInventoryEntryStatus.Unreadable,
                    CacheInventoryDiagnosticCode.CandidateUnreadable,
                    hasTemporary);
            }
            catch (IOException)
            {
                return UnavailableEntry(
                    source.Source,
                    iso3,
                    CacheInventoryEntryStatus.Unreadable,
                    CacheInventoryDiagnosticCode.CandidateUnreadable,
                    hasTemporary);
            }

            if (before.Kind == CacheInventoryPathKind.Missing)
            {
                return UnavailableEntry(
                    source.Source,
                    iso3,
                    hasTemporary
                        ? CacheInventoryEntryStatus.InProgress
                        : CacheInventoryEntryStatus.Absent,
                    null,
                    hasTemporary);
            }

            if (before.Kind != CacheInventoryPathKind.RegularFile)
            {
                return UnavailableEntry(
                    source.Source,
                    iso3,
                    CacheInventoryEntryStatus.Unsafe,
                    CacheInventoryDiagnosticCode.CandidateUnsafe,
                    hasTemporary);
            }

            if (before.SizeBytes == 0)
            {
                return new CacheInventoryEntry(
                    source.Source,
                    iso3,
                    CacheInventoryEntryStatus.Invalid,
                    0,
                    before.LastModifiedUtc,
                    null,
                    null,
                    hasTemporary,
                    CacheInventoryDiagnosticCode.InvalidDatabase);
            }

            CacheInventoryMetadataResult metadata;
            try
            {
                metadata = await _metadataReader
                    .ReadAsync(path, source.Source, _options, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                metadata = new CacheInventoryMetadataResult(
                    CacheInventoryMetadataStatus.Unreadable,
                    null,
                    null,
                    CacheInventoryDiagnosticCode.CandidateUnreadable);
            }
            catch (IOException)
            {
                metadata = new CacheInventoryMetadataResult(
                    CacheInventoryMetadataStatus.Unreadable,
                    null,
                    null,
                    CacheInventoryDiagnosticCode.CandidateUnreadable);
            }

            CacheInventoryPathObservation after;
            try
            {
                after = _fileSystem.Observe(path);
            }
            catch (UnauthorizedAccessException)
            {
                after = default;
            }
            catch (IOException)
            {
                after = default;
            }

            CacheInventoryPathObservation sourceAfter;
            try
            {
                sourceAfter = _fileSystem.Observe(sourceDirectory);
            }
            catch (UnauthorizedAccessException)
            {
                return UnavailableEntry(
                    source.Source,
                    iso3,
                    CacheInventoryEntryStatus.Unreadable,
                    CacheInventoryDiagnosticCode.SourceUnreadable,
                    hasTemporary);
            }
            catch (IOException)
            {
                return UnavailableEntry(
                    source.Source,
                    iso3,
                    CacheInventoryEntryStatus.Unreadable,
                    CacheInventoryDiagnosticCode.SourceUnreadable,
                    hasTemporary);
            }

            if (sourceAfter.Kind is CacheInventoryPathKind.RegularFile
                or CacheInventoryPathKind.Unsafe)
            {
                return UnavailableEntry(
                    source.Source,
                    iso3,
                    CacheInventoryEntryStatus.Unsafe,
                    CacheInventoryDiagnosticCode.SourceUnsafe,
                    hasTemporary);
            }

            if (sourceAfter.Kind == CacheInventoryPathKind.Missing)
            {
                after = default;
            }

            if (before != after)
            {
                if (attempt == 0)
                {
                    continue;
                }

                return UnavailableEntry(
                    source.Source,
                    iso3,
                    CacheInventoryEntryStatus.Unreadable,
                    CacheInventoryDiagnosticCode.CandidateChanged,
                    hasTemporary);
            }

            return new CacheInventoryEntry(
                source.Source,
                iso3,
                metadata.Status switch
                {
                    CacheInventoryMetadataStatus.Available => CacheInventoryEntryStatus.Available,
                    CacheInventoryMetadataStatus.Invalid => CacheInventoryEntryStatus.Invalid,
                    _ => CacheInventoryEntryStatus.Unreadable
                },
                before.SizeBytes,
                before.LastModifiedUtc,
                metadata.DownloadedUtc,
                metadata.DatasetVersion,
                hasTemporary,
                metadata.DiagnosticCode);
        }

        throw new InvalidOperationException("The bounded inspection loop did not return.");
    }

    private string GetSourceDirectory(SourceDefinition source)
    {
        string result = Path.GetFullPath(Path.Combine(_dataRoot, source.DirectoryName));
        string rootWithSeparator = Path.TrimEndingDirectorySeparator(_dataRoot)
            + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!result.StartsWith(rootWithSeparator, comparison))
        {
            throw new InvalidOperationException("The fixed cache source is outside the data root.");
        }

        return result;
    }

    private static bool TryParseFinal(string basename, out string? iso3)
    {
        iso3 = basename.Length == 6
            && basename.EndsWith(".db", StringComparison.Ordinal)
            && IsCanonicalIso3(basename.AsSpan(0, 3))
                ? basename[..3]
                : null;
        return iso3 is not null;
    }

    private static bool TryParseTemporary(
        CacheMutationSource source,
        string basename,
        out string? iso3,
        out string? identity)
    {
        iso3 = null;
        identity = null;
        string candidateName = basename.EndsWith(".owner", StringComparison.Ordinal)
            ? basename[..^".owner".Length]
            : basename;
        string suffix = candidateName.EndsWith(".tmp", StringComparison.Ordinal)
            ? ".tmp"
            : source == CacheMutationSource.Gadm
                && candidateName.EndsWith(".gpkg.download", StringComparison.Ordinal)
                    ? ".gpkg.download"
                    : string.Empty;
        if (suffix.Length == 0)
        {
            return false;
        }

        string stem = candidateName[..^suffix.Length];
        string[] segments = stem.Split('.');
        if (segments.Length != 3
            || !IsCanonicalIso3(segments[0])
            || !int.TryParse(segments[1], out int processId)
            || processId <= 0
            || !Guid.TryParseExact(segments[2], "N", out _))
        {
            return false;
        }

        iso3 = segments[0];
        identity = candidateName;
        return true;
    }

    private static bool IsCanonicalIso3(ReadOnlySpan<char> value) =>
        value.Length == 3
        && value[0] is >= 'A' and <= 'Z'
        && value[1] is >= 'A' and <= 'Z'
        && value[2] is >= 'A' and <= 'Z';

    private static CacheInventoryEntry UnavailableEntry(
        CacheMutationSource source,
        string iso3,
        CacheInventoryEntryStatus status,
        CacheInventoryDiagnosticCode? diagnosticCode,
        bool hasTemporary) =>
        new(source, iso3, status, null, null, null, null, hasTemporary, diagnosticCode);

    private sealed record SourceDefinition(
        CacheMutationSource Source,
        string DirectoryName);

    private sealed class DiscoveryResult
    {
        private DiscoveryResult(
            CacheInventorySourceStatus status,
            CacheInventoryDiagnosticCode? diagnosticCode,
            HashSet<string>? isos = null,
            HashSet<string>? finals = null,
            Dictionary<string, HashSet<string>>? temporaries = null,
            HashSet<string>? unsafeTemporaryIsos = null)
        {
            Status = status;
            DiagnosticCode = diagnosticCode;
            Isos = isos ?? new HashSet<string>(StringComparer.Ordinal);
            Finals = finals ?? new HashSet<string>(StringComparer.Ordinal);
            Temporaries = temporaries ?? new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            UnsafeTemporaryIsos = unsafeTemporaryIsos
                ?? new HashSet<string>(StringComparer.Ordinal);
        }

        internal CacheInventorySourceStatus Status { get; }
        internal CacheInventoryDiagnosticCode? DiagnosticCode { get; }
        internal HashSet<string> Isos { get; }
        internal HashSet<string> Finals { get; }
        internal Dictionary<string, HashSet<string>> Temporaries { get; }
        internal HashSet<string> UnsafeTemporaryIsos { get; }
        internal bool IsComplete => Status == CacheInventorySourceStatus.Ready;

        internal static DiscoveryResult Complete(
            HashSet<string>? isos = null,
            HashSet<string>? finals = null,
            Dictionary<string, HashSet<string>>? temporaries = null,
            HashSet<string>? unsafeTemporaryIsos = null) =>
            new(
                CacheInventorySourceStatus.Ready,
                null,
                isos,
                finals,
                temporaries,
                unsafeTemporaryIsos);

        internal static DiscoveryResult Truncated(CacheInventoryDiagnosticCode diagnosticCode) =>
            new(CacheInventorySourceStatus.Truncated, diagnosticCode);

        internal static DiscoveryResult Failed(
            CacheInventorySourceStatus status,
            CacheInventoryDiagnosticCode diagnosticCode) =>
            new(status, diagnosticCode);
    }
}
