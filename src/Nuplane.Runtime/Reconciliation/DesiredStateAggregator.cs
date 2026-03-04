using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Reconciliation.Models;

namespace Nuplane.Runtime.Reconciliation;

/// <summary>
/// Aggregates desired package requests from multiple <see cref="IDesiredPackageSource"/> instances,
/// validates them against the allowlist, and produces a deterministically ordered result.
/// Per-source exceptions are captured in <see cref="DesiredAggregateResult.SourceErrors"/> rather
/// than propagated, allowing healthy sources to continue contributing their requests.
/// </summary>
public sealed class DesiredStateAggregator : IDesiredStateAggregator
{
    /// <inheritdoc />
    public async Task<DesiredAggregateResult> AggregateAsync(
        IEnumerable<IDesiredPackageSource> sources,
        SourceTrustOptions trustOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(trustOptions);

        var collected = new List<PackageRequest>();
        var sourceErrors = new Dictionary<string, Exception>(StringComparer.Ordinal);

        foreach (var source in sources.OrderBy(GetSourceTypeName, StringComparer.Ordinal))
        {
            var sourceName = GetSourceTypeName(source);
            IReadOnlyList<PackageRequest> sourceRequests;
            try
            {
                sourceRequests = await source.GetDesiredAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                sourceErrors[sourceName] = ex;
                continue;
            }

            foreach (var request in sourceRequests)
            {
                if (string.IsNullOrWhiteSpace(request.Id))
                {
                    continue;
                }

                if (trustOptions.RejectUnallowlistedPackages && !trustOptions.IsPackageAllowed(request.Id))
                {
                    throw new InvalidOperationException($"Package '{request.Id}' is not allowlisted.");
                }

                collected.Add(request);
            }
        }

        var ordered = collected
            .OrderBy(request => request.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(request => request.SourceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(request => request.FeedName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new DesiredAggregateResult(ordered, sourceErrors);
    }

    private static string GetSourceTypeName(IDesiredPackageSource source) => source.GetType().FullName ?? source.GetType().Name;
}