using System.Text.Json;
using System.Text.Json.Serialization;
using Nuplane.Abstractions;

namespace Nuplane.Runtime.Reconciliation;

/// <summary>
/// Reads and writes package lock files in JSON format, providing serialization
/// and deserialization for <see cref="PackageLockFile"/> instances.
/// </summary>
public sealed class LockFileStore(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _path = path ?? throw new ArgumentNullException(nameof(path));

    /// <summary>
    /// Reads the lock file from disk, returning <see langword="null"/> if the file does not exist.
    /// </summary>
    public async Task<PackageLockFile?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<PackageLockFile>(stream, JsonOptions, cancellationToken);
    }

    /// <summary>
    /// Writes the lock file to disk, creating the directory if necessary.
    /// </summary>
    public async Task WriteAsync(PackageLockFile lockFile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lockFile);

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(_path);
        await JsonSerializer.SerializeAsync(stream, lockFile, JsonOptions, cancellationToken);
    }
}
