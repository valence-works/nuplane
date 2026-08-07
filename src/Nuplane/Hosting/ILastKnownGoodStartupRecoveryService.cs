namespace Nuplane.Hosting;

internal interface ILastKnownGoodStartupRecoveryService
{
    Task<LastKnownGoodStartupRecoveryResult> TryRecoverAsync(
        string correlationId,
        CancellationToken cancellationToken);
}
