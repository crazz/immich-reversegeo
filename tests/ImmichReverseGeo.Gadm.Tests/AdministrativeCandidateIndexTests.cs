using ImmichReverseGeo.Spatial;
using Microsoft.Data.Sqlite;

namespace ImmichReverseGeo.Gadm.Tests;

[TestClass]
public sealed class AdministrativeCandidateIndexTests
{
    [TestMethod]
    [DataRow(GeometrySource.Gadm, "y")]
    [DataRow(GeometrySource.Gadm, "x")]
    [DataRow(GeometrySource.Gadm, "none")]
    [DataRow(GeometrySource.Overture, "y")]
    [DataRow(GeometrySource.Overture, "x")]
    [DataRow(GeometrySource.Overture, "none")]
    public void PersistentIndex_PreservesOrderedMetadataAndExactBounds(GeometrySource source, string index)
    {
        using var fixture = new Fixture(source, index);
        var before = fixture.ReadAllSource();
        var points = new[] { (-0.5, -0.5), (0.2, 0.2), (0.200000001, 0.2), (1.0, 1.0), (5.0, 5.0) };
        var expected = points.Select(point => fixture.Read(point.Item1, point.Item2, false)).ToArray();

        Assert.IsTrue(AdministrativeCandidateIndex.Build(fixture.Connection, source, CancellationToken.None));
        Assert.IsFalse(AdministrativeCandidateIndex.NeedsBuild(fixture.Connection, source, CancellationToken.None));
        fixture.Reopen();
        for (var i = 0; i < points.Length; i++)
        {
            var actual = fixture.Read(points[i].Item1, points[i].Item2, true);
            CollectionAssert.AreEqual(expected[i], actual, $"{source}/{index}/{points[i]}");
        }

        CollectionAssert.AreEqual(before, fixture.ReadAllSource(), "Source rows, WKB, and metadata must not change.");
    }

    [TestMethod]
    [DataRow(GeometrySource.Gadm)]
    [DataRow(GeometrySource.Overture)]
    public void UnknownOrCorruptAcceleration_LeavesLegacyResultsAvailable(GeometrySource source)
    {
        using var fixture = new Fixture(source, "y");
        var expected = fixture.Read(0, 0, false);
        Assert.IsFalse(fixture.Configure(0, 0));
        Assert.IsTrue(AdministrativeCandidateIndex.Build(fixture.Connection, source, CancellationToken.None));
        fixture.Execute("DELETE FROM rg_candidates WHERE source_rowid=1");
        Assert.IsFalse(fixture.Configure(0, 0));
        CollectionAssert.AreEqual(expected, fixture.Read(0, 0, false));
        Assert.IsTrue(AdministrativeCandidateIndex.Build(fixture.Connection, source, CancellationToken.None));
        fixture.Execute($"UPDATE _meta SET value='9:future' WHERE key='{AdministrativeCandidateIndex.VersionKey}'");
        Assert.IsFalse(fixture.Configure(0, 0));
        Assert.IsFalse(AdministrativeCandidateIndex.NeedsBuild(fixture.Connection, source, CancellationToken.None));
        Assert.IsFalse(AdministrativeCandidateIndex.Build(fixture.Connection, source, CancellationToken.None));
        CollectionAssert.AreEqual(expected, fixture.Read(0, 0, false));
    }

    [TestMethod]
    public void ReverseScan_FallsBackWithoutChangingTheOriginalStatement()
    {
        using var fixture = new Fixture(GeometrySource.Gadm, "none");
        Assert.IsTrue(AdministrativeCandidateIndex.Build(fixture.Connection, fixture.Source, CancellationToken.None));
        fixture.Execute("PRAGMA reverse_unordered_selects=ON");
        Assert.IsFalse(fixture.Configure(0, 0));
        Assert.HasCount(4, fixture.Read(0, 0, false));
    }

    [TestMethod]
    public void LegacyOvertureWithoutAdminLevel_RetainsNullFieldAndNullGeometry()
    {
        using var fixture = new Fixture(GeometrySource.Overture, "y", legacy: true);
        var before = fixture.Read(0, 0, false);
        Assert.IsTrue(AdministrativeCandidateIndex.Build(fixture.Connection, fixture.Source, CancellationToken.None));
        CollectionAssert.AreEqual(before, fixture.Read(0, 0, true));
    }

    [TestMethod]
    public void CancelledBuild_DoesNotPublishAcceleration()
    {
        using var fixture = new Fixture(GeometrySource.Gadm, "y");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            AdministrativeCandidateIndex.Build(fixture.Connection, fixture.Source, cancellation.Token));
        Assert.IsFalse(fixture.Configure(0, 0));
    }

    [TestMethod]
    [DataRow(GeometrySource.Gadm)]
    [DataRow(GeometrySource.Overture)]
    public void UnpublishedBuild_UnsupportedBoundsRetainsEverySourceValue(GeometrySource source)
    {
        using var fixture = new Fixture(source, "y");
        var table = source == GeometrySource.Gadm ? "gadm_area" : "division_area";
        fixture.Execute($"UPDATE {table} SET bbox_xmin=2,bbox_xmax=1 WHERE id='z'");
        var before = fixture.ReadAllSource();
        var builder = new AdministrativeCandidatePreparation(source,
            static (path, _) => new SqliteConnection($"Data Source={path};Mode=ReadWrite;Pooling=false"));

        Assert.IsFalse(builder.BuildFile(fixture.Connection.DataSource, default), "Optional acceleration is unavailable.");
        CollectionAssert.AreEqual(before, fixture.ReadAllSource(), "Authoritative export survives optional build failure.");
        Assert.IsFalse(fixture.Configure(0, 0), "Incomplete acceleration cannot become eligible.");
    }

    [TestMethod]
    [DataRow(GeometrySource.Gadm, 1)]
    [DataRow(GeometrySource.Gadm, 7)]
    [DataRow(GeometrySource.Gadm, 3082)]
    [DataRow(GeometrySource.Overture, 1)]
    [DataRow(GeometrySource.Overture, 7)]
    [DataRow(GeometrySource.Overture, 3082)]
    public void UnpublishedBuild_DistinguishesOptionalSqliteErrorsFromCriticalMemory(GeometrySource source, int code)
    {
        using var fixture = new Fixture(source, "y");
        var before = fixture.ReadAllSource();
        var builder = new AdministrativeCandidatePreparation(source, (_, _) =>
            throw new SqliteException("controlled", code & 255, code));
        if (code == 1)
        {
            Assert.IsFalse(builder.BuildFile(fixture.Connection.DataSource, default), "Ordinary optional failure retains legacy output.");
        }
        else
        {
            Assert.ThrowsExactly<OutOfMemoryException>(() => builder.BuildFile(fixture.Connection.DataSource, default));
        }
        CollectionAssert.AreEqual(before, fixture.ReadAllSource(), "Failure never mutates authoritative output.");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        private readonly string _table;
        private readonly string _columns;
        public GeometrySource Source { get; }
        public SqliteConnection Connection { get; private set; }

        public Fixture(GeometrySource source, string index, bool legacy = false)
        {
            Source = source;
            Directory.CreateDirectory(_root);
            Connection = Open();
            _table = source == GeometrySource.Gadm ? "gadm_area" : "division_area";
            var fields = source == GeometrySource.Gadm
                ? "english_type TEXT, local_type TEXT, admin_level INTEGER NOT NULL"
                : $"subtype TEXT, class_name TEXT, {(legacy ? "" : "admin_level INTEGER,")} country TEXT, is_land INTEGER, is_territorial INTEGER";
            _columns = source == GeometrySource.Gadm
                ? "id,name,english_type,local_type,admin_level"
                : $"id,name,subtype,class_name,{(legacy ? "NULL AS admin_level" : "admin_level")},country,is_land,is_territorial";
            Execute($"""
                CREATE TABLE {_table}(id TEXT PRIMARY KEY,name TEXT NOT NULL,{fields},geom_wkb BLOB,
                    bbox_xmin REAL,bbox_ymin REAL,bbox_xmax REAL,bbox_ymax REAL);
                CREATE TABLE _meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
                INSERT INTO _meta VALUES('version','retained'),('release','retained'),('downloadedAt','2026-08-01T00:00:00Z');
                """);
            var values = source == GeometrySource.Gadm ? "'City',NULL,2" : $"'locality','land',{(legacy ? "" : "2,")}'GB',1,0";
            // Equal candidates use insertion order as the last legacy tie-break.
            Execute($"""
                INSERT INTO {_table} VALUES('z','First',{values},zeroblob(100),-1,-1,1,1);
                INSERT INTO {_table} VALUES('a','Second',{values},zeroblob(100),-1,-1,1,1);
                INSERT INTO {_table} VALUES('b','EarlierY',{values},zeroblob(100),-1,-2,1,1);
                INSERT INTO {_table} VALUES('c','Rounding',{values},NULL,-0.2,-0.2,0.2,0.2);
                """);
            if (index != "none")
            {
                Execute($"CREATE INDEX fixture_bbox_{index} ON {_table}(bbox_{index}min,bbox_{index}max)");
            }
        }

        private SqliteConnection Open()
        {
            var connection = new SqliteConnection($"Data Source={Path.Combine(_root, "test.db")};Pooling=false");
            connection.Open();
            return connection;
        }

        public void Reopen()
        {
            Connection.Dispose();
            Connection = Open();
        }

        private SqliteCommand CandidateCommand(double lat, double lon)
        {
            var command = Connection.CreateCommand();
            command.CommandText = $"SELECT {_columns},length(geom_wkb),bbox_xmin,bbox_ymin,bbox_xmax,bbox_ymax FROM {_table} WHERE bbox_xmax >= $lon AND bbox_xmin <= $lon AND bbox_ymax >= $lat AND bbox_ymin <= $lat";
            command.Parameters.AddWithValue("$lon", lon);
            command.Parameters.AddWithValue("$lat", lat);
            return command;
        }

        public bool Configure(double lat, double lon)
        {
            using var command = CandidateCommand(lat, lon);
            var original = command.CommandText;
            var configured = AdministrativeCandidateIndex.TryConfigure(command, Source, null, CancellationToken.None);
            if (!configured)
            {
                Assert.AreEqual(original, command.CommandText);
            }
            return configured;
        }

        public string[] Read(double lat, double lon, bool accelerated)
        {
            using var command = CandidateCommand(lat, lon);
            if (accelerated)
            {
                Assert.IsTrue(AdministrativeCandidateIndex.TryConfigure(command, Source, null, CancellationToken.None));
            }
            return ReadRows(command);
        }

        public string[] ReadAllSource()
        {
            using var command = Connection.CreateCommand();
            command.CommandText = $"SELECT rowid,*,hex(geom_wkb) FROM {_table} ORDER BY rowid";
            var rows = ReadRows(command).ToList();
            command.CommandText = $"SELECT key,value FROM _meta WHERE key <> '{AdministrativeCandidateIndex.VersionKey}' ORDER BY key";
            rows.AddRange(ReadRows(command));
            return rows.ToArray();
        }

        private static string[] ReadRows(SqliteCommand command)
        {
            using var reader = command.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read())
            {
                rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "<null>" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
            }
            return rows.ToArray();
        }

        public void Execute(string sql)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            Connection.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }
}
