using System;
using System.IO;

namespace ImmichReverseGeo.Web.Composition;

internal enum CompositionEnvironment
{
    Development,
    Production
}

/// <summary>
/// Immutable, non-secret inputs shared by the Web and future worker composition roots.
/// </summary>
internal sealed class ApplicationCompositionContext
{
    private ApplicationCompositionContext(
        CompositionEnvironment environment,
        string contentRoot,
        string dataDirectory,
        string configDirectory,
        string bundledDataDirectory)
    {
        Environment = environment;
        ContentRoot = contentRoot;
        DataDirectory = dataDirectory;
        ConfigDirectory = configDirectory;
        BundledDataDirectory = bundledDataDirectory;
    }

    internal CompositionEnvironment Environment { get; }

    internal string ContentRoot { get; }

    internal string DataDirectory { get; }

    internal string ConfigDirectory { get; }

    internal string BundledDataDirectory { get; }

    public override string ToString()
    {
        return $"ApplicationCompositionContext ({Environment})";
    }

    internal static ApplicationCompositionContext Create(
        CompositionEnvironment environment,
        string contentRoot,
        string? dataDirectoryOverride,
        string? configDirectoryOverride)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);

        var (defaultDataDirectory, defaultConfigDirectory, bundledDataDirectory) = environment switch
        {
            CompositionEnvironment.Development => (
                Path.Combine(contentRoot, "localdata"),
                Path.Combine(contentRoot, "localdata"),
                Path.Combine(contentRoot, "bundled-data")),
            CompositionEnvironment.Production => ("/data", "/config", "/app/bundled-data"),
            _ => throw new ArgumentOutOfRangeException(nameof(environment), environment, null)
        };

        return new ApplicationCompositionContext(
            environment,
            contentRoot,
            dataDirectoryOverride ?? defaultDataDirectory,
            configDirectoryOverride ?? defaultConfigDirectory,
            bundledDataDirectory);
    }
}
