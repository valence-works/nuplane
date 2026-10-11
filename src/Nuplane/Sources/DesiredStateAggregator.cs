using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Sources.Configuration;

namespace Nuplane.Sources;

/// <summary>
/// Aggregates desired package requests from multiple <see cref="IDesiredPackageSource"/> instances,
/// resolves duplicate package IDs via configured source precedence and deterministic tie-breaks,
/// and produces a deterministically ordered result. The selected request is preserved verbatim;
/// source admission and package resolution remain downstream responsibilities.
/// In the unscoped mode, per-source exceptions are captured in <see cref="DesiredAggregateResult.SourceErrors"/>
/// rather than propagated. The scoped mode propagates cancellation and typed package-store admission refusals.
/// </summary>
public sealed class DesiredStateAggregator : IScopedDesiredStateAggregator
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

        return await AggregateCoreAsync(
            sources,
            static (source, ct) => source.GetDesiredAsync(ct),
            propagateAdmissionRefusals: false,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<DesiredAggregateResult> AggregateAsync(
        IEnumerable<IDesiredPackageSource> sources,
        PackageStoreOperationOwner owner,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(owner);

        // Materialize and validate the complete participant set before the first source callback.
        // This also ensures the scoped path keeps the original source instances and identities.
        var sourceSnapshot = sources.ToArray();
        DesiredPackageSourceAccess.Validate(sourceSnapshot);

        return await AggregateCoreAsync(
            sourceSnapshot,
            (source, ct) => DesiredPackageSourceAccess.ReadAsync(source, owner, ct),
            propagateAdmissionRefusals: true,
            cancellationToken);
    }

    private async Task<DesiredAggregateResult> AggregateCoreAsync(
        IEnumerable<IDesiredPackageSource> sources,
        Func<IDesiredPackageSource, CancellationToken, Task<IReadOnlyList<PackageRequest>>> readSourceAsync,
        bool propagateAdmissionRefusals,
        CancellationToken cancellationToken)
    {
        if (propagateAdmissionRefusals)
            cancellationToken.ThrowIfCancellationRequested();

        var collected = new List<PackageRequest>();
        var sourceErrors = new Dictionary<string, Exception>(StringComparer.Ordinal);

        // Deterministic source ordering: stable sort by full type name then ToString()
        var orderedSources = sources
            .OrderBy(GetSourceTypeName, StringComparer.Ordinal)
            .ThenBy(s => s.ToString() ?? string.Empty, StringComparer.Ordinal)
            .ToList();

        foreach (var source in orderedSources)
        {
            if (propagateAdmissionRefusals)
                cancellationToken.ThrowIfCancellationRequested();

            var sourceName = GetSourceTypeName(source);
            IReadOnlyList<PackageRequest> sourceRequests;
            try
            {
                sourceRequests = await readSourceAsync(source, cancellationToken);
                if (propagateAdmissionRefusals)
                    cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (PackageStoreAdmissionException) when (propagateAdmissionRefusals)
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

    private static string GetSourceTypeName(IDesiredPackageSource source) => DesiredPackageSourceAccess.GetSourceName(source);
}
