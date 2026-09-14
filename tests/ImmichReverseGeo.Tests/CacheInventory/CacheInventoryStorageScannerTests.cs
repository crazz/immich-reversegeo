using System.Collections.Immutable;
using System.Text;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.Services;
using Microsoft.Data.Sqlite;

namespace ImmichReverseGeo.Tests.CacheInventory;

[TestClass]
public sealed class CacheInventoryStorageScannerTests
{
    [TestMethod]
    [TestCategory("Change53")]
    public void InvalidWorkBounds_AreRejectedBeforeAnyStorageAccess()
    {
        CacheInventoryOptions[] invalidOptions =
        [
            new()
            {
                MaxVisitedEntriesPerSource = 1,
                MaxLogicalCandidatesPerSource = 2
            },
            new()
            {
                MaxMetadataValueCharacters = 1024,
                MaxSqliteValueBytes = 1024
            },
            new()
            {
                SqliteBusyTimeout = TimeSpan.FromSeconds(31)
            }
        ];
        var noIo = new NoIoFileSystem();

        foreach (CacheInventoryOptions options in invalidOptions)
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
                new CacheInventoryStorageScanner(
                    new StorageOptions(Path.GetTempPath(), Path.GetTempPath()),
                    noIo,
                    new RecordingMetadataReader(),
                    options,
                    TimeProvider.System));
        }

        Assert.AreEqual(0, noIo.Calls);
    }

    [TestMethod]
    [TestCategory("Change53")]
    public async Task MinimalSourceSchemas_ReturnBoundedMetadataWithoutDataRows()
    {
        using var fixture = new StorageFixture();
        string overture = fixture.CreateDatabase(
            CacheMutationSource.Overture,
            "CHE",
            includeMetadata: true,
            downloadedAt: "2026-09-10T00:00:00.0000000+00:00",
            version: "2026-09-01");
        string gadm = fixture.CreateDatabase(
            CacheMutationSource.Gadm,
            "JPN",
            includeMetadata: false);

        CacheInventorySnapshot snapshot = await fixture.Scanner.ScanAsync(7, CancellationToken.None);

        CacheInventoryEntry swiss = Entry(snapshot, CacheMutationSource.Overture, "CHE");
        Assert.AreEqual(CacheInventoryEntryStatus.Available, swiss.Status);
        Assert.AreEqual("2026-09-01", swiss.DatasetVersion);
        Assert.AreEqual(
            DateTimeOffset.Parse("2026-09-10T00:00:00+00:00"),
            swiss.DownloadedUtc);
        Assert.IsTrue(swiss.SizeBytes > 0);
        CacheInventoryEntry japan = Entry(snapshot, CacheMutationSource.Gadm, "JPN");
        Assert.AreEqual(CacheInventoryEntryStatus.Available, japan.Status);
        Assert.IsNull(japan.DatasetVersion);
        Assert.IsNull(japan.DownloadedUtc);

        File.Delete(overture);
        File.Delete(gadm);
        Assert.IsFalse(File.Exists(overture), "overture-handle-released");
        Assert.IsFalse(File.Exists(gadm), "gadm-handle-released");
    }

    [TestMethod]
    [TestCategory("Change53")]
    public async Task ProducerUtcTimestampFormats_AreAcceptedWithoutLosingMetadata()
    {
        using var fixture = new StorageFixture();
        fixture.CreateDatabase(
            CacheMutationSource.Overture,
            "CHE",
            includeMetadata: true,
            downloadedAt: new DateTime(2026, 9, 10, 1, 2, 3, 456, DateTimeKind.Utc).ToString("O"),
            version: "exporter-release");
        fixture.CreateDatabase(
            CacheMutationSource.Gadm,
            "JPN",
            includeMetadata: true,
            downloadedAt: "2026-09-09T08:00:00Z",
            version: "historical-version");

        CacheInventorySnapshot snapshot = await fixture.Scanner.ScanAsync(0, CancellationToken.None);

        CacheInventoryEntry exporter = Entry(snapshot, CacheMutationSource.Overture, "CHE");
        Assert.AreEqual(CacheInventoryEntryStatus.Available, exporter.Status);
        Assert.AreEqual("exporter-release", exporter.DatasetVersion);
        Assert.AreEqual(
            new DateTimeOffset(2026, 9, 10, 1, 2, 3, 456, TimeSpan.Zero),
            exporter.DownloadedUtc);
        CacheInventoryEntry historical = Entry(snapshot, CacheMutationSource.Gadm, "JPN");
        Assert.AreEqual(CacheInventoryEntryStatus.Available, historical.Status);
        Assert.AreEqual("historical-version", historical.DatasetVersion);
        Assert.AreEqual(
            new DateTimeOffset(2026, 9, 9, 8, 0, 0, TimeSpan.Zero),
            historical.DownloadedUtc);
    }

    [TestMethod]
    [TestCategory("Change53")]
    public async Task VisitOverflow_DiscardsSourcePrefixAndContinuesOtherSource()
    {
        using var fixture = new StorageFixture(new CacheInventoryOptions
        {
            MaxVisitedEntriesPerSource = 3,
            MaxLogicalCandidatesPerSource = 2,
            MaxTemporaryArtifactsPerIso = 2
        }, createScanner: false);
        var fileSystem = new BoundedEnumerationFileSystem(fixture.Root);
        var metadata = new RecordingMetadataReader();
        CacheInventoryStorageScanner scanner = fixture.CreateScanner(metadata, fileSystem);

        CacheInventorySnapshot snapshot = await scanner.ScanAsync(0, CancellationToken.None);

        CacheInventorySourceSnapshot overture = Source(snapshot, CacheMutationSource.Overture);
        Assert.AreEqual(CacheInventorySourceStatus.Truncated, overture.Status);
        Assert.AreEqual(CacheInventoryDiagnosticCode.EnumerationTruncated, overture.DiagnosticCode);
        Assert.AreEqual(0, overture.Entries.Length, "no-enumeration-prefix-is-published");
        Assert.AreEqual(4, fileSystem.MoveNextCalls, "visit-bound-plus-one");
        Assert.AreEqual(1, fileSystem.DisposeCalls, "overflow-disposes-enumerator");
        Assert.AreEqual(0, metadata.ReadCalls, "canonical-prefix-is-discarded-before-sqlite-open");
        Assert.AreEqual(CacheInventorySourceStatus.Ready,
            Source(snapshot, CacheMutationSource.Gadm).Status);
    }

    [TestMethod]
    [TestCategory("Change53")]
    public async Task ExactLookup_TruncatedDiscoveryNeverClaimsTemporaryAbsence()
    {
        using var fixture = new StorageFixture(new CacheInventoryOptions
        {
            MaxVisitedEntriesPerSource = 2,
            MaxLogicalCandidatesPerSource = 2,
            MaxTemporaryArtifactsPerIso = 2
        });
        for (var index = 0; index < 3; index++)
        {
            File.WriteAllText(Path.Combine(fixture.OvertureDirectory, $"junk-{index}"), "x");
        }

        CacheInventoryExactResult absent = await fixture.Scanner.ScanExactAsync(
            CacheMutationSource.Overture,
            "CHE",
            CancellationToken.None);

        Assert.IsFalse(absent.IsTemporaryDiscoveryComplete);
        Assert.AreEqual(CacheInventoryEntryStatus.Unreadable, absent.Entry.Status);
        Assert.AreNotEqual(CacheInventoryEntryStatus.Absent, absent.Entry.Status);

        fixture.CreateDatabase(CacheMutationSource.Overture, "CHE", includeMetadata: false);
        CacheInventoryExactResult available = await fixture.Scanner.ScanExactAsync(
            CacheMutationSource.Overture,
            "CHE",
            CancellationToken.None);
        Assert.IsFalse(available.IsTemporaryDiscoveryComplete);
        Assert.AreEqual(CacheInventoryEntryStatus.Available, available.Entry.Status);
        Assert.IsFalse(available.Entry.HasTemporaryArtifacts);
    }

    [TestMethod]
    [TestCategory("Change53")]
    public async Task ExactUnknownKey_ProvesCompleteAbsentWithoutSynthesizingAListingRow()
    {
        using var fixture = new StorageFixture(createScanner: false);
        var metadata = new RecordingMetadataReader();
        CacheInventoryStorageScanner scanner = fixture.CreateScanner(metadata);
        await using var inventory = new CacheInventoryService(scanner);

        CacheInventoryExactResult exact = await inventory.GetExactAsync(
            CacheMutationSource.Overture,
            "ZZZ");
        CacheInventorySnapshot listing = await inventory.GetSnapshotAsync();

        Assert.IsTrue(exact.IsTemporaryDiscoveryComplete);
        Assert.AreEqual(CacheInventoryEntryStatus.Absent, exact.Entry.Status);
        Assert.IsFalse(exact.Entry.HasTemporaryArtifacts);
        Assert.AreEqual(0, metadata.ReadCalls, "absent-final-never-opens-sqlite");
        Assert.IsFalse(Source(listing, CacheMutationSource.Overture).Entries
            .Any(entry => entry.Iso3 == "ZZZ"),
            "complete-absent-exact-result-is-not-a-synthesized-listing-row");
    }

    [TestMethod]
    [TestCategory("Change53")]
    public async Task OwnedTemporaryPatterns_AreLogicalAndOwnerSidecarsAreDeduplicated()
    {
        using var fixture = new StorageFixture(new CacheInventoryOptions
        {
            MaxTemporaryArtifactsPerIso = 1
        });
        const string guid = "0123456789abcdef0123456789abcdef";
        string overtureCandidate = $"CHE.123.{guid}.tmp";
        File.WriteAllText(Path.Combine(fixture.OvertureDirectory, overtureCandidate), "x");
        File.WriteAllText(Path.Combine(fixture.OvertureDirectory, overtureCandidate + ".owner"), "x");
        string gadmCandidate = $"JPN.456.{guid}.gpkg.download";
        File.WriteAllText(Path.Combine(fixture.GadmDirectory, gadmCandidate), "x");
        File.WriteAllText(Path.Combine(fixture.GadmDirectory, gadmCandidate + ".owner"), "x");

        CacheInventorySnapshot snapshot = await fixture.Scanner.ScanAsync(0, CancellationToken.None);

        CacheInventoryEntry swiss = Entry(snapshot, CacheMutationSource.Overture, "CHE");
        CacheInventoryEntry japan = Entry(snapshot, CacheMutationSource.Gadm, "JPN");
        Assert.AreEqual(CacheInventoryEntryStatus.InProgress, swiss.Status);
        Assert.AreEqual(CacheInventoryEntryStatus.InProgress, japan.Status);
        Assert.IsTrue(swiss.HasTemporaryArtifacts);
        Assert.IsTrue(japan.HasTemporaryArtifacts);
    }

    [TestMethod]
    [TestCategory("Change53")]
    public async Task FinalAndOwnedTemporary_CoexistAsAvailableWithPositiveTemporaryFact()
    {
        using var fixture = new StorageFixture();
        fixture.CreateDatabase(
            CacheMutationSource.Overture,
            "CHE",
            includeMetadata: true,
            version: "stable-release");
        File.WriteAllText(
            Path.Combine(
                fixture.OvertureDirectory,
                "CHE.123.0123456789abcdef0123456789abcdef.tmp"),
            "partial");

        CacheInventorySnapshot snapshot = await fixture.Scanner.ScanAsync(0, CancellationToken.None);

        CacheInventoryEntry entry = Entry(snapshot, CacheMutationSource.Overture, "CHE");
        Assert.AreEqual(CacheInventoryEntryStatus.Available, entry.Status);
        Assert.AreEqual("stable-release", entry.DatasetVersion);
        Assert.IsTrue(entry.HasTemporaryArtifacts);
    }

    [TestMethod]
    [TestCategory("Change53")]
    public async Task EmptyCorruptAndMalformedMetadataValues_AreClosedInvalidResults()
    {
        using var fixture = new StorageFixture();
        File.WriteAllBytes(Path.Combine(fixture.OvertureDirectory, "CHE.db"), []);
        File.WriteAllText(Path.Combine(fixture.OvertureDirectory, "JPN.db"), "not sqlite");
        fixture.CreateDatabase(
            CacheMutationSource.Gadm,
            "AUT",
            includeMetadata: true,
            downloadedAt: "not-a-time",
            version: "v1");

        CacheInventorySnapshot snapshot = await fixture.Scanner.ScanAsync(0, CancellationToken.None);

        Assert.AreEqual(CacheInventoryDiagnosticCode.InvalidDatabase,
            Entry(snapshot, CacheMutationSource.Overture, "CHE").DiagnosticCode);
        Assert.AreEqual(CacheInventoryDiagnosticCode.InvalidDatabase,
            Entry(snapshot, CacheMutationSource.Overture, "JPN").DiagnosticCode);
        Assert.AreEqual(CacheInventoryDiagnosticCode.InvalidMetadataValue,
            Entry(snapshot, CacheMutationSource.Gadm, "AUT").DiagnosticCode);
    }

    [TestMethod]
    [TestCategory("Change53")]
    public async Task LogicalAndTemporaryBounds_DiscardEverySourceRowWithExactDiagnostic()
    {
        using (var logicalFixture = new StorageFixture(new CacheInventoryOptions
        {
            MaxVisitedEntriesPerSource = 8,
            MaxLogicalCandidatesPerSource = 2,
            MaxTemporaryArtifactsPerIso = 2
        }))
        {
            File.WriteAllBytes(Path.Combine(logicalFixture.OvertureDirectory, "AUT.db"), [1]);
            File.WriteAllBytes(Path.Combine(logicalFixture.OvertureDirectory, "CHE.db"), [1]);
            File.WriteAllBytes(Path.Combine(logicalFixture.OvertureDirectory, "JPN.db"), [1]);

            CacheInventorySourceSnapshot source = Source(
                await logicalFixture.Scanner.ScanAsync(0, CancellationToken.None),
                CacheMutationSource.Overture);

            Assert.AreEqual(CacheInventorySourceStatus.Truncated, source.Status);
            Assert.AreEqual(CacheInventoryDiagnosticCode.LogicalCandidateLimitExceeded,
                source.DiagnosticCode);
            Assert.AreEqual(0, source.Entries.Length);
        }

        using var temporaryFixture = new StorageFixture(new CacheInventoryOptions
        {
            MaxVisitedEntriesPerSource = 8,
            MaxLogicalCandidatesPerSource = 2,
            MaxTemporaryArtifactsPerIso = 1
        });
        File.WriteAllText(Path.Combine(
            temporaryFixture.GadmDirectory,
            "CHE.1.0123456789abcdef0123456789abcdef.tmp"), "one");
        File.WriteAllText(Path.Combine(
            temporaryFixture.GadmDirectory,
            "CHE.2.fedcba9876543210fedcba9876543210.tmp"), "two");

        CacheInventorySourceSnapshot temporarySource = Source(
            await temporaryFixture.Scanner.ScanAsync(0, CancellationToken.None),
            CacheMutationSource.Gadm);
        Assert.AreEqual(CacheInventorySourceStatus.Truncated, temporarySource.Status);
        Assert.AreEqual(CacheInventoryDiagnosticCode.TemporaryArtifactLimitExceeded,
            temporarySource.DiagnosticCode);
        Assert.AreEqual(0, temporarySource.Entries.Length);
    }

    [TestMethod]
    [TestCategory("Change53")]
    public async Task InvalidNamesAndNestedCandidates_AreIgnoredAndExactInputsAreRejected()
    {
        using var fixture = new StorageFixture();
        File.WriteAllText(Path.Combine(fixture.OvertureDirectory, "che.db"), "junk");
        File.WriteAllText(Path.Combine(fixture.OvertureDirectory, "ÄAA.db"), "junk");
        string nested = Path.Combine(fixture.OvertureDirectory, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "CHE.db"), "nested-cache");

        CacheInventorySnapshot snapshot = await fixture.Scanner.ScanAsync(0, CancellationToken.None);

        Assert.AreEqual(0, Source(snapshot, CacheMutationSource.Overture).Entries.Length);
        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => fixture.Scanner.ScanExactAsync(
                CacheMutationSource.Overture, "che", CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => fixture.Scanner.ScanExactAsync(
                CacheMutationSource.Overture, "ÄAA", CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => fixture.Scanner.ScanExactAsync(
                CacheMutationSource.Overture, "../", CancellationToken.None));
    }

    [TestMethod]
    [TestCategory("Change53")]
    public async Task SourceEnumerationFailure_PreservesReadableOtherSourceWithoutPathDisclosure()
    {
        using var fixture = new StorageFixture(createScanner: false);
        fixture.CreateDatabase(CacheMutationSource.Gadm, "JPN", includeMetadata: false);
        CacheInventoryStorageScanner scanner = fixture.CreateScanner(
            new CacheInventorySqliteMetadataReader(),
            new FaultingSourceFileSystem(
                new PhysicalCacheInventoryFileSystem(),
                fixture.OvertureDirectory));

        CacheInventorySnapshot snapshot = await scanner.ScanAsync(0, CancellationToken.None);

        CacheInventorySourceSnapshot failed = Source(snapshot, CacheMutationSource.Overture);
        Assert.AreEqual(CacheInventorySourceStatus.Unreadable, failed.Status);
        Assert.AreEqual(CacheInventoryDiagnosticCode.SourceUnreadable, failed.DiagnosticCode);
        Assert.AreEqual(CacheInventoryEntryStatus.Available,
            Entry(snapshot, CacheMutationSource.Gadm, "JPN").Status);
        Assert.IsFalse(snapshot.ToString().Contains(fixture.Root, StringComparison.Ordinal));
    }

    [TestMethod]
    [TestCategory("Change53")]
    public async Task MissingSourceAndCandidateReadFailure_AreLocalClosedResults()
    {
        using var fixture = new StorageFixture(createScanner: false);
        Directory.Delete(fixture.OvertureDirectory);
        string gadm = fixture.CreateDatabase(CacheMutationSource.Gadm, "JPN", includeMetadata: false);
        var reader = new FaultingMetadataReader(
            new CacheInventorySqliteMetadataReader(),
            gadm);
        CacheInventoryStorageScanner scanner = fixture.CreateScanner(reader);

        CacheInventorySnapshot missing = await scanner.ScanAsync(0, CancellationToken.None);
        CacheInventorySourceSnapshot missingSource = Source(
            missing,
            CacheMutationSource.Overture);
        Assert.AreEqual(CacheInventorySourceStatus.Ready, missingSource.Status);
        Assert.AreEqual(0, missingSource.Entries.Length);
        CacheInventoryEntry unreadable = Entry(missing, CacheMutationSource.Gadm, "JPN");
        Assert.AreEqual(CacheInventoryEntryStatus.Unreadable, unreadable.Status);
        Assert.AreEqual(CacheInventoryDiagnosticCode.CandidateUnreadable,
            unreadable.DiagnosticCode);
        Assert.IsFalse(missing.ToString().Contains(fixture.Root, StringComparison.Ordinal));
    }

    [TestMethod]
    [TestCategory("Change53")]
    public async Task ManagedMetadataCharacterBound_RejectsOtherwiseReadableText()
    {
        using var fixture = new StorageFixture(new CacheInventoryOptions
        {
            MaxMetadataValueCharacters = 32,
            MaxSqliteValueBytes = 4096
        });
        fixture.CreateDatabase(
            CacheMutationSource.Overture,
            "CHE",
            includeMetadata: true,
            version: new string('x', 33));

        CacheInventoryExactResult result = await fixture.Scanner.ScanExactAsync(
            CacheMutationSource.Overture,
            "CHE",
            CancellationToken.None);

        Assert.AreEqual(CacheInventoryEntryStatus.Invalid, result.Entry.Status);
        Assert.AreEqual(CacheInventoryDiagnosticCode.InvalidMetadataValue,
            result.Entry.DiagnosticCode);
    }

    [TestMethod]
    [TestCategory("Change53")]
    public async Task ExclusiveSqliteLock_ReturnsBoundedUnreadableAndReleasesAllHandles()
    {
        using var fixture = new StorageFixture(new CacheInventoryOptions
        {
            SqliteBusyTimeout = TimeSpan.FromMilliseconds(100)
        });
        string path = fixture.CreateDatabase(
            CacheMutationSource.Overture,
            "CHE",
            includeMetadata: true,
            version: "locked-release");
        using var owner = new SqliteConnection($"Data Source={path};Pooling=false");
        owner.Open();
        using var control = owner.CreateCommand();
        control.CommandText = "BEGIN EXCLUSIVE;";
        control.ExecuteNonQuery();
        var lockAcquired = true;
        Task<CacheInventoryExactResult>? scan = null;
        try
        {
            Assert.IsTrue(lockAcquired, "exclusive-transaction-acquired-before-reader-start");
            scan = fixture.Scanner.ScanExactAsync(
                CacheMutationSource.Overture,
                "CHE",
                CancellationToken.None);

            CacheInventoryExactResult result = await scan.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(CacheInventoryEntryStatus.Unreadable, result.Entry.Status);
            Assert.AreEqual(CacheInventoryDiagnosticCode.CandidateUnreadable,
                result.Entry.DiagnosticCode);
        }
        finally
        {
            control.CommandText = "ROLLBACK;";
            control.ExecuteNonQuery();
            lockAcquired = false;
            if (scan is not null)
            {
                try
                {
                    await scan.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch
                {
                }
            }
        }

        owner.Close();
        File.Delete(path);
        Assert.IsFalse(lockAcquired);
        Assert.IsFalse(File.Exists(path), "busy-reader-and-owner-release-all-handles");
    }

    [TestMethod]
    [TestCategory("Change53")]
    public async Task MalformedMetadataShapeAndWrongSourceSchema_AreInvalid()
    {
        using var fixture = new StorageFixture();
        string malformed = fixture.CreateDatabase(
            CacheMutationSource.Overture,
            "CHE",
            includeMetadata: false);
        using (var connection = new SqliteConnection($"Data Source={malformed};Pooling=false"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE _meta (key TEXT, value TEXT);";
            command.ExecuteNonQuery();
        }

        string wrong = Path.Combine(fixture.GadmDirectory, "JPN.db");
        using (var connection = new SqliteConnection($"Data Source={wrong};Pooling=false"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE unrelated (id INTEGER);";
            command.ExecuteNonQuery();
        }

        CacheInventorySnapshot snapshot = await fixture.Scanner.ScanAsync(0, CancellationToken.None);

        CacheInventoryEntry swiss = Entry(snapshot, CacheMutationSource.Overture, "CHE");
        Assert.AreEqual(CacheInventoryEntryStatus.Invalid, swiss.Status);
        Assert.AreEqual(CacheInventoryDiagnosticCode.InvalidMetadataSchema, swiss.DiagnosticCode);
        CacheInventoryEntry japan = Entry(snapshot, CacheMutationSource.Gadm, "JPN");
        Assert.AreEqual(CacheInventoryEntryStatus.Invalid, japan.Status);
        Assert.AreEqual(CacheInventoryDiagnosticCode.MissingExpectedTable, japan.DiagnosticCode);
    }

    [TestMethod]
    [TestCategory("Change53")]
    public async Task OversizedMetadataValue_IsRejectedByConnectionLengthLimit()
    {
        using var fixture = new StorageFixture(new CacheInventoryOptions
        {
            MaxMetadataValueCharacters = 512,
            MaxSqliteValueBytes = 1024
        });
        string oversizedUtf8 = new('界', 400);
        Assert.AreEqual(400, oversizedUtf8.Length, "within-managed-character-bound");
        Assert.IsTrue(Encoding.UTF8.GetByteCount(oversizedUtf8) > 1024,
            "exceeds-native-sqlite-byte-bound");
        string path = fixture.CreateDatabase(
            CacheMutationSource.Overture,
            "CHE",
            includeMetadata: true,
            version: "small");
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=false"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE _meta SET value = $value WHERE key = 'release';";
            command.Parameters.AddWithValue("$value", oversizedUtf8);
            command.ExecuteNonQuery();
        }

        CacheInventoryExactResult result = await fixture.Scanner.ScanExactAsync(
            CacheMutationSource.Overture,
            "CHE",
            CancellationToken.None);

        Assert.AreEqual(CacheInventoryEntryStatus.Invalid, result.Entry.Status);
        Assert.AreEqual(CacheInventoryDiagnosticCode.InvalidMetadataValue, result.Entry.DiagnosticCode);
        File.Delete(path);
        Assert.IsFalse(File.Exists(path), "oversized-value-reader-released-handle");
    }

    [TestMethod]
    [TestCategory("Change53")]
    public async Task SchemaJunkObjects_AllConsumeTheObjectBudget()
    {
        using var fixture = new StorageFixture(new CacheInventoryOptions
        {
            MaxSchemaObjects = 3
        });
        string path = fixture.CreateDatabase(
            CacheMutationSource.Overture,
            "CHE",
            includeMetadata: true,
            version: "release");
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=false"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                CREATE INDEX junk_index ON division_area(id);
                CREATE VIEW junk_view AS SELECT id FROM division_area;
                CREATE TRIGGER junk_trigger AFTER INSERT ON division_area BEGIN SELECT 1; END;
                """;
            command.ExecuteNonQuery();
        }

        CacheInventoryExactResult result = await fixture.Scanner.ScanExactAsync(
            CacheMutationSource.Overture,
            "CHE",
            CancellationToken.None);

        Assert.AreEqual(CacheInventoryEntryStatus.Invalid, result.Entry.Status);
        Assert.AreEqual(
            CacheInventoryDiagnosticCode.SchemaObjectLimitExceeded,
            result.Entry.DiagnosticCode);
    }

    [TestMethod]
    [TestCategory("Change53")]
    public async Task AtomicReplacementDuringRead_RetriesAndReturnsStableNewMetadata()
    {
        using var fixture = new StorageFixture(createScanner: false);
        string final = fixture.CreateDatabase(
            CacheMutationSource.Overture,
            "CHE",
            includeMetadata: true,
            version: "old");
        string replacement = fixture.CreateDatabase(
            CacheMutationSource.Overture,
            "AUT",
            includeMetadata: true,
            version: "new-generation-release");
        File.SetLastWriteTimeUtc(final, new DateTime(2026, 9, 9, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(replacement, new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc));
        var firstReadStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRead = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new BarrierMetadataReader(
            new CacheInventorySqliteMetadataReader(),
            firstReadStarted,
            releaseFirstRead);
        CacheInventoryStorageScanner scanner = fixture.CreateScanner(reader);

        Task<CacheInventoryExactResult> scan = scanner.ScanExactAsync(
            CacheMutationSource.Overture,
            "CHE",
            CancellationToken.None);
        try
        {
            await firstReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            File.Move(replacement, final, overwrite: true);
            releaseFirstRead.TrySetResult();

            CacheInventoryExactResult result = await scan.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(CacheInventoryEntryStatus.Available, result.Entry.Status);
            Assert.AreEqual("new-generation-release", result.Entry.DatasetVersion);
            Assert.AreEqual(2, reader.ReadCalls);
        }
        finally
        {
            releaseFirstRead.TrySetResult();
            try
            {
                await scan.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
            }
        }
    }

    [TestMethod]
    [TestCategory("Change53")]
    public async Task DeletionDuringRead_RetriesToAbsentAndKeepsUnrelatedSource()
    {
        using var fixture = new StorageFixture(createScanner: false);
        string deleted = fixture.CreateDatabase(
            CacheMutationSource.Overture,
            "CHE",
            includeMetadata: true,
            version: "soon-deleted");
        fixture.CreateDatabase(CacheMutationSource.Gadm, "JPN", includeMetadata: false);
        var firstReadFinished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRead = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new BarrierMetadataReader(
            new CacheInventorySqliteMetadataReader(),
            firstReadFinished,
            releaseFirstRead);
        CacheInventoryStorageScanner scanner = fixture.CreateScanner(reader);
        Task<CacheInventorySnapshot> scan = scanner.ScanAsync(0, CancellationToken.None);
        try
        {
            await firstReadFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            File.Delete(deleted);
            releaseFirstRead.TrySetResult();

            CacheInventorySnapshot result = await scan.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(Source(result, CacheMutationSource.Overture).Entries
                .Any(entry => entry.Iso3 == "CHE"));
            Assert.AreEqual(CacheInventoryEntryStatus.Available,
                Entry(result, CacheMutationSource.Gadm, "JPN").Status);
            Assert.AreEqual(2, reader.ReadCalls,
                "one-deleted-read-plus-one-unrelated-source-read");
        }
        finally
        {
            releaseFirstRead.TrySetResult();
            try
            {
                await scan.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
            }
        }
    }

    [TestMethod]
    [TestCategory("Change53")]
    public async Task CandidateSymlink_IsReportedUnsafeWithoutOpeningTarget()
    {
        using var fixture = new StorageFixture(createScanner: false);
        var metadata = new RecordingMetadataReader();
        CacheInventoryStorageScanner scanner = fixture.CreateScanner(metadata);
        string target = Path.Combine(fixture.Root, "target.db");
        File.WriteAllText(target, "not-a-database");
        string link = Path.Combine(fixture.OvertureDirectory, "CHE.db");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or PlatformNotSupportedException
            or IOException)
        {
            Assert.Inconclusive("Symbolic links are unavailable on this host: " + exception.GetType().Name);
        }

        CacheInventoryExactResult result = await scanner.ScanExactAsync(
            CacheMutationSource.Overture,
            "CHE",
            CancellationToken.None);

        Assert.AreEqual(CacheInventoryEntryStatus.Unsafe, result.Entry.Status);
        Assert.AreEqual(CacheInventoryDiagnosticCode.CandidateUnsafe, result.Entry.DiagnosticCode);
        Assert.AreEqual(0, metadata.ReadCalls, "observed-link-is-never-opened-as-sqlite");
        Assert.IsTrue(File.Exists(target));
        Assert.AreEqual("not-a-database", File.ReadAllText(target));
    }

    [TestMethod]
    [TestCategory("Change53")]
    public async Task SourceDirectorySymlink_IsUnsafeWithoutEnumerationOrMetadataOpen()
    {
        using var fixture = new StorageFixture(createScanner: false);
        Directory.Delete(fixture.OvertureDirectory);
        string target = Path.Combine(fixture.Root, "outside-source");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "CHE.db"), "target");
        try
        {
            Directory.CreateSymbolicLink(fixture.OvertureDirectory, target);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or PlatformNotSupportedException
            or IOException)
        {
            Assert.Inconclusive("Symbolic links are unavailable on this host: " + exception.GetType().Name);
        }

        var metadata = new RecordingMetadataReader();
        CacheInventoryStorageScanner scanner = fixture.CreateScanner(metadata);
        CacheInventorySnapshot snapshot = await scanner.ScanAsync(0, CancellationToken.None);

        CacheInventorySourceSnapshot source = Source(snapshot, CacheMutationSource.Overture);
        Assert.AreEqual(CacheInventorySourceStatus.Unsafe, source.Status);
        Assert.AreEqual(CacheInventoryDiagnosticCode.SourceUnsafe, source.DiagnosticCode);
        Assert.AreEqual(0, source.Entries.Length);
        Assert.AreEqual(0, metadata.ReadCalls);
        Assert.AreEqual("target", File.ReadAllText(Path.Combine(target, "CHE.db")));
    }

    [TestMethod]
    [TestCategory("Change53")]
    public async Task SourceLinkDetectedAfterMetadataRead_DiscardsTheReadResult()
    {
        using var fixture = new StorageFixture(createScanner: false);
        var metadata = new AvailableMetadataReader("discard-me");
        var fileSystem = new SourceLinkRaceFileSystem(fixture.Root);
        CacheInventoryStorageScanner scanner = fixture.CreateScanner(metadata, fileSystem);

        CacheInventoryExactResult result = await scanner.ScanExactAsync(
            CacheMutationSource.Overture,
            "CHE",
            CancellationToken.None);

        Assert.AreEqual(1, metadata.ReadCalls, "race-occurs-after-bounded-metadata-read");
        Assert.AreEqual(CacheInventoryEntryStatus.Unsafe, result.Entry.Status);
        Assert.AreEqual(CacheInventoryDiagnosticCode.SourceUnsafe,
            result.Entry.DiagnosticCode);
        Assert.IsNull(result.Entry.DatasetVersion,
            "metadata-from-detected-source-link-race-is-discarded");
    }

    private static CacheInventoryEntry Entry(
        CacheInventorySnapshot snapshot,
        CacheMutationSource source,
        string iso3) =>
        Source(snapshot, source).Entries.Single(entry => entry.Iso3 == iso3);

    private static CacheInventorySourceSnapshot Source(
        CacheInventorySnapshot snapshot,
        CacheMutationSource source) =>
        snapshot.Sources.Single(result => result.Source == source);

    private sealed class BarrierMetadataReader(
        ICacheInventoryMetadataReader inner,
        TaskCompletionSource firstReadStarted,
        TaskCompletionSource releaseFirstRead) : ICacheInventoryMetadataReader
    {
        internal int ReadCalls { get; private set; }

        public async Task<CacheInventoryMetadataResult> ReadAsync(
            string databasePath,
            CacheMutationSource source,
            CacheInventoryOptions options,
            CancellationToken cancellationToken)
        {
            ReadCalls++;
            CacheInventoryMetadataResult result = await inner.ReadAsync(
                databasePath,
                source,
                options,
                cancellationToken);
            if (ReadCalls == 1)
            {
                firstReadStarted.TrySetResult();
                await releaseFirstRead.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }

    private sealed class StorageFixture : IDisposable
    {
        private readonly CacheInventoryOptions _options;

        internal StorageFixture(
            CacheInventoryOptions? options = null,
            bool createScanner = true)
        {
            _options = options ?? new CacheInventoryOptions();
            Root = Path.Combine(Path.GetTempPath(), "cache-inventory-" + Guid.NewGuid().ToString("N"));
            OvertureDirectory = Path.Combine(Root, "overture-divisions");
            GadmDirectory = Path.Combine(Root, "gadm-divisions");
            Directory.CreateDirectory(OvertureDirectory);
            Directory.CreateDirectory(GadmDirectory);
            Scanner = createScanner
                ? CreateScanner(new CacheInventorySqliteMetadataReader())
                : null!;
        }

        internal string Root { get; }
        internal string OvertureDirectory { get; }
        internal string GadmDirectory { get; }
        internal CacheInventoryStorageScanner Scanner { get; private set; }

        internal CacheInventoryStorageScanner CreateScanner(
            ICacheInventoryMetadataReader reader,
            ICacheInventoryFileSystem? fileSystem = null)
        {
            Scanner = new CacheInventoryStorageScanner(
                new StorageOptions(Root, Root),
                fileSystem ?? new PhysicalCacheInventoryFileSystem(),
                reader,
                _options,
                TimeProvider.System);
            return Scanner;
        }

        internal string CreateDatabase(
            CacheMutationSource source,
            string iso3,
            bool includeMetadata,
            string? downloadedAt = null,
            string? version = null)
        {
            string directory = source == CacheMutationSource.Overture
                ? OvertureDirectory
                : GadmDirectory;
            string path = Path.Combine(directory, iso3 + ".db");
            using var connection = new SqliteConnection($"Data Source={path};Pooling=false");
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = source == CacheMutationSource.Overture
                ? "CREATE TABLE division_area (id INTEGER);"
                : "CREATE TABLE gadm_area (id INTEGER);";
            command.ExecuteNonQuery();
            if (!includeMetadata)
            {
                return path;
            }

            command.CommandText = "CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);";
            command.ExecuteNonQuery();
            if (downloadedAt is not null)
            {
                Insert(command, "downloadedAt", downloadedAt);
            }

            if (version is not null)
            {
                Insert(command, source == CacheMutationSource.Overture ? "release" : "version", version);
            }

            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
            }
        }

        private static void Insert(SqliteCommand command, string key, string value)
        {
            command.CommandText = "INSERT INTO _meta (key, value) VALUES ($key, $value);";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        }
    }

    private sealed class RecordingMetadataReader : ICacheInventoryMetadataReader
    {
        internal int ReadCalls { get; private set; }

        public Task<CacheInventoryMetadataResult> ReadAsync(
            string databasePath,
            CacheMutationSource source,
            CacheInventoryOptions options,
            CancellationToken cancellationToken)
        {
            ReadCalls++;
            return Task.FromResult(new CacheInventoryMetadataResult(
                CacheInventoryMetadataStatus.Available,
                null,
                null,
                null));
        }
    }

    private sealed class BoundedEnumerationFileSystem(string root) : ICacheInventoryFileSystem
    {
        internal int MoveNextCalls { get; private set; }
        internal int DisposeCalls { get; private set; }

        public CacheInventoryPathObservation Observe(string path) =>
            path.EndsWith("overture-divisions", StringComparison.Ordinal)
                || path.EndsWith("gadm-divisions", StringComparison.Ordinal)
                ? new CacheInventoryPathObservation(
                    CacheInventoryPathKind.Directory,
                    0,
                    DateTimeOffset.UnixEpoch)
                : new CacheInventoryPathObservation(
                    CacheInventoryPathKind.RegularFile,
                    1,
                    DateTimeOffset.UnixEpoch);

        public IEnumerable<string> EnumerateImmediateEntries(string sourceDirectory) =>
            sourceDirectory.EndsWith("overture-divisions", StringComparison.Ordinal)
                ? new CountingEnumerable(
                    [
                        Path.Combine(root, "overture-divisions", "CHE.db"),
                        Path.Combine(root, "overture-divisions", "junk-1"),
                        Path.Combine(root, "overture-divisions", "junk-2"),
                        Path.Combine(root, "overture-divisions", "junk-3"),
                        Path.Combine(root, "overture-divisions", "junk-never-observed")
                    ],
                    this)
                : Array.Empty<string>();

        private sealed class CountingEnumerable(
            IReadOnlyList<string> entries,
            BoundedEnumerationFileSystem owner) : IEnumerable<string>, IEnumerator<string>
        {
            private int _index = -1;

            public string Current => entries[_index];
            object System.Collections.IEnumerator.Current => Current;
            public IEnumerator<string> GetEnumerator() => this;
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

            public bool MoveNext()
            {
                owner.MoveNextCalls++;
                _index++;
                return _index < entries.Count;
            }

            public void Reset() => throw new NotSupportedException();

            public void Dispose()
            {
                owner.DisposeCalls++;
            }
        }
    }

    private sealed class FaultingSourceFileSystem(
        ICacheInventoryFileSystem inner,
        string failingDirectory) : ICacheInventoryFileSystem
    {
        public CacheInventoryPathObservation Observe(string path) => inner.Observe(path);

        public IEnumerable<string> EnumerateImmediateEntries(string sourceDirectory)
        {
            if (string.Equals(sourceDirectory, failingDirectory, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("achieved-source-denial");
            }

            return inner.EnumerateImmediateEntries(sourceDirectory);
        }
    }

    private sealed class FaultingMetadataReader(
        ICacheInventoryMetadataReader inner,
        string failingPath) : ICacheInventoryMetadataReader
    {
        public Task<CacheInventoryMetadataResult> ReadAsync(
            string databasePath,
            CacheMutationSource source,
            CacheInventoryOptions options,
            CancellationToken cancellationToken)
        {
            if (string.Equals(databasePath, failingPath, StringComparison.Ordinal))
            {
                throw new IOException("achieved-candidate-read-failure");
            }

            return inner.ReadAsync(databasePath, source, options, cancellationToken);
        }
    }

    private sealed class SourceLinkRaceFileSystem(string root) : ICacheInventoryFileSystem
    {
        private int _overtureSourceObservations;

        public CacheInventoryPathObservation Observe(string path)
        {
            if (string.Equals(
                    path,
                    Path.Combine(root, "overture-divisions"),
                    StringComparison.Ordinal))
            {
                return Interlocked.Increment(ref _overtureSourceObservations) >= 3
                    ? new CacheInventoryPathObservation(
                        CacheInventoryPathKind.Unsafe,
                        0,
                        DateTimeOffset.UnixEpoch)
                    : new CacheInventoryPathObservation(
                        CacheInventoryPathKind.Directory,
                        0,
                        DateTimeOffset.UnixEpoch);
            }

            if (string.Equals(
                    path,
                    Path.Combine(root, "gadm-divisions"),
                    StringComparison.Ordinal))
            {
                return new CacheInventoryPathObservation(
                    CacheInventoryPathKind.Directory,
                    0,
                    DateTimeOffset.UnixEpoch);
            }

            return new CacheInventoryPathObservation(
                CacheInventoryPathKind.RegularFile,
                1,
                DateTimeOffset.UnixEpoch);
        }

        public IEnumerable<string> EnumerateImmediateEntries(string sourceDirectory) =>
            string.Equals(
                sourceDirectory,
                Path.Combine(root, "overture-divisions"),
                StringComparison.Ordinal)
                ? [Path.Combine(sourceDirectory, "CHE.db")]
                : [];
    }

    private sealed class AvailableMetadataReader(string version) : ICacheInventoryMetadataReader
    {
        internal int ReadCalls { get; private set; }

        public Task<CacheInventoryMetadataResult> ReadAsync(
            string databasePath,
            CacheMutationSource source,
            CacheInventoryOptions options,
            CancellationToken cancellationToken)
        {
            ReadCalls++;
            return Task.FromResult(new CacheInventoryMetadataResult(
                CacheInventoryMetadataStatus.Available,
                null,
                version,
                null));
        }
    }

    private sealed class NoIoFileSystem : ICacheInventoryFileSystem
    {
        internal int Calls { get; private set; }

        public CacheInventoryPathObservation Observe(string path)
        {
            Calls++;
            throw new AssertFailedException("invalid-options-must-not-observe-storage");
        }

        public IEnumerable<string> EnumerateImmediateEntries(string sourceDirectory)
        {
            Calls++;
            throw new AssertFailedException("invalid-options-must-not-enumerate-storage");
        }
    }
}
