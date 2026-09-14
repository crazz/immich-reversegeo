using ImmichReverseGeo.Core.WorkerJobs;
using Microsoft.Data.Sqlite;

namespace ImmichReverseGeo.Tests.WorkerJobs;

[TestClass]
public sealed class AtomicCacheFilePublisherTests
{
    [TestMethod]
    [TestCategory("Change51")]
    public void Publish_ReplacesSiblingFileAndRemovesCandidateOnActualFileSystem()
    {
        using var directory = new TemporaryDirectory();
        string final = Path.Combine(directory.Path, "CHE.db");
        string candidate = Path.Combine(directory.Path, "CHE.operation.tmp");
        File.WriteAllText(final, "old-cache");
        File.WriteAllText(candidate, "new-cache");

        new AtomicCacheFilePublisher().Publish(candidate, final);

        Assert.AreEqual("new-cache", File.ReadAllText(final));
        Assert.IsFalse(File.Exists(candidate));
    }

    [TestMethod]
    [TestCategory("Change51")]
    public void Publish_NativeFailureRetainsExistingFinal()
    {
        using var directory = new TemporaryDirectory();
        string final = Path.Combine(directory.Path, "CHE.db");
        string missingCandidate = Path.Combine(directory.Path, "CHE.missing.tmp");
        File.WriteAllText(final, "old-cache");

        Assert.ThrowsExactly<CachePublicationException>(() =>
            new AtomicCacheFilePublisher().Publish(missingCandidate, final));

        Assert.AreEqual("old-cache", File.ReadAllText(final));
    }

    [TestMethod]
    [TestCategory("Change51")]
    public void Publish_RejectsNonSiblingPathsBeforeChangingEitherFile()
    {
        using var first = new TemporaryDirectory();
        using var second = new TemporaryDirectory();
        string final = Path.Combine(first.Path, "CHE.db");
        string candidate = Path.Combine(second.Path, "CHE.operation.tmp");
        File.WriteAllText(final, "old-cache");
        File.WriteAllText(candidate, "new-cache");

        Assert.ThrowsExactly<ArgumentException>(() =>
            new AtomicCacheFilePublisher().Publish(candidate, final));

        Assert.AreEqual("old-cache", File.ReadAllText(final));
        Assert.AreEqual("new-cache", File.ReadAllText(candidate));
    }

    [TestMethod]
    [TestCategory("Change51")]
    public void Publish_WithLiveSqliteReaderExposesOnlyValidOldOrNewCacheState()
    {
        using var directory = new TemporaryDirectory();
        string final = Path.Combine(directory.Path, "CHE.db");
        string candidate = Path.Combine(directory.Path, "CHE.operation.tmp");
        WriteCache(final, "old-cache");
        WriteCache(candidate, "new-cache");
        using var oldReader = OpenReadOnly(final);
        Assert.AreEqual("old-cache", ReadVersion(oldReader));

        bool published;
        try
        {
            new AtomicCacheFilePublisher().Publish(candidate, final);
            published = true;
        }
        catch (CachePublicationException)
        {
            published = false;
        }

        Assert.AreEqual("old-cache", ReadVersion(oldReader));
        using (SqliteConnection currentReader = OpenReadOnly(final))
        {
            Assert.AreEqual(
                published ? "new-cache" : "old-cache",
                ReadVersion(currentReader));
        }

        if (published)
        {
            Assert.IsFalse(File.Exists(candidate));
            return;
        }

        Assert.IsTrue(File.Exists(candidate), "a refused publication retains its candidate");
        oldReader.Close();
        new AtomicCacheFilePublisher().Publish(candidate, final);
        using SqliteConnection retriedReader = OpenReadOnly(final);
        Assert.AreEqual("new-cache", ReadVersion(retriedReader));
        Assert.IsFalse(File.Exists(candidate));
    }

    private static void WriteCache(string path, string version)
    {
        using var connection = new SqliteConnection(
            $"Data Source={path};Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO _meta VALUES ('version', $version);
            """;
        command.Parameters.AddWithValue("$version", version);
        command.ExecuteNonQuery();
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ConnectionString);
        connection.Open();
        return connection;
    }

    private static string ReadVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM _meta WHERE key = 'version'";
        return (string)command.ExecuteScalar()!;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"immich-reversegeo-change51-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
