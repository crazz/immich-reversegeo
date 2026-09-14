using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace ImmichReverseGeo.Core.WorkerJobs;

public enum CacheMutationSource
{
    Overture,
    Gadm
}

public enum CacheMutationOperation
{
    Ensure,
    Refresh
}

public enum CacheMutationDisposition
{
    AlreadyReady,
    Published
}

public enum CacheMutationProgressStep
{
    CheckingExisting,
    PreparingSource,
    Downloading,
    Exporting,
    ValidatingCandidate,
    Publishing,
    Completed
}

public sealed record CacheMutationRequest : IWorkerJobRequest
{
    public CacheMutationSource Source { get; }
    public CacheMutationOperation Operation { get; }
    public string Iso3 { get; }

    public CacheMutationRequest(
        CacheMutationSource source,
        CacheMutationOperation operation,
        string iso3)
    {
        if (!Enum.IsDefined(source))
        {
            throw new ArgumentOutOfRangeException(nameof(source));
        }

        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(nameof(operation));
        }

        RequireCanonicalIso3(iso3, nameof(iso3));
        Source = source;
        Operation = operation;
        Iso3 = iso3;
    }

    public static void RequireCanonicalIso3(string iso3, string parameterName)
    {
        if (iso3 is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        if (iso3.Length != 3
            || iso3[0] is < 'A' or > 'Z'
            || iso3[1] is < 'A' or > 'Z'
            || iso3[2] is < 'A' or > 'Z')
        {
            throw new ArgumentException(
                "The country code must be exactly three uppercase ASCII letters.",
                parameterName);
        }
    }
}

public sealed record CacheMutationGadmAttribution
{
    public const string OfficialDatasetName = "GADM";
    public const string OfficialLicenseUrl = "https://gadm.org/license.html";
    public const string NonCommercialUseNotice =
        "GADM data is available for academic and other non-commercial use.";

    public string DatasetName { get; }
    public string DatasetVersion { get; }
    public string LicenseUrl { get; }
    public string UsageNotice { get; }

    public CacheMutationGadmAttribution(
        string datasetName,
        string datasetVersion,
        string licenseUrl,
        string usageNotice)
    {
        if (!string.Equals(datasetName, OfficialDatasetName, StringComparison.Ordinal))
        {
            throw new ArgumentException("The GADM dataset name is not canonical.", nameof(datasetName));
        }

        WorkerJobProtocolV2.RequireSafeText(datasetVersion, nameof(datasetVersion));
        if (!string.Equals(licenseUrl, OfficialLicenseUrl, StringComparison.Ordinal))
        {
            throw new ArgumentException("The GADM license URL is not canonical.", nameof(licenseUrl));
        }

        if (!string.Equals(usageNotice, NonCommercialUseNotice, StringComparison.Ordinal))
        {
            throw new ArgumentException("The GADM usage notice is not canonical.", nameof(usageNotice));
        }

        DatasetName = datasetName;
        DatasetVersion = datasetVersion;
        LicenseUrl = licenseUrl;
        UsageNotice = usageNotice;
    }
}

public sealed record CacheMutationSourceResult
{
    public CacheMutationSource Source { get; }
    public CacheMutationOperation Operation { get; }
    public string Iso3 { get; }
    public CacheMutationDisposition Disposition { get; }
    public long RowCount { get; }
    public DateTimeOffset DownloadedAtUtc { get; }
    public long FileSizeBytes { get; }
    public string Version { get; }
    public CacheMutationGadmAttribution? GadmAttribution { get; }

    public CacheMutationSourceResult(
        CacheMutationSource source,
        CacheMutationOperation operation,
        string iso3,
        CacheMutationDisposition disposition,
        long rowCount,
        DateTimeOffset downloadedAtUtc,
        long fileSizeBytes,
        string version,
        CacheMutationGadmAttribution? gadmAttribution)
    {
        if (!Enum.IsDefined(source))
        {
            throw new ArgumentOutOfRangeException(nameof(source));
        }

        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(nameof(operation));
        }

        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition));
        }

        CacheMutationRequest.RequireCanonicalIso3(iso3, nameof(iso3));
        if (rowCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rowCount));
        }

        WorkerJobProtocolV2.RequireUtc(downloadedAtUtc, nameof(downloadedAtUtc));
        if (fileSizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fileSizeBytes));
        }

        WorkerJobProtocolV2.RequireSafeText(version, nameof(version));
        if ((source == CacheMutationSource.Gadm) != (gadmAttribution is not null))
        {
            throw new ArgumentException(
                "GADM attribution must be present exactly for GADM results.",
                nameof(gadmAttribution));
        }

        if (operation == CacheMutationOperation.Refresh
            && disposition == CacheMutationDisposition.AlreadyReady)
        {
            throw new ArgumentException(
                "A refresh operation must publish a replacement.",
                nameof(disposition));
        }

        if (gadmAttribution is not null
            && !string.Equals(gadmAttribution.DatasetVersion, version, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The GADM attribution version must match the result version.",
                nameof(gadmAttribution));
        }

        Source = source;
        Operation = operation;
        Iso3 = iso3;
        Disposition = disposition;
        RowCount = rowCount;
        DownloadedAtUtc = downloadedAtUtc;
        FileSizeBytes = fileSizeBytes;
        Version = version;
        GadmAttribution = gadmAttribution;
    }
}

public sealed record CacheMutationResult : IWorkerJobResult
{
    public DateTimeOffset StartedAtUtc { get; }
    public DateTimeOffset EndedAtUtc { get; }
    public CacheMutationSourceResult Cache { get; }

    public CacheMutationResult(
        DateTimeOffset startedAtUtc,
        DateTimeOffset endedAtUtc,
        CacheMutationSourceResult cache)
    {
        WorkerJobProtocolV2.RequireUtc(startedAtUtc, nameof(startedAtUtc));
        WorkerJobProtocolV2.RequireUtc(endedAtUtc, nameof(endedAtUtc));
        if (endedAtUtc < startedAtUtc)
        {
            throw new ArgumentException(
                "The terminal timestamp must not precede the start timestamp.",
                nameof(endedAtUtc));
        }

        Cache = cache ?? throw new ArgumentNullException(nameof(cache));
        StartedAtUtc = startedAtUtc;
        EndedAtUtc = endedAtUtc;
    }
}

public sealed record CacheMutationProgressPayload : WorkerJobOutputPayload
{
    public CacheMutationProgressStep Step { get; }
    public CacheMutationSource Source { get; }
    public CacheMutationOperation Operation { get; }
    public string Iso3 { get; }
    public string Message { get; }
    public CacheMutationGadmAttribution? GadmAttribution { get; }

    public CacheMutationProgressPayload(
        CacheMutationProgressStep step,
        CacheMutationSource source,
        CacheMutationOperation operation,
        string iso3,
        string message,
        CacheMutationGadmAttribution? gadmAttribution = null)
    {
        if (!Enum.IsDefined(step))
        {
            throw new ArgumentOutOfRangeException(nameof(step));
        }

        if (!Enum.IsDefined(source))
        {
            throw new ArgumentOutOfRangeException(nameof(source));
        }

        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(nameof(operation));
        }

        CacheMutationRequest.RequireCanonicalIso3(iso3, nameof(iso3));
        WorkerJobProtocolV2.RequireSafeText(message, nameof(message));
        if ((source == CacheMutationSource.Gadm) != (gadmAttribution is not null))
        {
            throw new ArgumentException(
                "GADM attribution must be present exactly for GADM progress.",
                nameof(gadmAttribution));
        }

        Step = step;
        Source = source;
        Operation = operation;
        Iso3 = iso3;
        Message = message;
        GadmAttribution = gadmAttribution;
    }
}

public interface ICacheMutationActivity : IAsyncDisposable
{
}

public interface ICacheMutationReporter
{
    ValueTask ReportProgressAsync(
        CacheMutationProgressPayload progress,
        CancellationToken cancellationToken);

    ValueTask ReportLogAsync(
        string level,
        string message,
        CancellationToken cancellationToken);

    ValueTask<ICacheMutationActivity> BeginActivityAsync(
        string label,
        CancellationToken cancellationToken);
}

public static class CacheMutationReporters
{
    public static ICacheMutationReporter None { get; } = new NullReporter();

    private sealed class NullReporter : ICacheMutationReporter
    {
        public ValueTask ReportProgressAsync(
            CacheMutationProgressPayload progress,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(progress);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask ReportLogAsync(
            string level,
            string message,
            CancellationToken cancellationToken)
        {
            WorkerJobProtocolV2.RequireSafeText(level, nameof(level));
            WorkerJobProtocolV2.RequireDiagnosticText(message, nameof(message));
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask<ICacheMutationActivity> BeginActivityAsync(
            string label,
            CancellationToken cancellationToken)
        {
            WorkerJobProtocolV2.RequireDiagnosticText(label, nameof(label));
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ICacheMutationActivity>(NullActivity.Instance);
        }
    }

    private sealed class NullActivity : ICacheMutationActivity
    {
        internal static NullActivity Instance { get; } = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public interface ICacheMutationSourceOperation
{
    CacheMutationSource Source { get; }

    ValueTask<CacheMutationSourceResult> ExecuteAsync(
        CacheMutationOperation operation,
        string iso3,
        ICacheMutationReporter reporter,
        CancellationToken cancellationToken);
}

public interface IWorkerCacheMutationOperation
{
    ValueTask<CacheMutationSourceResult> ExecuteAsync(
        CacheMutationRequest request,
        ICacheMutationReporter reporter,
        CancellationToken cancellationToken);
}

public sealed class WorkerCacheMutationOperation : IWorkerCacheMutationOperation
{
    private readonly IReadOnlyDictionary<CacheMutationSource, ICacheMutationSourceOperation> _sources;

    public WorkerCacheMutationOperation(IEnumerable<ICacheMutationSourceOperation> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var bySource = new Dictionary<CacheMutationSource, ICacheMutationSourceOperation>();
        foreach (ICacheMutationSourceOperation source in sources)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (!Enum.IsDefined(source.Source) || !bySource.TryAdd(source.Source, source))
            {
                throw new ArgumentException(
                    "Cache mutation sources must be defined and unique.",
                    nameof(sources));
            }
        }

        if (bySource.Count != 2
            || !bySource.ContainsKey(CacheMutationSource.Overture)
            || !bySource.ContainsKey(CacheMutationSource.Gadm))
        {
            throw new ArgumentException(
                "Exactly the Overture and GADM cache mutation sources must be registered.",
                nameof(sources));
        }

        _sources = new ReadOnlyDictionary<CacheMutationSource, ICacheMutationSourceOperation>(bySource);
    }

    public ValueTask<CacheMutationSourceResult> ExecuteAsync(
        CacheMutationRequest request,
        ICacheMutationReporter reporter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(reporter);
        return _sources[request.Source].ExecuteAsync(
            request.Operation,
            request.Iso3,
            reporter,
            cancellationToken);
    }
}
