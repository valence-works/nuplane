using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Sources.Configuration;

namespace Nuplane.Sources;

/// <summary>
/// Aggregates desired package requests from multiple <see cref="IDesiredPackageSource"/> instances,
/// resolves duplicate package IDs via configured source precedence and deterministic tie-breaks,
/// and produces a deterministically ordered result. The selected request is preserved verbatim;
/// source admission and package resolution remain downstream responsibilities.
/// Per-source exceptions are captured in <see cref="DesiredAggregateResult.SourceErrors"/> rather
/// than propagated, allowing healthy sources to continue contributing their requests.
/// </summary>
public sealed class DesiredStateAggregator : IDesiredStateAggregator
{
    private readonly DesiredStateOptions _options;

    /// <summary>
    /// Creates an aggregator with default desired-state source precedence.
    /// </summary>
    public DesiredStateAggregator()
        : this(Options.Create(new DesiredStateOptions()))
    {
    }

    /// <summary>
    /// Creates an aggregator using the configured desired-state source precedence.
    /// </summary>
    /// <param name="options">The desired-state options.</param>
    public DesiredStateAggregator(IOptions<DesiredStateOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    /// <inheritdoc />
    public async Task<DesiredAggregateResult> AggregateAsync(
        IEnumerable<IDesiredPackageSource> sources,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);

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
        //   1. Explicit source precedence (lower values win; unspecified is last)
        //   2. SourceName (alphabetical, case-insensitive)
        //   3. VersionRange (alphabetical, case-insensitive)
        //   4. FeedName, update policy, and ordinal casing tie-breaks
        var deduped = collected
            .GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g
                .OrderBy(r => GetPriority(r.SourceName))
                .ThenBy(r => r.SourceName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.VersionRange, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.FeedName is null ? 0 : 1)
                .ThenBy(r => r.FeedName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.UpdatePolicy)
                .ThenBy(r => r.Id, StringComparer.Ordinal)
                .ThenBy(r => r.SourceName, StringComparer.Ordinal)
                .ThenBy(r => r.VersionRange, StringComparer.Ordinal)
                .ThenBy(r => r.FeedName ?? string.Empty, StringComparer.Ordinal)
                .First())
            .OrderBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.SourceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.FeedName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new(deduped, sourceErrors);
    }

    private int GetPriority(string sourceName) =>
        string.IsNullOrWhiteSpace(sourceName) ? int.MaxValue : _options.GetPriority(sourceName);

    private static string GetSourceTypeName(IDesiredPackageSource source) => source.GetType().FullName ?? source.GetType().Name;
}
