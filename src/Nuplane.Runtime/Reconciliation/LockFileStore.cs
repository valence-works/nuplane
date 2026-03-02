using System.Text.Json;
using System.Text.Json.Serialization;
using Nuplane.Abstractions;

namespace Nuplane.Runtime.Reconciliation;

public sealed class LockFileStore(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string path = path ?? throw new ArgumentNullException(nameof(path));

    public async Task<PackageLockFile?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<PackageLockFile>(stream, JsonOptions, cancellationToken);
    }

    public async Task WriteAsync(PackageLockFile lockFile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lockFile);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, lockFile, JsonOptions, cancellationToken);
    }
}
