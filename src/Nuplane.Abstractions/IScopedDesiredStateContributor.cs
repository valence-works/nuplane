using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Abstractions;

/// <summary>Opts a desired-state contributor into explicit coordinated package reads.</summary>
public interface IScopedDesiredStateContributor : IDesiredStateContributor
{
    /// <summary>Contributes under the existing root operation before any package IO.</summary>
    /// <param name="context">The current resolved package and desired-state context.</param>
    /// <param name="borrow">The caller-owned live root borrow.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The contribution.</returns>
    Task<DesiredStateContribution> ContributeAsync(DesiredStateContributionContext context,
        PackageStoreOperationBorrow borrow, CancellationToken cancellationToken);
}
