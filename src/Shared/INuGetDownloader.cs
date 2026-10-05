namespace DotNetLab;

public interface INuGetDownloader
{
    Task<NuGetResults> DownloadAsync(
        Set<NuGetDependency> dependencies,
        string targetFramework,
        bool loadForExecution,
        NuGetFolder? folder = null);
}

/// <summary>
/// Takes files directly in <paramref name="Path"/> (e.g., <c>runtimes/linux-x64/lib/net10.0</c>) with the given extension
/// instead of the package's <c>lib</c> assets. <see cref="RefAssembly.Name"/> is then the file name without the extension.
/// </summary>
public readonly record struct NuGetFolder(string Path, string Extension);

public readonly record struct NuGetDependency
{
    public required Comparable<string, Comparers.String.OrdinalIgnoreCase> PackageId { get; init; }
    public required Comparable<string, Comparers.String.OrdinalIgnoreCase> VersionRange { get; init; }

    public override string ToString()
    {
        return $"{PackageId}@{VersionRange}";
    }
}

public readonly struct NuGetResults
{
    public required IReadOnlyDictionary<NuGetDependency, IReadOnlyList<string>> Errors { get; init; }
    public required ImmutableArray<RefAssembly> Assemblies { get; init; }
}
