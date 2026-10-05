using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Reconciliation.LockFile;
using Nuplane.Store.State;

namespace Nuplane.Reconciliation;

/// <summary>
/// Reads and writes package lock files in JSON format, providing serialization
/// and deserialization for <see cref="PackageLockFile"/> instances.
/// </summary>
public sealed class LockFileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _path;
    private readonly AtomicFileWriter _fileWriter;

    /// <summary>Initializes a lock-file store that publishes writes atomically.</summary>
    public LockFileStore(IOptions<LockFileOptions> options)
        : this(options, new AtomicFileWriter())
    {
    }

    internal LockFileStore(IOptions<LockFileOptions> options, IAtomicFileReplacer fileReplacer)
        : this(options, new AtomicFileWriter(fileReplacer))
    {
    }

    private LockFileStore(IOptions<LockFileOptions> options, AtomicFileWriter fileWriter)
    {
        _path = (options ?? throw new ArgumentNullException(nameof(options))).Value.Path;
        _fileWriter = fileWriter ?? throw new ArgumentNullException(nameof(fileWriter));
    }

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

        await _fileWriter.WriteAsync(
            _path,
            (stream, token) => JsonSerializer.SerializeAsync(stream, lockFile, JsonOptions, token).AsTask(),
            cancellationToken);
    }
}
