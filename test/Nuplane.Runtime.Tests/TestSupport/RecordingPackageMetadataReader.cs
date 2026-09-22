using Nuplane.Metadata;

namespace Nuplane.Runtime.Tests.TestSupport;

/// <summary>
/// The real <see cref="NuplanePackageMetadataReader"/>, counting the reads it performs, so a test
/// can assert how often a cycle touches each package's <c>nuplane.json</c> rather than only what it
/// concluded from it.
/// </summary>
internal sealed class RecordingPackageMetadataReader : IPackageMetadataReader
{
    private readonly NuplanePackageMetadataReader _reader = new();

    /// <summary>The <c>id@version</c> of every read performed, in order, including repeats.</summary>
    public List<string> Reads { get; } = [];

    public NuplanePackageMetadataReadResult Read(string packageId, string version, string installPath)
    {
        Reads.Add($"{packageId}@{version}");
        return _reader.Read(packageId, version, installPath);
    }

    /// <summary>How many times <paramref name="packageId"/>'s metadata was read.</summary>
    public int CountFor(string packageId) =>
        Reads.Count(read => read.StartsWith($"{packageId}@", StringComparison.Ordinal));
}
