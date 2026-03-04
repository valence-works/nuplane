using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Reconciliation.Models;

namespace Nuplane.Runtime.Reconciliation;

/// <summary>
/// Aggregates desired package requests from multiple sources, enforcing allowlist rules
/// and producing a deterministically ordered list of requests.
/// </summary>
public interface IDesiredStateAggregator
{
    /// <summary>
    /// Aggregates desired package requests from the specified sources.
    /// </summary>
    /// <param name="sources">The desired-state sources to read from.</param>
    /// <param name="trustOptions">The source trust options governing allowlisting.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// A <see cref="DesiredAggregateResult"/> containing the deterministically ordered aggregated
    /// package requests and any per-source errors that occurred during reading.
    /// </returns>
    Task<DesiredAggregateResult> AggregateAsync(
        IEnumerable<IDesiredPackageSource> sources,
        SourceTrustOptions trustOptions,
        CancellationToken cancellationToken);
}
