namespace Nuplane.Reconciliation;

/// <summary>Contains package-level Loading failures for a deferred graph handoff.</summary>
public sealed class LeasedPackageGraphLoadingResult
{
    /// <summary>Creates a result containing the failures observed during Loading.</summary>
    /// <param name="failures">The failed package identifiers and diagnostics.</param>
    public LeasedPackageGraphLoadingResult(IReadOnlyList<LeasedPackageGraphLoadingFailure> failures)
    {
        Failures = Array.AsReadOnly((failures ?? throw new ArgumentNullException(nameof(failures))).ToArray());
    }

    /// <summary>Gets the failures observed during Loading.</summary>
    public IReadOnlyList<LeasedPackageGraphLoadingFailure> Failures { get; }
}
