namespace Nuplane.Runtime.Tests.TestSupport;

/// <summary>
/// Creates a unique temporary directory that is automatically deleted when disposed.
/// Use in tests that require isolated directory state (e.g., file-system watcher, directory source).
/// </summary>
public sealed class TempDirectory : IDisposable
{
    public string Path { get; }

    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "nuplane-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    /// <summary>
    /// Creates a subdirectory inside this temp directory.
    /// </summary>
    public string CreateSubdirectory(string name)
    {
        var sub = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(sub);
        return sub;
    }

    /// <summary>
    /// Writes a file inside this temp directory.
    /// </summary>
    public string WriteFile(string fileName, byte[] content)
    {
        var filePath = System.IO.Path.Combine(Path, fileName);
        File.WriteAllBytes(filePath, content);
        return filePath;
    }

    /// <summary>
    /// Writes a file inside this temp directory with text content.
    /// </summary>
    public string WriteFile(string fileName, string content)
    {
        var filePath = System.IO.Path.Combine(Path, fileName);
        File.WriteAllText(filePath, content);
        return filePath;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup; temp directories are cleaned by OS eventually.
        }
    }
}
