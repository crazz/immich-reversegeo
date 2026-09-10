using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests.CacheDeletionCoordination;

[TestClass]
[TestCategory("Change52")]
public sealed class CacheDeletionCommandTests
{
    [TestMethod]
    public async Task DeleteAll_UnsortedMixedAndDuplicate_AttemptsAndReturnsOrdinalSnapshot()
    {
        var admission = new RecordingAdmissionGate();
        var fileSystem = new RecordingFileSystem();
        CacheDeletionCommand command = CreateCommand("/private/tmp/cache-delete-order", admission, fileSystem);

        CacheDeletionOperationResult result = await command.DeleteAllAsync(
            CacheMutationSource.Overture,
            [
                new CacheDeletionTarget(CacheMutationSource.Gadm, "AUS"),
                new CacheDeletionTarget(CacheMutationSource.Overture, "USA"),
                new CacheDeletionTarget(CacheMutationSource.Overture, "CAN"),
                new CacheDeletionTarget(CacheMutationSource.Overture, "USA"),
                new CacheDeletionTarget(CacheMutationSource.Overture, "fra"),
                new CacheDeletionTarget(CacheMutationSource.Overture, "DEU")
            ]);

        Assert.AreEqual(CacheDeletionOperationDisposition.Completed, result.Disposition);
        CollectionAssert.AreEqual(
            new[] { "CAN.db", "DEU.db", "USA.db" },
            fileSystem.DeletedPaths.Select(Path.GetFileName).ToArray(),
            "eligible filesystem work must use deterministic ordinal ISO3 order");
        CollectionAssert.AreEqual(
            new[] { 0, 2, 5, 1, 3, 4 },
            result.Targets.Select(target => target.RequestedIndex).ToArray(),
            "final results must use raw ISO3 ordinal order and original index for equal keys");
        Assert.AreEqual(3, result.DeletedCount);
        Assert.AreEqual(3, result.InvalidCount);
        Assert.AreEqual(1, admission.Reservation!.DisposeCount);

        var exposed = Assert.IsInstanceOfType<IList<CacheDeletionTargetResult>>(result.Targets);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            exposed[0] = exposed[0] with { Disposition = CacheDeletionTargetDisposition.Failed });
        Assert.AreEqual(3, result.DeletedCount, "finalized counts cannot change after release");
    }

    [TestMethod]
    public async Task Delete_ConfiguredLinkBeforeDotDot_IsRefusedWithoutDeletingEitherResolvedTarget()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The Unix symbolic-link path traversal contract is covered on Unix hosts.");
        }

        string canonicalTemp = Directory.Exists("/private/tmp")
            ? "/private/tmp"
            : Path.GetFullPath(Path.GetTempPath());
        string root = Path.Combine(canonicalTemp, $"cache-delete-link-{Guid.NewGuid():N}");
        try
        {
            string ordinaryData = Path.Combine(root, "data", "overture-divisions");
            string outsideParent = Path.Combine(root, "outside");
            string outsideChild = Path.Combine(outsideParent, "child");
            string resolvedData = Path.Combine(outsideParent, "data", "overture-divisions");
            string link = Path.Combine(root, "link");
            Directory.CreateDirectory(ordinaryData);
            Directory.CreateDirectory(outsideChild);
            Directory.CreateDirectory(resolvedData);
            Directory.CreateSymbolicLink(link, outsideChild);
            Assert.IsNotNull(Directory.ResolveLinkTarget(link, returnFinalTarget: false),
                "fixture must create an actual symbolic link");
            string ordinaryTarget = Path.Combine(ordinaryData, "USA.db");
            string resolvedTarget = Path.Combine(resolvedData, "USA.db");
            await File.WriteAllTextAsync(ordinaryTarget, "ordinary");
            await File.WriteAllTextAsync(resolvedTarget, "outside");
            var admission = new RecordingAdmissionGate();
            CacheDeletionCommand command = CreateCommand(
                Path.Combine(link, "..", "data"),
                admission,
                new PhysicalCacheDeletionFileSystem());

            CacheDeletionOperationResult result = await command.DeleteAsync(
                new CacheDeletionTarget(CacheMutationSource.Overture, "USA"));

            Assert.AreEqual(CacheDeletionOperationDisposition.Completed, result.Disposition);
            Assert.AreEqual(CacheDeletionTargetDisposition.Failed, result.Targets.Single().Disposition);
            Assert.IsTrue(File.Exists(ordinaryTarget), "lexically normalized sibling must remain unchanged");
            Assert.IsTrue(File.Exists(resolvedTarget), "linked resolved target must remain unchanged");
            Assert.AreEqual("ordinary", await File.ReadAllTextAsync(ordinaryTarget));
            Assert.AreEqual("outside", await File.ReadAllTextAsync(resolvedTarget));
            Assert.AreEqual(1, admission.Reservation!.DisposeCount);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task DeleteAll_EmptyAndInvalidOnly_CompleteWithoutAdmissionOrStorage()
    {
        var admission = new RecordingAdmissionGate
        {
            NextReservation = new CacheMaintenanceAdmissionResult.Busy(
                new ExclusiveHeavyOwnerBusyMetadata.Worker(new WorkerJobBusyMetadata(
                    WorkerJobCapabilityFamily.Processing,
                    WorkerJobRequestOrigin.Manual,
                    isCancellable: true)))
        };
        var fileSystem = new RecordingFileSystem();
        CacheDeletionCommand command = CreateCommand("/private/tmp/cache-delete-invalid", admission, fileSystem);

        CacheDeletionOperationResult empty = await command.DeleteAllAsync(
            CacheMutationSource.Overture,
            []);
        CacheDeletionOperationResult invalidOnly = await command.DeleteAllAsync(
            CacheMutationSource.Overture,
            [
                new CacheDeletionTarget(CacheMutationSource.Overture, "usa"),
                new CacheDeletionTarget(CacheMutationSource.Overture, " USA"),
                new CacheDeletionTarget(CacheMutationSource.Overture, "ZZZ"),
                new CacheDeletionTarget((CacheMutationSource)999, "USA"),
                new CacheDeletionTarget(CacheMutationSource.Overture, "日A本")
            ]);

        Assert.AreEqual(CacheDeletionOperationDisposition.Completed, empty.Disposition);
        Assert.AreEqual(0, empty.Targets.Count);
        Assert.AreEqual(CacheDeletionOperationDisposition.Completed, invalidOnly.Disposition);
        Assert.AreEqual(5, invalidOnly.InvalidCount);
        Assert.IsTrue(invalidOnly.Targets.All(target => target.Iso3 is null));
        Assert.AreEqual(0, admission.ReservationAttempts);
        Assert.AreEqual(0, fileSystem.InspectedPaths.Count);
        Assert.AreEqual(0, fileSystem.DeletedPaths.Count);
    }

    [TestMethod]
    public async Task DeleteAll_BusyAndUnavailable_PreserveInvalidsAndCountEligibleWithoutStorage()
    {
        var fileSystem = new RecordingFileSystem();
        var workerBusy = new ExclusiveHeavyOwnerBusyMetadata.Worker(new WorkerJobBusyMetadata(
            WorkerJobCapabilityFamily.Lookup,
            WorkerJobRequestOrigin.Manual,
            isCancellable: false));
        var busyAdmission = new RecordingAdmissionGate
        {
            NextReservation = new CacheMaintenanceAdmissionResult.Busy(workerBusy)
        };
        CacheDeletionOperationResult busy = await CreateCommand(
            "/private/tmp/cache-delete-busy",
            busyAdmission,
            fileSystem).DeleteAllAsync(
                CacheMutationSource.Overture,
                [
                    new CacheDeletionTarget(CacheMutationSource.Overture, "USA"),
                    new CacheDeletionTarget(CacheMutationSource.Overture, "usa"),
                    new CacheDeletionTarget(CacheMutationSource.Overture, "CAN")
                ]);

        var unavailableAdmission = new RecordingAdmissionGate
        {
            NextReservation = new CacheMaintenanceAdmissionResult.Unavailable(
                "cache-maintenance-stopped",
                "Cache maintenance is unavailable while the Web host is stopping.")
        };
        CacheDeletionOperationResult unavailable = await CreateCommand(
            "/private/tmp/cache-delete-unavailable",
            unavailableAdmission,
            fileSystem).DeleteAllAsync(
                CacheMutationSource.Gadm,
                [
                    new CacheDeletionTarget(CacheMutationSource.Gadm, "DEU"),
                    new CacheDeletionTarget(CacheMutationSource.Gadm, "DEU")
                ]);

        Assert.AreEqual(CacheDeletionOperationDisposition.Busy, busy.Disposition);
        Assert.AreEqual(1, busy.InvalidCount);
        Assert.AreEqual(2, busy.EligibleUnattemptedCount);
        Assert.AreSame(workerBusy, busy.BusyOwner);
        Assert.AreEqual(CacheDeletionOperationDisposition.Unavailable, unavailable.Disposition);
        Assert.AreEqual(1, unavailable.InvalidCount);
        Assert.AreEqual(1, unavailable.EligibleUnattemptedCount);
        Assert.AreEqual("cache-maintenance-stopped", unavailable.Code);
        Assert.AreEqual(0, fileSystem.InspectedPaths.Count);
        Assert.AreEqual(0, fileSystem.DeletedPaths.Count);
    }

    [TestMethod]
    public async Task InvalidIdentity_PrecedesRealShutdownFence_WhileValidIdentityIsUnavailable()
    {
        await using var coordinator = new WorkerJobCoordinator(WorkerJobDescriptors.Registered);
        await coordinator.BeginShutdown();
        var fileSystem = new RecordingFileSystem();
        CacheDeletionCommand command = CreateCommand(
            "/private/tmp/cache-delete-fenced",
            coordinator,
            fileSystem);

        CacheDeletionOperationResult invalid = await command.DeleteAsync(
            new CacheDeletionTarget(CacheMutationSource.Overture, "../"));
        CacheDeletionOperationResult valid = await command.DeleteAsync(
            new CacheDeletionTarget(CacheMutationSource.Overture, "USA"));

        Assert.AreEqual(CacheDeletionOperationDisposition.Completed, invalid.Disposition);
        Assert.AreEqual(CacheDeletionTargetDisposition.Invalid, invalid.Targets.Single().Disposition);
        Assert.IsNull(invalid.Targets.Single().Iso3, "invalid caller text must not be echoed");
        Assert.AreEqual(CacheDeletionOperationDisposition.Unavailable, valid.Disposition);
        Assert.AreEqual("cache-maintenance-stopped", valid.Code);
        Assert.AreEqual(1, valid.EligibleUnattemptedCount);
        Assert.AreEqual(0, fileSystem.InspectedPaths.Count);
        Assert.AreEqual(0, fileSystem.DeletedPaths.Count);
    }

    [TestMethod]
    [DataRow(CacheMutationSource.Overture, "overture-divisions")]
    [DataRow(CacheMutationSource.Gadm, "gadm-divisions")]
    public async Task BothSources_OrdinaryFailuresContinueAndMapTruthfulPerTargetOutcomes(
        CacheMutationSource source,
        string expectedDirectory)
    {
        var admission = new RecordingAdmissionGate();
        var fileSystem = new RecordingFileSystem();
        fileSystem.InspectFailures["CAN.db"] = new IOException("private host path and secret");
        fileSystem.Inspections["DEU.db"] = CacheDeletionFileInspection.Missing;
        fileSystem.DeleteFailures["FRA.db"] = new IOException("private host path and secret");
        CacheDeletionCommand command = CreateCommand("/private/tmp/cache-delete-outcomes", admission, fileSystem);

        CacheDeletionOperationResult result = await command.DeleteAllAsync(
            source,
            [
                new CacheDeletionTarget(source, "USA"),
                new CacheDeletionTarget(source, "DEU"),
                new CacheDeletionTarget(source, "CAN"),
                new CacheDeletionTarget(source, "FRA")
            ]);

        CollectionAssert.AreEqual(
            new[]
            {
                CacheDeletionTargetDisposition.Failed,
                CacheDeletionTargetDisposition.Missing,
                CacheDeletionTargetDisposition.Failed,
                CacheDeletionTargetDisposition.Deleted
            },
            result.Targets.Select(target => target.Disposition).ToArray());
        CollectionAssert.AreEqual(
            new[] { "FRA.db", "USA.db" },
            fileSystem.DeletedPaths.Select(Path.GetFileName).ToArray());
        Assert.IsTrue(
            fileSystem.InspectedPaths.All(path =>
                string.Equals(new DirectoryInfo(Path.GetDirectoryName(path)!).Name, expectedDirectory,
                    StringComparison.Ordinal)),
            "every derived final path must remain in the selected source directory");
        Assert.AreEqual(1, result.DeletedCount);
        Assert.AreEqual(1, result.MissingCount);
        Assert.AreEqual(2, result.FailedCount);
        Assert.IsFalse(result.Targets.Any(target => target.Message.Contains("secret", StringComparison.Ordinal)));
        Assert.AreEqual(1, admission.Reservation!.DisposeCount);
    }

    [TestMethod]
    public async Task ResultFinalization_PrecedesHeldReservationReleaseAndCommandReturn()
    {
        var reservation = new BlockingReservation();
        var admission = new RecordingAdmissionGate
        {
            NextReservation = new CacheMaintenanceAdmissionResult.Reserved(reservation)
        };
        var fileSystem = new RecordingFileSystem();
        CacheDeletionCommand command = CreateCommand(
            "/private/tmp/cache-delete-release-order",
            admission,
            fileSystem);

        Task<CacheDeletionOperationResult> operation = command.DeleteAllAsync(
            CacheMutationSource.Overture,
            [
                new CacheDeletionTarget(CacheMutationSource.Overture, "CAN"),
                new CacheDeletionTarget(CacheMutationSource.Overture, "DEU")
            ]);

        CacheDeletionOperationResult result = null!;
        try
        {
            await reservation.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            CollectionAssert.AreEqual(
                new[] { "CAN.db", "DEU.db" },
                fileSystem.DeletedPaths.Select(Path.GetFileName).ToArray(),
                "all target outcomes must be fixed before release begins");
            Assert.IsFalse(operation.IsCompleted,
                "the command must not return while reservation release is held");
        }
        finally
        {
            reservation.AllowDispose.TrySetResult();
            result = await operation.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.AreEqual(CacheDeletionOperationDisposition.Completed, result.Disposition);
        Assert.AreEqual(2, result.DeletedCount);
        Assert.AreEqual(1, reservation.DisposeCount);
        var exposed = Assert.IsInstanceOfType<IList<CacheDeletionTargetResult>>(result.Targets);
        Assert.ThrowsExactly<NotSupportedException>(() => exposed.RemoveAt(0));
        Assert.AreEqual(2, result.DeletedCount);
    }

    [TestMethod]
    [DataRow("inspect")]
    [DataRow("delete")]
    public async Task ExpectedFilesystemFailures_AreLoggedOnceWithSafeStructuredIdentityAndGenericResult(
        string failureStage)
    {
        const string hostile = "/private/host/SENTINEL Password=secret";
        Exception failure = failureStage == "inspect"
            ? new UnauthorizedAccessException(hostile)
            : new IOException(hostile);
        var fileSystem = new RecordingFileSystem();
        if (failureStage == "inspect")
        {
            fileSystem.InspectFailures["USA.db"] = failure;
        }
        else
        {
            fileSystem.DeleteFailures["USA.db"] = failure;
        }

        var logger = new CaptureLogger();
        CacheDeletionOperationResult result = await CreateCommand(
            "/private/tmp/cache-delete-logging",
            new RecordingAdmissionGate(),
            fileSystem,
            logger).DeleteAsync(new CacheDeletionTarget(CacheMutationSource.Overture, "USA"));

        Assert.AreEqual(CacheDeletionTargetDisposition.Failed, result.Targets.Single().Disposition);
        Assert.AreEqual("The cache file could not be deleted.", result.Targets.Single().Message);
        Assert.IsFalse(result.Targets.Single().Message.Contains(hostile, StringComparison.Ordinal));
        CapturedLog entry = logger.Entries.Single();
        Assert.AreEqual(LogLevel.Warning, entry.Level);
        Assert.IsNull(entry.Exception, "raw filesystem exceptions must not be attached to ILogger");
        Assert.AreEqual(CacheMutationSource.Overture, entry.State["CacheSource"]);
        Assert.AreEqual("USA", entry.State["Iso3"]);
        Assert.AreEqual(failureStage, entry.State["FailureStage"]);
        Assert.AreEqual(failure.GetType().Name, entry.State["FailureType"]);
        Assert.AreEqual(failure.HResult, entry.State["HResult"]);
        Assert.IsFalse(entry.Message.Contains(hostile, StringComparison.Ordinal));
        Assert.IsFalse(entry.State.Values.Any(value =>
            value?.ToString()?.Contains(hostile, StringComparison.Ordinal) == true));
    }

    [TestMethod]
    public async Task GadmAlias_UsesApplicationIso3ForFinalCacheName()
    {
        var admission = new RecordingAdmissionGate();
        var fileSystem = new RecordingFileSystem();
        CacheDeletionOperationResult result = await CreateCommand(
            "/private/tmp/cache-delete-alias",
            admission,
            fileSystem).DeleteAsync(new CacheDeletionTarget(CacheMutationSource.Gadm, "XKX"));

        Assert.AreEqual(CacheDeletionTargetDisposition.Deleted, result.Targets.Single().Disposition);
        Assert.AreEqual("XKX.db", Path.GetFileName(fileSystem.DeletedPaths.Single()));
        StringAssert.Contains(fileSystem.DeletedPaths.Single(), "gadm-divisions");
    }

    [TestMethod]
    public void BundledKnownCountries_HaveTotalCanonicalSourceMappings()
    {
        CountryCodeService countries = CountryCodeService.CreateForTest(
            Path.Combine(AppContext.BaseDirectory, "data"));

        foreach (string iso3 in countries.GetKnownIso3Codes())
        {
            Assert.IsNotNull(countries.FindByAlpha3(iso3), $"Overture identity missing for {iso3}");
            string gadm = ImmichReverseGeo.Gadm.Services.GadmCountryCodeMapper.ToGadmCode(iso3);
            Assert.AreEqual(3, gadm.Length, $"GADM mapping length for {iso3}");
            Assert.IsTrue(gadm.All(character => character is >= 'A' and <= 'Z'),
                $"GADM mapping must stay canonical for {iso3}");
        }
    }

    [TestMethod]
    public void CommandBoundary_HasOnlyCoordinatorIdentityStorageFilesystemAndLoggingDependencies()
    {
        Type[] constructorParameters = typeof(CacheDeletionCommand)
            .GetConstructors(System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic)
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        CollectionAssert.AreEquivalent(
            new[]
            {
                typeof(IWorkerJobAdmissionGate),
                typeof(ICacheDeletionFileSystem),
                typeof(StorageOptions),
                typeof(CountryCodeService),
                typeof(Microsoft.Extensions.Logging.ILogger<CacheDeletionCommand>)
            },
            constructorParameters);
        Type[] fieldTypes = typeof(CacheDeletionCommand)
            .GetFields(System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic)
            .Select(field => field.FieldType)
            .ToArray();
        Assert.IsFalse(fieldTypes.Contains(typeof(ProcessingState)));
        Assert.IsFalse(fieldTypes.Contains(typeof(ImmichReverseGeo.Overture.Services.OvertureDivisionCacheService)));
        Assert.IsFalse(fieldTypes.Contains(typeof(ImmichReverseGeo.Gadm.Services.GadmDivisionCacheService)));
        Assert.IsFalse(fieldTypes.Any(type => typeof(IProcessingRunExecutor).IsAssignableFrom(type)));
    }

    [TestMethod]
    public void DeletionProductionMethods_ReferenceNoForbiddenHeavyOrLifecycleBoundary()
    {
        Type[] productionTypes =
        [
            typeof(CacheDeletionCommand),
            typeof(PhysicalCacheDeletionFileSystem),
            typeof(CacheDeletionPageController)
        ];
        const System.Reflection.BindingFlags declared =
            System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Static
            | System.Reflection.BindingFlags.DeclaredOnly;
        System.Reflection.MethodBase[] roots = productionTypes
            .SelectMany(type => type.GetMethods(declared).Cast<System.Reflection.MethodBase>()
                .Concat(type.GetConstructors(declared)))
            .ToArray();

        IlWalkResult walk = TransitiveIlWalker.Walk(roots, typeof(CacheDeletionCommandTests).Assembly);
        string[] forbidden = walk.Members
            .Where(IsForbiddenDeletionReference)
            .Select(member => $"{member.DeclaringType?.FullName}.{member.Name}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(Array.Empty<string>(), forbidden,
            "deletion methods and their async state machines must retain the lightweight boundary");
    }

    [TestMethod]
    public async Task PhysicalFileSystem_OrdinaryMissingAndFinalLink_AreDistinguishedWithoutFollowingLink()
    {
        string root = CreateOrdinaryTemporaryRoot();
        try
        {
            string sourceRoot = Path.Combine(root, "overture-divisions");
            Directory.CreateDirectory(sourceRoot);
            string ordinary = Path.Combine(sourceRoot, "CAN.db");
            string operationOwnedCandidate = Path.Combine(sourceRoot, "CAN.refresh.tmp");
            await File.WriteAllTextAsync(ordinary, "canada");
            await File.WriteAllTextAsync(operationOwnedCandidate, "candidate");
            CacheDeletionCommand command = CreateCommand(
                root,
                new RecordingAdmissionGate(),
                new PhysicalCacheDeletionFileSystem());

            CacheDeletionOperationResult ordinaryAndMissing = await command.DeleteAllAsync(
                CacheMutationSource.Overture,
                [
                    new CacheDeletionTarget(CacheMutationSource.Overture, "DEU"),
                    new CacheDeletionTarget(CacheMutationSource.Overture, "CAN")
                ]);

            Assert.AreEqual(CacheDeletionTargetDisposition.Deleted, ordinaryAndMissing.Targets[0].Disposition);
            Assert.AreEqual(CacheDeletionTargetDisposition.Missing, ordinaryAndMissing.Targets[1].Disposition);
            Assert.IsFalse(File.Exists(ordinary));
            Assert.AreEqual("candidate", await File.ReadAllTextAsync(operationOwnedCandidate),
                "deletion must not remove refresh-owned candidates");

            if (!OperatingSystem.IsWindows())
            {
                string outside = Path.Combine(root, "outside.db");
                await File.WriteAllTextAsync(outside, "outside-content");
                string linkedFinal = Path.Combine(sourceRoot, "USA.db");
                File.CreateSymbolicLink(linkedFinal, outside);
                Assert.IsNotNull(File.ResolveLinkTarget(linkedFinal, returnFinalTarget: false));

                CacheDeletionOperationResult linked = await command.DeleteAsync(
                    new CacheDeletionTarget(CacheMutationSource.Overture, "USA"));

                Assert.AreEqual(CacheDeletionTargetDisposition.Failed, linked.Targets.Single().Disposition);
                Assert.IsTrue(File.Exists(linkedFinal), "the linked cache entry must not be removed");
                Assert.AreEqual("outside-content", await File.ReadAllTextAsync(outside));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task PhysicalFileSystem_UnixDirectoryPermissionFailure_IsFailedAndPreservesContent()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Unix directory permissions are covered on Unix hosts.");
        }

        string root = CreateOrdinaryTemporaryRoot();
        string sourceRoot = Path.Combine(root, "overture-divisions");
        Directory.CreateDirectory(sourceRoot);
        string target = Path.Combine(sourceRoot, "USA.db");
        string probe = Path.Combine(sourceRoot, "probe.db");
        await File.WriteAllTextAsync(target, "retained");
        await File.WriteAllTextAsync(probe, "probe");
        UnixFileMode originalMode = File.GetUnixFileMode(sourceRoot);
        try
        {
            File.SetUnixFileMode(
                sourceRoot,
                UnixFileMode.UserRead | UnixFileMode.UserExecute);
            Assert.ThrowsExactly<UnauthorizedAccessException>(() => File.Delete(probe),
                "fixture must achieve a real directory permission failure");

            CacheDeletionOperationResult result = await CreateCommand(
                root,
                new RecordingAdmissionGate(),
                new PhysicalCacheDeletionFileSystem()).DeleteAsync(
                    new CacheDeletionTarget(CacheMutationSource.Overture, "USA"));

            Assert.AreEqual(CacheDeletionTargetDisposition.Failed, result.Targets.Single().Disposition);
            Assert.AreEqual("retained", await File.ReadAllTextAsync(target));
        }
        finally
        {
            File.SetUnixFileMode(sourceRoot, originalMode);
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task PhysicalFileSystem_WindowsSharingAndReadOnlyFailures_AreFailedAndPreserveContent()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows sharing and read-only deletion semantics are covered on Windows hosts.");
        }

        string root = CreateOrdinaryTemporaryRoot();
        try
        {
            string sourceRoot = Path.Combine(root, "overture-divisions");
            Directory.CreateDirectory(sourceRoot);
            string shared = Path.Combine(sourceRoot, "CAN.db");
            string readOnly = Path.Combine(sourceRoot, "USA.db");
            await File.WriteAllTextAsync(shared, "shared");
            await File.WriteAllTextAsync(readOnly, "readonly");
            CacheDeletionCommand command = CreateCommand(
                root,
                new RecordingAdmissionGate(),
                new PhysicalCacheDeletionFileSystem());

            await using (var held = new FileStream(
                shared,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None))
            {
                Assert.ThrowsExactly<IOException>(() => File.Delete(shared),
                    "fixture must achieve a real sharing violation");
                CacheDeletionOperationResult sharing = await command.DeleteAsync(
                    new CacheDeletionTarget(CacheMutationSource.Overture, "CAN"));
                Assert.AreEqual(CacheDeletionTargetDisposition.Failed, sharing.Targets.Single().Disposition);
                Assert.IsTrue(File.Exists(shared));
            }

            File.SetAttributes(readOnly, File.GetAttributes(readOnly) | FileAttributes.ReadOnly);
            try
            {
                Assert.ThrowsExactly<UnauthorizedAccessException>(() => File.Delete(readOnly),
                    "fixture must achieve a real read-only deletion failure");
                CacheDeletionOperationResult readOnlyResult = await command.DeleteAsync(
                    new CacheDeletionTarget(CacheMutationSource.Overture, "USA"));
                Assert.AreEqual(CacheDeletionTargetDisposition.Failed, readOnlyResult.Targets.Single().Disposition);
                Assert.AreEqual("readonly", await File.ReadAllTextAsync(readOnly));
            }
            finally
            {
                File.SetAttributes(readOnly, FileAttributes.Normal);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateOrdinaryTemporaryRoot()
    {
        string parent = Directory.Exists("/private/tmp")
            ? "/private/tmp"
            : Path.GetFullPath(Path.GetTempPath());
        string root = Path.Combine(parent, $"cache-delete-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Assert.IsFalse((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0);
        return root;
    }

    private static CacheDeletionCommand CreateCommand(
        string dataDir,
        IWorkerJobAdmissionGate admission,
        ICacheDeletionFileSystem fileSystem,
        ILogger<CacheDeletionCommand>? logger = null) => new(
            admission,
            fileSystem,
            new StorageOptions(dataDir, Path.Combine(AppContext.BaseDirectory, "data")),
            CountryCodeService.CreateForTest(Path.Combine(AppContext.BaseDirectory, "data")),
            logger ?? NullLogger<CacheDeletionCommand>.Instance);

    private static bool IsForbiddenDeletionReference(System.Reflection.MemberInfo member)
    {
        if (member is System.Reflection.MethodBase method
            && ((method.DeclaringType == typeof(Task) && method.Name == nameof(Task.Run))
                || method.Name == "ClearAllPools"
                || (method.DeclaringType == typeof(Environment)
                    && method.Name is nameof(Environment.Exit) or "set_ExitCode")))
        {
            return true;
        }

        return ReferencedTypes(member).Any(IsForbiddenDeletionType);
    }

    private static IEnumerable<Type> ReferencedTypes(System.Reflection.MemberInfo member)
    {
        if (member.DeclaringType is not null)
        {
            yield return member.DeclaringType;
        }

        if (member is System.Reflection.FieldInfo field)
        {
            yield return field.FieldType;
        }
        else if (member is System.Reflection.PropertyInfo property)
        {
            yield return property.PropertyType;
        }
        else if (member is System.Reflection.MethodInfo info)
        {
            yield return info.ReturnType;
        }

        if (member is System.Reflection.MethodBase method)
        {
            foreach (System.Reflection.ParameterInfo parameter in method.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }
    }

    private static bool IsForbiddenDeletionType(Type type)
    {
        if (type.HasElementType)
        {
            return IsForbiddenDeletionType(type.GetElementType()!);
        }

        if (type.IsGenericType && type.GetGenericArguments().Any(IsForbiddenDeletionType))
        {
            return true;
        }

        string fullName = type.FullName ?? type.Name;
        string typeNamespace = type.Namespace ?? string.Empty;
        return type == typeof(System.Diagnostics.Process)
            || type == typeof(System.Diagnostics.ProcessStartInfo)
            || type == typeof(ProcessingState)
            || type == typeof(WorkerJobDispatch)
            || type == typeof(IWorkerJobAdmissionLease)
            || type == typeof(WorkerJobContext)
            || typeNamespace.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal)
            || typeNamespace.StartsWith("Npgsql", StringComparison.Ordinal)
            || typeNamespace.StartsWith("DuckDB", StringComparison.Ordinal)
            || typeNamespace.Contains("WorkerProtocol", StringComparison.Ordinal)
            || fullName.Contains("OvertureDivisionCacheService", StringComparison.Ordinal)
            || fullName.Contains("GadmDivisionCacheService", StringComparison.Ordinal)
            || fullName.Contains("AdministrativeAreaResolver", StringComparison.Ordinal)
            || fullName.Contains("Inventory", StringComparison.Ordinal)
            || fullName.Contains("Invalidation", StringComparison.Ordinal);
    }

    private sealed class RecordingAdmissionGate : IWorkerJobAdmissionGate
    {
        internal RecordingReservation? Reservation { get; private set; }
        internal CacheMaintenanceAdmissionResult? NextReservation { get; init; }
        internal int ReservationAttempts { get; private set; }

        public WorkerJobAdmissionResult TryAdmit(WorkerJobDispatch dispatch) =>
            throw new AssertFailedException("Deletion command tests do not admit worker jobs.");

        public CacheMaintenanceAdmissionResult TryReserveCacheMaintenance(
            CacheMaintenanceRequestOrigin origin)
        {
            ReservationAttempts++;
            Assert.AreEqual(CacheMaintenanceRequestOrigin.GeoBoundariesPage, origin);
            if (NextReservation is not null)
            {
                return NextReservation;
            }

            Reservation = new RecordingReservation();
            return new CacheMaintenanceAdmissionResult.Reserved(Reservation);
        }
    }

    private sealed class RecordingReservation : ICacheMaintenanceReservation
    {
        internal int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingReservation : ICacheMaintenanceReservation
    {
        internal TaskCompletionSource DisposeEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource AllowDispose { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int DisposeCount { get; private set; }

        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            DisposeEntered.TrySetResult();
            await AllowDispose.Task.ConfigureAwait(false);
        }
    }

    private sealed class RecordingFileSystem : ICacheDeletionFileSystem
    {
        internal Dictionary<string, CacheDeletionFileInspection> Inspections { get; } = [];
        internal Dictionary<string, Exception> InspectFailures { get; } = [];
        internal Dictionary<string, Exception> DeleteFailures { get; } = [];
        internal List<string> InspectedPaths { get; } = [];
        internal List<string> DeletedPaths { get; } = [];

        public ValueTask<CacheDeletionFileInspection> InspectAsync(
            string sourceRoot,
            string finalPath)
        {
            InspectedPaths.Add(finalPath);
            if (InspectFailures.TryGetValue(Path.GetFileName(finalPath), out Exception? failure))
            {
                throw failure;
            }

            return ValueTask.FromResult(
                Inspections.GetValueOrDefault(Path.GetFileName(finalPath), CacheDeletionFileInspection.Ready));
        }

        public ValueTask DeleteAsync(string finalPath)
        {
            DeletedPaths.Add(finalPath);
            if (DeleteFailures.TryGetValue(Path.GetFileName(finalPath), out Exception? failure))
            {
                throw failure;
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed record CapturedLog(
        LogLevel Level,
        string Message,
        Exception? Exception,
        IReadOnlyDictionary<string, object?> State);

    private sealed class CaptureLogger : ILogger<CacheDeletionCommand>
    {
        internal List<CapturedLog> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            IReadOnlyDictionary<string, object?> values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
                : new Dictionary<string, object?>();
            Entries.Add(new CapturedLog(logLevel, formatter(state, exception), exception, values));
        }
    }
}
