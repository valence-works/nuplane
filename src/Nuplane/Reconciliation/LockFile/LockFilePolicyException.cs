namespace Nuplane.Reconciliation.LockFile;

internal sealed class LockFilePolicyException(string reasonCode, string message) :
    InvalidOperationException($"{reasonCode}: {message}")
{
    public string ReasonCode { get; } = reasonCode;
}
