namespace Nuplane.Loading;

/// <summary>
/// Provides load-mode advice for a resolved package graph before Nuplane creates loading contexts.
/// </summary>
public interface IPackageLoadModeAdvisor
{
    /// <summary>
    /// Gets the stable advisor name used in diagnostics.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Evaluates load-mode advice for the supplied graph context.
    /// </summary>
    /// <param name="context">The graph context being evaluated.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>Advisor results for the graph.</returns>
    ValueTask<IReadOnlyList<LoadModeAdvisorResult>> EvaluateAsync(
        LoadModeAdvisorContext context,
        CancellationToken cancellationToken);
}
