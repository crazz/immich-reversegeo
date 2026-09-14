using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.WorkerJobs;

namespace ImmichReverseGeo.Web.Services;

internal enum CacheInventoryEntryStatus
{
    Available,
    Absent,
    InProgress,
    Invalid,
    Unreadable,
    Unsafe
}

internal enum CacheInventorySourceStatus
{
    Ready,
    Truncated,
    Unreadable,
    Unsafe
}

internal enum CacheInventoryDiagnosticCode
{
    SourceUnreadable,
    SourceUnsafe,
    EnumerationTruncated,
    LogicalCandidateLimitExceeded,
    TemporaryArtifactLimitExceeded,
    CandidateUnreadable,
    CandidateUnsafe,
    InvalidDatabase,
    MissingExpectedTable,
    InvalidMetadataSchema,
    InvalidMetadataValue,
    SchemaObjectLimitExceeded,
    CandidateChanged
}

internal sealed record CacheInventoryEntry
{
    public CacheInventoryEntry(
        CacheMutationSource source,
        string iso3,
        CacheInventoryEntryStatus status,
        long? sizeBytes,
        DateTimeOffset? lastModifiedUtc,
        DateTimeOffset? downloadedUtc,
        string? datasetVersion,
        bool hasTemporaryArtifacts,
        CacheInventoryDiagnosticCode? diagnosticCode)
    {
        if (!Enum.IsDefined(source))
        {
            throw new ArgumentOutOfRangeException(nameof(source));
        }

        CacheMutationRequest.RequireCanonicalIso3(iso3, nameof(iso3));
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        if (sizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeBytes));
        }

        RequireUtc(lastModifiedUtc, nameof(lastModifiedUtc));
        RequireUtc(downloadedUtc, nameof(downloadedUtc));
        if (datasetVersion is { Length: 0 })
        {
            throw new ArgumentException("Dataset version must be null or non-empty.", nameof(datasetVersion));
        }

        if (diagnosticCode is not null && !Enum.IsDefined(diagnosticCode.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(diagnosticCode));
        }

        Source = source;
        Iso3 = iso3;
        Status = status;
        SizeBytes = sizeBytes;
        LastModifiedUtc = lastModifiedUtc;
        DownloadedUtc = downloadedUtc;
        DatasetVersion = datasetVersion;
        HasTemporaryArtifacts = hasTemporaryArtifacts;
        DiagnosticCode = diagnosticCode;
    }

    public CacheMutationSource Source { get; }
    public string Iso3 { get; }
    public CacheInventoryEntryStatus Status { get; }
    public long? SizeBytes { get; }
    public DateTimeOffset? LastModifiedUtc { get; }
    public DateTimeOffset? DownloadedUtc { get; }
    public string? DatasetVersion { get; }
    public bool HasTemporaryArtifacts { get; }
    public CacheInventoryDiagnosticCode? DiagnosticCode { get; }

    private static void RequireUtc(DateTimeOffset? value, string parameterName)
    {
        if (value is not null && value.Value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must use UTC offset zero.", parameterName);
        }
    }
}

internal sealed record CacheInventorySourceSnapshot
{
    public CacheInventorySourceSnapshot(
        CacheMutationSource source,
        CacheInventorySourceStatus status,
        CacheInventoryDiagnosticCode? diagnosticCode,
        ImmutableArray<CacheInventoryEntry> entries)
    {
        if (!Enum.IsDefined(source))
        {
            throw new ArgumentOutOfRangeException(nameof(source));
        }

        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        if (diagnosticCode is not null && !Enum.IsDefined(diagnosticCode.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(diagnosticCode));
        }

        Source = source;
        Status = status;
        DiagnosticCode = diagnosticCode;
        Entries = entries.IsDefault ? ImmutableArray<CacheInventoryEntry>.Empty : entries;
    }

    public CacheMutationSource Source { get; }
    public CacheInventorySourceStatus Status { get; }
    public CacheInventoryDiagnosticCode? DiagnosticCode { get; }
    public ImmutableArray<CacheInventoryEntry> Entries { get; }
    public bool IsComplete => Status == CacheInventorySourceStatus.Ready;
}

internal sealed record CacheInventorySnapshot
{
    public CacheInventorySnapshot(
        long generation,
        DateTimeOffset observedAtUtc,
        ImmutableArray<CacheInventorySourceSnapshot> sources)
    {
        if (generation < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }

        if (observedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must use UTC offset zero.", nameof(observedAtUtc));
        }

        Generation = generation;
        ObservedAtUtc = observedAtUtc;
        Sources = sources.IsDefault ? ImmutableArray<CacheInventorySourceSnapshot>.Empty : sources;
    }

    public long Generation { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public ImmutableArray<CacheInventorySourceSnapshot> Sources { get; }
}

internal sealed record CacheInventoryExactResult(
    CacheInventoryEntry Entry,
    bool IsTemporaryDiscoveryComplete);

internal interface ICacheInventory
{
    Task<CacheInventorySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    Task<CacheInventorySnapshot> RefreshAsync(CancellationToken cancellationToken = default);

    Task<CacheInventoryExactResult> GetExactAsync(
        CacheMutationSource source,
        string iso3,
        CancellationToken cancellationToken = default);
}

internal interface ICacheInventoryInvalidator
{
    void InvalidateKey(CacheMutationSource source, string iso3);

    void InvalidateSource(CacheMutationSource source);

    void InvalidateAll();
}
