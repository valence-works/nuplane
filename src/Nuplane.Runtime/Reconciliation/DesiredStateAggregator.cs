using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;

namespace Nuplane.Runtime.Reconciliation;

public sealed class DesiredStateAggregator
{
    public async Task<IReadOnlyList<PackageRequest>> AggregateAsync(
        IEnumerable<IDesiredPackageSource> sources,
        SourceTrustOptions trustOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(trustOptions);

        var collected = new List<PackageRequest>();
        foreach (var source in sources.OrderBy(GetSourceTypeName, StringComparer.Ordinal))
        {
            var sourceRequests = await source.GetDesiredAsync(cancellationToken);
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

        return collected
            .OrderBy(request => request.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(request => request.SourceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(request => request.FeedName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string GetSourceTypeName(IDesiredPackageSource source) => source.GetType().FullName ?? source.GetType().Name;
}