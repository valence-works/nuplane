namespace Nuplane.Store.State;

internal interface IAtomicFileReplacer
{
    Task ReplaceAsync(string temporaryFilePath, string destinationFilePath, CancellationToken cancellationToken);
}

internal sealed class AtomicFileReplacer : IAtomicFileReplacer
{
    public Task ReplaceAsync(string temporaryFilePath, string destinationFilePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (File.Exists(destinationFilePath))
        {
            File.Replace(temporaryFilePath, destinationFilePath, destinationBackupFileName: null);
        }
        else
        {
            File.Move(temporaryFilePath, destinationFilePath);
        }

        return Task.CompletedTask;
    }
}
