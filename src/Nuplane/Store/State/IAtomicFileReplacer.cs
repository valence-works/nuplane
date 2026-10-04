namespace Nuplane.Store.State;

internal interface IAtomicFileReplacer
{
    Task ReplaceAsync(string temporaryFilePath, string destinationFilePath, CancellationToken cancellationToken);
}
