namespace Nuplane.Store.State;

internal sealed class AtomicFileWriter
{
    private readonly IAtomicFileReplacer _fileReplacer;

    internal AtomicFileWriter()
        : this(new AtomicFileReplacer())
    {
    }

    internal AtomicFileWriter(IAtomicFileReplacer fileReplacer)
    {
        _fileReplacer = fileReplacer ?? throw new ArgumentNullException(nameof(fileReplacer));
    }

    internal async Task WriteAsync(
        string destinationFilePath,
        Func<Stream, CancellationToken, Task> writeAsync,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFilePath);
        ArgumentNullException.ThrowIfNull(writeAsync);
        cancellationToken.ThrowIfCancellationRequested();

        var destinationPath = Path.GetFullPath(destinationFilePath);
        var directory = Path.GetDirectoryName(destinationPath)!;
        Directory.CreateDirectory(directory);

        var operationId = Guid.NewGuid().ToString("N");
        var fileName = Path.GetFileName(destinationPath);
        var temporaryPath = Path.Combine(directory, $"{fileName}.{operationId}.tmp");
        var backupPath = Path.Combine(directory, $"{fileName}.{operationId}.bak");
        var replacementAttempted = false;

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous))
            {
                await writeAsync(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            replacementAttempted = true;
            await _fileReplacer.ReplaceAsync(temporaryPath, destinationPath, backupPath, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryRestoreBackup(destinationPath, backupPath);
            throw;
        }
        finally
        {
            if (!replacementAttempted || File.Exists(destinationPath))
            {
                TryDeleteRecoveryFile(temporaryPath);
                TryDeleteRecoveryFile(backupPath);
            }
        }
    }

    private static void TryRestoreBackup(string destinationPath, string backupPath)
    {
        if (File.Exists(destinationPath) || !File.Exists(backupPath))
        {
            return;
        }

        try
        {
            File.Move(backupPath, destinationPath);
        }
        catch (IOException)
        {
            // Preserve the backup under its unique recovery name when restoration is blocked.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the backup under its unique recovery name when restoration is blocked.
        }
    }

    private static void TryDeleteRecoveryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Cleanup must not hide the original write or replacement failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Cleanup must not hide the original write or replacement failure.
        }
    }
}
