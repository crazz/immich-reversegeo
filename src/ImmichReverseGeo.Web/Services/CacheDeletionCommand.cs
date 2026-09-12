using ImmichReverseGeo.Core.Countries;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Web.Services;

internal sealed record CacheDeletionTarget(CacheMutationSource Source, string? Iso3);

internal enum CacheDeletionTargetDisposition
{
    Deleted,
    Missing,
    Invalid,
    Failed
}

internal sealed record CacheDeletionTargetResult(
    int RequestedIndex,
    CacheMutationSource? Source,
    string? Iso3,
    CacheDeletionTargetDisposition Disposition,
    string Message);

internal enum CacheDeletionOperationDisposition
{
    Completed,
    Busy,
    Unavailable
}

internal sealed class CacheDeletionOperationResult
{
    internal CacheDeletionOperationResult(
        CacheDeletionOperationDisposition disposition,
        IReadOnlyList<CacheDeletionTargetResult> targets,
        int eligibleUnattemptedCount,
        ExclusiveHeavyOwnerBusyMetadata? busyOwner,
        string? code,
        string? message)
    {
        Disposition = disposition;
        Targets = Array.AsReadOnly(targets.ToArray());
        EligibleUnattemptedCount = eligibleUnattemptedCount;
        BusyOwner = busyOwner;
        Code = code;
        Message = message;
        DeletedCount = Count(CacheDeletionTargetDisposition.Deleted);
        MissingCount = Count(CacheDeletionTargetDisposition.Missing);
        InvalidCount = Count(CacheDeletionTargetDisposition.Invalid);
        FailedCount = Count(CacheDeletionTargetDisposition.Failed);
    }

    internal CacheDeletionOperationDisposition Disposition { get; }
    internal IReadOnlyList<CacheDeletionTargetResult> Targets { get; }
    internal int EligibleUnattemptedCount { get; }
    internal ExclusiveHeavyOwnerBusyMetadata? BusyOwner { get; }
    internal string? Code { get; }
    internal string? Message { get; }
    internal int DeletedCount { get; }
    internal int MissingCount { get; }
    internal int InvalidCount { get; }
    internal int FailedCount { get; }

    private int Count(CacheDeletionTargetDisposition disposition) =>
        Targets.Count(target => target.Disposition == disposition);
}

internal enum CacheDeletionFileInspection
{
    Ready,
    Missing,
    Unsafe
}

internal interface ICacheDeletionFileSystem
{
    ValueTask<CacheDeletionFileInspection> InspectAsync(
        string sourceRoot,
        string finalPath);

    ValueTask DeleteAsync(string finalPath);
}

internal sealed class PhysicalCacheDeletionFileSystem : ICacheDeletionFileSystem
{
    public ValueTask<CacheDeletionFileInspection> InspectAsync(
        string sourceRoot,
        string finalPath)
    {
        try
        {
            string configuredRoot = MakeAbsoluteWithoutCollapsing(sourceRoot);
            string configuredFinalPath = MakeAbsoluteWithoutCollapsing(finalPath);
            CacheDeletionFileInspection rootInspection = InspectConfiguredPath(configuredRoot);
            if (rootInspection != CacheDeletionFileInspection.Ready)
            {
                return ValueTask.FromResult(rootInspection);
            }

            string canonicalRoot = Path.GetFullPath(configuredRoot);
            string canonicalFinalPath = Path.GetFullPath(configuredFinalPath);
            if (!IsContained(canonicalRoot, canonicalFinalPath))
            {
                return ValueTask.FromResult(CacheDeletionFileInspection.Unsafe);
            }

            return ValueTask.FromResult(InspectComponent(configuredFinalPath, requireDirectory: false));
        }
        catch (ArgumentException)
        {
            return ValueTask.FromResult(CacheDeletionFileInspection.Unsafe);
        }
        catch (NotSupportedException)
        {
            return ValueTask.FromResult(CacheDeletionFileInspection.Unsafe);
        }
    }

    private static CacheDeletionFileInspection InspectConfiguredPath(string configuredPath)
    {
        string? volumeRoot = Path.GetPathRoot(configuredPath);
        if (string.IsNullOrEmpty(volumeRoot))
        {
            return CacheDeletionFileInspection.Unsafe;
        }

        CacheDeletionFileInspection rootInspection = InspectComponent(volumeRoot, requireDirectory: true);
        if (rootInspection != CacheDeletionFileInspection.Ready)
        {
            return rootInspection;
        }

        string current = volumeRoot;
        string relative = configuredPath[volumeRoot.Length..];
        foreach (string component in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            CacheDeletionFileInspection inspection = InspectComponent(current, requireDirectory: true);
            if (inspection != CacheDeletionFileInspection.Ready)
            {
                return inspection;
            }
        }

        return CacheDeletionFileInspection.Ready;
    }

    private static string MakeAbsoluteWithoutCollapsing(string path) =>
        Path.IsPathFullyQualified(path)
            ? path
            : Path.Combine(Environment.CurrentDirectory, path);

    public ValueTask DeleteAsync(string finalPath)
    {
        File.Delete(finalPath);
        return ValueTask.CompletedTask;
    }

    private static CacheDeletionFileInspection InspectComponent(
        string path,
        bool requireDirectory)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return CacheDeletionFileInspection.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return CacheDeletionFileInspection.Missing;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            return CacheDeletionFileInspection.Unsafe;
        }

        bool isDirectory = (attributes & FileAttributes.Directory) != 0;
        if (isDirectory != requireDirectory)
        {
            return CacheDeletionFileInspection.Unsafe;
        }

        return CacheDeletionFileInspection.Ready;
    }

    private static bool IsContained(string sourceRoot, string finalPath)
    {
        string rootWithSeparator = Path.TrimEndingDirectorySeparator(sourceRoot)
            + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return finalPath.StartsWith(rootWithSeparator, comparison);
    }
}

internal sealed class CacheDeletionCommand(
    IWorkerJobAdmissionGate admission,
    ICacheDeletionFileSystem fileSystem,
    StorageOptions storage,
    CountryCodeService countryCodes,
    ILogger<CacheDeletionCommand> logger)
{
    private const string InvalidMessage = "The cache target is invalid.";
    private const string DeletedMessage = "The cache file was deleted.";
    private const string MissingMessage = "The cache file is already absent.";
    private const string FailedMessage = "The cache file could not be deleted.";
    private const string UnsafeMessage = "The cache path is not safe to delete.";

    internal Task<CacheDeletionOperationResult> DeleteAsync(CacheDeletionTarget target) =>
        ExecuteAsync(target.Source, [target]);

    internal async Task<CacheDeletionOperationResult> DeleteAllAsync(
        CacheMutationSource source,
        IEnumerable<CacheDeletionTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        return await ExecuteAsync(source, targets.ToArray()).ConfigureAwait(false);
    }

    private async Task<CacheDeletionOperationResult> ExecuteAsync(
        CacheMutationSource expectedSource,
        IReadOnlyList<CacheDeletionTarget> requestedTargets)
    {
        if (requestedTargets.Count == 0 && Enum.IsDefined(expectedSource))
        {
            return Completed([]);
        }

        PreparedTarget[] prepared = Prepare(expectedSource, requestedTargets)
            .OrderBy(target => target.RawSortKey, StringComparer.Ordinal)
            .ThenBy(target => target.RequestedIndex)
            .ToArray();
        CacheDeletionTargetResult[] invalidResults = prepared
            .Where(target => !target.IsEligible)
            .Select(target => target.InvalidResult!)
            .ToArray();
        PreparedTarget[] eligible = prepared
            .Where(target => target.IsEligible)
            .ToArray();
        if (eligible.Length == 0)
        {
            return Completed(invalidResults);
        }

        CacheMaintenanceAdmissionResult reservation =
            admission.TryReserveCacheMaintenance(CacheMaintenanceRequestOrigin.GeoBoundariesPage);
        if (reservation is CacheMaintenanceAdmissionResult.Busy busy)
        {
            return new CacheDeletionOperationResult(
                CacheDeletionOperationDisposition.Busy,
                invalidResults,
                eligible.Length,
                busy.ActiveOwner,
                null,
                "Cache deletion could not start because another operation is active.");
        }

        if (reservation is CacheMaintenanceAdmissionResult.Unavailable unavailable)
        {
            return new CacheDeletionOperationResult(
                CacheDeletionOperationDisposition.Unavailable,
                invalidResults,
                eligible.Length,
                null,
                unavailable.Code,
                unavailable.Message);
        }

        ICacheMaintenanceReservation lease =
            ((CacheMaintenanceAdmissionResult.Reserved)reservation).Reservation;
        var finalized = invalidResults.ToDictionary(result => result.RequestedIndex);
        CacheDeletionOperationResult finalizedResult;
        try
        {
            foreach (PreparedTarget target in eligible)
            {
                finalized.Add(
                    target.RequestedIndex,
                    await DeletePreparedAsync(target).ConfigureAwait(false));
            }

            finalizedResult = Completed(
                prepared.Select(target => finalized[target.RequestedIndex]).ToArray());
        }
        finally
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }

        return finalizedResult;
    }

    private PreparedTarget[] Prepare(
        CacheMutationSource expectedSource,
        IReadOnlyList<CacheDeletionTarget> requestedTargets)
    {
        var seen = new HashSet<(CacheMutationSource Source, string Iso3)>();
        var prepared = new PreparedTarget[requestedTargets.Count];
        bool expectedSourceValid = Enum.IsDefined(expectedSource);
        for (int index = 0; index < requestedTargets.Count; index++)
        {
            CacheDeletionTarget target = requestedTargets[index];
            string? canonicalIso3 = ValidateIdentity(target.Source, target.Iso3);
            bool valid = expectedSourceValid
                && target.Source == expectedSource
                && canonicalIso3 is not null
                && seen.Add((target.Source, canonicalIso3));
            prepared[index] = valid
                ? new PreparedTarget(index, target.Source, target.Iso3 ?? string.Empty, canonicalIso3!, null)
                : new PreparedTarget(
                    index,
                    target.Source,
                    target.Iso3 ?? string.Empty,
                    canonicalIso3,
                    new CacheDeletionTargetResult(
                        index,
                        null,
                        null,
                        CacheDeletionTargetDisposition.Invalid,
                        InvalidMessage));
        }

        return prepared;
    }

    private string? ValidateIdentity(CacheMutationSource source, string? iso3)
    {
        if (!Enum.IsDefined(source)
            || iso3 is null
            || iso3.Length != 3
            || iso3[0] is < 'A' or > 'Z'
            || iso3[1] is < 'A' or > 'Z'
            || iso3[2] is < 'A' or > 'Z'
            || countryCodes.FindByAlpha3(iso3) is null)
        {
            return null;
        }

        if (source == CacheMutationSource.Gadm)
        {
            string mapped = GadmCountryCodeMapper.ToGadmCode(iso3);
            if (mapped.Length != 3
                || mapped.Any(character => character is < 'A' or > 'Z'))
            {
                return null;
            }
        }

        return iso3;
    }

    private async Task<CacheDeletionTargetResult> DeletePreparedAsync(PreparedTarget target)
    {
        string failureStage = "inspect";
        try
        {
            string sourceDirectory = target.Source == CacheMutationSource.Overture
                ? "overture-divisions"
                : "gadm-divisions";
            string sourceRoot = Path.Combine(storage.DataDir, sourceDirectory);
            string finalPath = Path.Combine(sourceRoot, $"{target.Iso3}.db");
            CacheDeletionFileInspection inspection =
                await fileSystem.InspectAsync(sourceRoot, finalPath).ConfigureAwait(false);
            if (inspection == CacheDeletionFileInspection.Missing)
            {
                return target.Result(CacheDeletionTargetDisposition.Missing, MissingMessage);
            }

            if (inspection == CacheDeletionFileInspection.Unsafe)
            {
                logger.LogWarning(
                    "Refused an unsafe {CacheSource} cache deletion for {Iso3}.",
                    target.Source,
                    target.Iso3);
                return target.Result(CacheDeletionTargetDisposition.Failed, UnsafeMessage);
            }

            failureStage = "delete";
            await fileSystem.DeleteAsync(finalPath).ConfigureAwait(false);
            return target.Result(CacheDeletionTargetDisposition.Deleted, DeletedMessage);
        }
        catch (UnauthorizedAccessException exception)
        {
            return ExpectedFailure(target, failureStage, exception);
        }
        catch (System.Security.SecurityException exception)
        {
            return ExpectedFailure(target, failureStage, exception);
        }
        catch (IOException exception)
        {
            return ExpectedFailure(target, failureStage, exception);
        }
        catch (ArgumentException)
        {
            return target.Result(CacheDeletionTargetDisposition.Failed, UnsafeMessage);
        }
        catch (NotSupportedException)
        {
            return target.Result(CacheDeletionTargetDisposition.Failed, UnsafeMessage);
        }
    }

    private CacheDeletionTargetResult ExpectedFailure(
        PreparedTarget target,
        string failureStage,
        Exception exception)
    {
        string failureType = exception switch
        {
            UnauthorizedAccessException => nameof(UnauthorizedAccessException),
            System.Security.SecurityException => nameof(System.Security.SecurityException),
            _ => nameof(IOException)
        };
        logger.LogWarning(
            "Cache deletion failed for {CacheSource} {Iso3} during {FailureStage}: {FailureType} ({HResult}).",
            target.Source,
            target.Iso3,
            failureStage,
            failureType,
            exception.HResult);
        return target.Result(CacheDeletionTargetDisposition.Failed, FailedMessage);
    }

    private static CacheDeletionOperationResult Completed(
        IReadOnlyList<CacheDeletionTargetResult> targets) => new(
            CacheDeletionOperationDisposition.Completed,
            targets,
            0,
            null,
            null,
            null);

    private sealed record PreparedTarget(
        int RequestedIndex,
        CacheMutationSource Source,
        string RawSortKey,
        string? Iso3,
        CacheDeletionTargetResult? InvalidResult)
    {
        internal bool IsEligible => InvalidResult is null;

        internal CacheDeletionTargetResult Result(
            CacheDeletionTargetDisposition disposition,
            string message) => new(
                RequestedIndex,
                Source,
                Iso3,
                disposition,
                message);
    }
}
