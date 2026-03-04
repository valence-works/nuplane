using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Nuplane.Runtime.Tests.TestSupport;

/// <summary>
/// Builds minimal <c>.nupkg</c> files (ZIP archives with a <c>.nuspec</c>) for testing.
/// Produces files that are valid enough for directory enumeration and filename parsing,
/// without requiring the full NuGet SDK.
/// </summary>
public sealed class NupkgTestBuilder
{
    private string _packageId = "TestPackage";
    private string _version = "1.0.0";

    public NupkgTestBuilder WithPackageId(string packageId)
    {
        _packageId = packageId ?? throw new ArgumentNullException(nameof(packageId));
        return this;
    }

    public NupkgTestBuilder WithVersion(string version)
    {
        _version = version ?? throw new ArgumentNullException(nameof(version));
        return this;
    }

    /// <summary>
    /// Returns the conventional <c>.nupkg</c> filename: <c>{id}.{version}.nupkg</c>.
    /// </summary>
    public string FileName => $"{_packageId}.{_version}.nupkg";

    /// <summary>
    /// Builds the <c>.nupkg</c> as an in-memory byte array.
    /// </summary>
    public byte[] Build()
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var nuspecEntry = archive.CreateEntry($"{_packageId}.nuspec");
            using var writer = new StreamWriter(nuspecEntry.Open(), Encoding.UTF8);
            writer.Write($"""
                <?xml version="1.0" encoding="utf-8"?>
                <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
                  <metadata>
                    <id>{_packageId}</id>
                    <version>{_version}</version>
                    <authors>test</authors>
                    <description>Test package</description>
                  </metadata>
                </package>
                """);
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Builds and writes the <c>.nupkg</c> to the specified directory.
    /// Returns the full path of the written file.
    /// </summary>
    public string BuildTo(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        Directory.CreateDirectory(directoryPath);
        var filePath = Path.Combine(directoryPath, FileName);
        File.WriteAllBytes(filePath, Build());
        return filePath;
    }

    /// <summary>
    /// Builds and writes a partially-written (truncated) <c>.nupkg</c> to simulate an in-progress write.
    /// Returns the full path.
    /// </summary>
    public string BuildPartialTo(string directoryPath, int truncateBytes = 10)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        Directory.CreateDirectory(directoryPath);

        var fullContent = Build();
        var partialLength = Math.Max(1, fullContent.Length - truncateBytes);
        var partial = new byte[partialLength];
        Array.Copy(fullContent, partial, partialLength);

        var filePath = Path.Combine(directoryPath, FileName);
        File.WriteAllBytes(filePath, partial);
        return filePath;
    }

    /// <summary>
    /// Creates a new builder for convenience.
    /// </summary>
    public static NupkgTestBuilder Create(string packageId, string version) =>
        new NupkgTestBuilder().WithPackageId(packageId).WithVersion(version);
}
