using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using ImmichReverseGeo.Tests.WorkerProcessFixture;

namespace ImmichReverseGeo.Tests.WorkerMemorySoak;

internal sealed record WorkerMemorySoakOptions(WorkerMemorySoakConfiguration Configuration,
    string OutputRoot, string? InputDirectory, string? ThresholdProfilePath)
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    internal static string RepositoryRoot
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "package.json"))
                    && Directory.Exists(Path.Combine(directory.FullName, "openspec")))
                {
                    return directory.FullName;
                }
            }
            throw new InvalidOperationException("soak/repository-root-unavailable");
        }
    }

    internal static WorkerMemorySoakOptions Load(string? path)
    {
        var values = path is null ? new InputOptions() : Read<InputOptions>(path);
        string requiredRoot = Path.Combine(RepositoryRoot, "_out", "performance", "worker-memory-soak");
        string output = values.OutputRoot is null ? requiredRoot : Path.GetFullPath(values.OutputRoot, RepositoryRoot);
        if (output != requiredRoot && !output.StartsWith(requiredRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("soak/output-root-outside-owned-tree");
        }
        string? input = values.InputDirectory is null ? null : Path.GetFullPath(values.InputDirectory, RepositoryRoot);
        if (input is not null)
        {
            foreach (string name in new[] { "assets.db", "gadm.db", "source.gpkg" })
            {
                var file = new FileInfo(Path.Combine(input, name));
                if (!file.Exists || file.Length == 0 || file.LinkTarget is not null)
                {
                    throw new ArgumentException("soak/invalid-local-input-profile");
                }
            }
        }
        return new(WorkerMemorySoakConfiguration.Create(values.Seed, values.WarmupIterations,
            values.MeasuredIterations, new(values.ProcessingWeight, values.LookupWeight, values.CacheWeight),
            values.Cancellation, values.Failure), output, input,
            values.ThresholdProfile is null ? null : Path.GetFullPath(values.ThresholdProfile, RepositoryRoot));
    }

    internal static T Read<T>(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 32_768)
            {
                throw new ArgumentException("soak/configuration-too-large");
            }
            // Duplicate keys are ambiguous even when they happen to have equal values.
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || document.RootElement.EnumerateObject().Any(property => !names.Add(property.Name)))
            {
                throw new ArgumentException("soak/invalid-configuration-object");
            }
            return document.Deserialize<T>(Json) ?? throw new ArgumentException("soak/empty-configuration");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new ArgumentException("soak/configuration-unreadable-or-invalid");
        }
    }

    internal static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    internal static string HashRuntime()
    {
        string root = WorkerProcessFixtureLease.FixtureDirectory;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string path in Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(root, path).Replace('\\', '/') + "\n" + HashFile(path) + "\n"));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static string[] InputHashes(WorkerMemorySoakOptions options)
    {
        string[] data = options.InputDirectory is null
            ? [HashFile(Path.Combine(WorkerProcessFixtureLease.FixtureDirectory, "data", "gadm-che-tiny.json"))]
            : new[] { "assets.db", "gadm.db", "source.gpkg" }.Select(name => HashFile(Path.Combine(options.InputDirectory, name))).ToArray();
        return [.. data, HashFile(Path.Combine(AppContext.BaseDirectory, "data", "iso3166.json"))];
    }

    internal static string HashInputProfile(WorkerMemorySoakOptions options) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", InputHashes(options)))));

    private sealed record InputOptions
    {
        public int Seed { get; init; } = 6801;
        public int WarmupIterations { get; init; } = 5;
        public int MeasuredIterations { get; init; } = 10;
        public int ProcessingWeight { get; init; } = 3;
        public int LookupWeight { get; init; } = 1;
        public int CacheWeight { get; init; } = 1;
        public bool Cancellation { get; init; }
        public bool Failure { get; init; }
        public string? OutputRoot { get; init; }
        public string? InputDirectory { get; init; }
        public string? ThresholdProfile { get; init; }
    }
}
