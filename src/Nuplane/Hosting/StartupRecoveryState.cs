using Nuplane.Operational;

namespace Nuplane.Hosting;

/// <summary>
/// Tracks startup last-known-good recovery status for operational-state reporting.
/// </summary>
public sealed class StartupRecoveryState
{
    private readonly object _gate = new();
    private OperationalStateContribution _contribution = new("startup-recovery", []);

    /// <summary>
    /// Records that startup recovered from last-known-good packages.
    /// </summary>
    public void MarkRecovered(string correlationId, int packageCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        lock (_gate)
        {
            _contribution = new(
                "startup-recovery",
                [$"startup-lkg-recovery-active:{packageCount}"]);
        }
    }

    /// <summary>
    /// Records that startup last-known-good recovery failed.
    /// </summary>
    public void MarkFailed(string correlationId, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        lock (_gate)
        {
            _contribution = new(
                "startup-recovery",
                [$"startup-lkg-recovery-failed:{Normalize(reason)}"]);
        }
    }

    /// <summary>
    /// Clears startup recovery degradation.
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            _contribution = new("startup-recovery", []);
        }
    }

    /// <summary>
    /// Gets the current operational-state contribution.
    /// </summary>
    public OperationalStateContribution GetContribution()
    {
        lock (_gate)
        {
            return _contribution;
        }
    }

    private static string Normalize(string reason) =>
        reason.Trim()
            .Replace(' ', '-')
            .Replace(':', '-')
            .ToLowerInvariant();
}
