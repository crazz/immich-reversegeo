using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Tests.WorkerProcessFixture;
using System.Diagnostics;

namespace ImmichReverseGeo.Tests.WorkerJobs;

[TestClass]
[TestCategory("Change51")]
public sealed class CacheCandidateOwnershipTests
{
    [TestMethod]
    public void Lease_ProtectsLiveCandidateAndCleansItsExactFilesOnDispose()
    {
        using var root = new TemporaryDirectory();
        string candidate = Path.Combine(root.Path, "CHE.owner.tmp");
        var ownership = new CacheCandidateOwnership();

        using (ICacheCandidateLease lease = ownership.Acquire(candidate))
        {
            File.WriteAllText(candidate, "candidate");
            Assert.IsFalse(ownership.TryCleanupAbandoned(candidate));
            Assert.IsTrue(File.Exists(candidate));
            Assert.IsTrue(File.Exists(CacheCandidateOwnership.GetOwnerPath(candidate)));
        }

        Assert.IsFalse(File.Exists(candidate));
        Assert.IsFalse(File.Exists(CacheCandidateOwnership.GetOwnerPath(candidate)));
    }

    [TestMethod]
    public void Cleaner_RemovesOnlyRecognizedUnlockedCandidate()
    {
        using var root = new TemporaryDirectory();
        string candidate = Path.Combine(root.Path, "CHE.abandoned.tmp");
        var ownership = new CacheCandidateOwnership();
        File.WriteAllText(candidate, "abandoned");
        File.WriteAllBytes(
            CacheCandidateOwnership.GetOwnerPath(candidate),
            CacheCandidateOwnership.GetMarkerBytes());

        Assert.IsTrue(ownership.TryCleanupAbandoned(candidate));
        Assert.IsFalse(File.Exists(candidate));
        Assert.IsFalse(File.Exists(CacheCandidateOwnership.GetOwnerPath(candidate)));
    }

    [TestMethod]
    public void Cleaner_RetainsIncompleteOrUnsupportedOwnershipEvidence()
    {
        using var root = new TemporaryDirectory();
        string incomplete = Path.Combine(root.Path, "CHE.incomplete.tmp");
        File.WriteAllText(incomplete, "candidate");
        File.WriteAllText(CacheCandidateOwnership.GetOwnerPath(incomplete), string.Empty);
        var ownership = new CacheCandidateOwnership();

        Assert.IsFalse(ownership.TryCleanupAbandoned(incomplete));
        Assert.IsTrue(File.Exists(incomplete));

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            string unsupported = Path.Combine(root.Path, "CHE.unsupported.tmp");
            File.WriteAllText(unsupported, "candidate");
            File.WriteAllBytes(
                CacheCandidateOwnership.GetOwnerPath(unsupported),
                CacheCandidateOwnership.GetMarkerBytes());
            var unsupportedOwnership = new CacheCandidateOwnership(static _ => false);

            Assert.IsFalse(unsupportedOwnership.TryCleanupAbandoned(unsupported));
            Assert.IsTrue(File.Exists(unsupported));
        }
    }

    [TestMethod]
    public void AcquireFailure_NeverDeletesExistingOwnerSidecar()
    {
        using var root = new TemporaryDirectory();
        string candidate = Path.Combine(root.Path, "CHE.existing.tmp");
        string owner = CacheCandidateOwnership.GetOwnerPath(candidate);
        File.WriteAllText(owner, "existing-live-owner");

        Assert.ThrowsExactly<CacheCandidateOwnershipException>(() =>
            new CacheCandidateOwnership().Acquire(candidate));
        Assert.AreEqual("existing-live-owner", File.ReadAllText(owner));
    }

    [TestMethod]
    public void Cleaner_RetainsMarkerWhenCandidateDeletionFails()
    {
        using var root = new TemporaryDirectory();
        string candidate = Path.Combine(root.Path, "CHE.delete-failure.tmp");
        File.WriteAllText(candidate, "candidate");
        File.WriteAllBytes(
            CacheCandidateOwnership.GetOwnerPath(candidate),
            CacheCandidateOwnership.GetMarkerBytes());
        var deleteFailure = new CacheCandidateOwnership(
            static _ => true,
            tryDeleteCandidate: static _ => false);

        Assert.IsFalse(deleteFailure.TryCleanupAbandoned(candidate));
        Assert.IsTrue(File.Exists(candidate));
        Assert.IsTrue(File.Exists(CacheCandidateOwnership.GetOwnerPath(candidate)));
    }

    [TestMethod]
    public void Acquire_UnwindsCreatedSidecarWhenLockHookThrowsOutOfMemory()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        using var root = new TemporaryDirectory();
        string candidate = Path.Combine(root.Path, "CHE.oom.tmp");
        var ownership = new CacheCandidateOwnership(
            static _ => throw new OutOfMemoryException("injected"));

        Assert.ThrowsExactly<OutOfMemoryException>(() => ownership.Acquire(candidate));
        Assert.IsFalse(File.Exists(CacheCandidateOwnership.GetOwnerPath(candidate)));
    }

    [TestMethod]
    public async Task Cleaner_RetainsChildHeldCandidateThenCleansItAfterForcedExit()
    {
        using var root = new TemporaryDirectory();
        string candidate = Path.Combine(root.Path, "CHE.child.tmp");
        string ready = Path.Combine(root.Path, "child.ready");
        var start = new ProcessStartInfo
        {
            FileName = WorkerProcessFixtureLease.FixtureExecutable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--hold-cache-candidate");
        start.ArgumentList.Add(candidate);
        start.ArgumentList.Add(ready);
        Task readyObserved = ObserveCreatedFileAsync(ready);
        using Process child = Process.Start(start)
            ?? throw new AssertFailedException("The ownership fixture did not start.");
        Task<string> standardOutput = child.StandardOutput.ReadToEndAsync();
        Task<string> standardError = child.StandardError.ReadToEndAsync();
        FileStream? deleteGuard = null;
        try
        {
            await readyObserved;
            var ownership = new CacheCandidateOwnership();
            Assert.IsFalse(ownership.TryCleanupAbandoned(candidate));
            Assert.IsTrue(File.Exists(candidate));

            child.Kill(entireProcessTree: true);
            Assert.IsTrue(
                child.WaitForExit(TimeSpan.FromSeconds(10)),
                "The ownership fixture did not reach kernel-signaled process finality.");
            Assert.AreEqual(string.Empty, await standardOutput.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.AreEqual(string.Empty, await standardError.WaitAsync(TimeSpan.FromSeconds(10)));

            if (OperatingSystem.IsWindows())
            {
                AssertReleasedOwnerMarker(candidate);
                using (var independentHolder = new FileStream(
                    candidate,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.None))
                {
                    Assert.IsFalse(ownership.TryCleanupAbandoned(candidate));
                    Assert.IsTrue(File.Exists(candidate));
                    Assert.IsTrue(File.Exists(CacheCandidateOwnership.GetOwnerPath(candidate)));
                }

                deleteGuard = new FileStream(
                    candidate,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.Delete);
            }

            Assert.IsTrue(ownership.TryCleanupAbandoned(candidate));
            deleteGuard?.Dispose();
            deleteGuard = null;
            Assert.IsFalse(File.Exists(candidate));
            Assert.IsFalse(File.Exists(CacheCandidateOwnership.GetOwnerPath(candidate)));
        }
        finally
        {
            try
            {
                deleteGuard?.Dispose();
            }
            catch
            {
            }

            try
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }

            try
            {
                _ = child.WaitForExit(TimeSpan.FromSeconds(10));
            }
            catch
            {
            }

            try
            {
                _ = await standardOutput.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch
            {
            }

            try
            {
                _ = await standardError.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch
            {
            }
        }
    }

    private static void AssertReleasedOwnerMarker(string candidate)
    {
        byte[] expected = CacheCandidateOwnership.GetMarkerBytes();
        byte[] actual = new byte[expected.Length];
        long actualLength = -1;
        string accessStage = "open";
        try
        {
            using var owner = new FileStream(
                CacheCandidateOwnership.GetOwnerPath(candidate),
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            actualLength = owner.Length;
            if (actualLength == expected.Length)
            {
                accessStage = "read";
                owner.ReadExactly(actual);
            }
        }
        catch (Exception exception)
        {
            Assert.Fail(
                $"post-exit-owner-{accessStage}:{exception.GetType().Name}:0x{exception.HResult:X8}");
            return;
        }

        Assert.AreEqual(expected.Length, actualLength, "post-exit-owner-marker-length");
        CollectionAssert.AreEqual(expected, actual, "post-exit-owner-marker-content");
    }

    private static async Task ObserveCreatedFileAsync(string path)
    {
        if (File.Exists(path))
        {
            return;
        }

        string directory = Path.GetDirectoryName(path)!;
        string name = Path.GetFileName(path);
        var reached = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(directory, name)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };
        watcher.Created += (_, _) => reached.TrySetResult();
        watcher.Changed += (_, _) => reached.TrySetResult();
        if (File.Exists(path))
        {
            return;
        }

        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"immich-reversegeo-candidate-ownership-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }
}
