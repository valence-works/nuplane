using System.Text.Json;

namespace Nuplane.Runtime.Tests.TestSupport;

/// <summary>
/// Writes a temporary manifest JSON file in the shape read by <c>DesiredManifestPackageSource</c>
/// (<c>{ SchemaVersion, GeneratedAtUtc, Packages }</c>) and deletes it when disposed.
/// </summary>
public sealed class TempManifestFile : IDisposable
{
    public string Path { get; }

    public TempManifestFile(object packages)
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"nuplane-manifest-{Guid.NewGuid():N}.json");

        File.WriteAllText(Path, JsonSerializer.Serialize(new
        {
            SchemaVersion = "1.0",
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Packages = packages
        }));
    }

    public void Dispose()
    {
        try { File.Delete(Path); }
        catch
        {
            // Best-effort cleanup; temp files are cleaned by OS eventually.
        }
    }
}
