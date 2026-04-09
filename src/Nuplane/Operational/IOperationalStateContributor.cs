namespace Nuplane.Operational;

/// <summary>
/// Contributes module-owned degraded-state information to the core operational-state surface.
/// </summary>
public interface IOperationalStateContributor
{
    /// <summary>
    /// Produces the contributor's current operational-state contribution.
    /// </summary>
    Task<OperationalStateContribution> ContributeAsync(CancellationToken cancellationToken);
}

