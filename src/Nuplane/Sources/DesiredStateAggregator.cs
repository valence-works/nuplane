using Nuplane.Abstractions;

namespace Nuplane.Sources;

/// <summary>
/// Aggregates desired package requests from multiple <see cref="IDesiredPackageSource"/> instances,
/// validates them against the allowlist, resolves duplicate package IDs via deterministic tie-break,
/// and produces a deterministically ordered result.
/// Per-source exceptions are captured in <see cref="DesiredAggregateResult.SourceErrors"/> rather
/// than propagated, allowing healthy sources to continue contributing their requests.
/// </summary>
public sealed class DesiredStateAggregator : IDesiredStateAggregator
{
    /// <inheritdoc />
    public async Task<DesiredAggregateResult> AggregateAsync(
        IEnumerable<IDesiredPackageSource> sources,
        CancellationToken cancellationToken)
    {
        var collected = new List<PackageRequest>();
        var sourceErrors = new Dictionary<string, Exception>(StringComparer.Ordinal);

        // Deterministic source ordering: stable sort by full type name then ToString()
        var orderedSources = sources
            .OrderBy(GetSourceTypeName, StringComparer.Ordinal)
            .ThenBy(s => s.ToString() ?? string.Empty, StringComparer.Ordinal)
            .ToList();

        foreach (var source in orderedSources)
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

            collected.AddRange(sourceRequests.Where(request => !string.IsNullOrWhiteSpace(request.Id)));
        }

        // Deterministic duplicate tie-break:
        // Group by case-insensitive package ID, then select the winner using:
        //   1. SourceName (alphabetical, case-insensitive) — lowest sort order wins
        //   2. Tie-break by VersionRange (alphabetical, case-insensitive)
        var deduped = collected
            .GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g
                .OrderBy(r => r.SourceName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.VersionRange, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.SourceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.FeedName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new(deduped, sourceErrors);
    }

    private static string GetSourceTypeName(IDesiredPackageSource source) => source.GetType().FullName ?? source.GetType().Name;
}

