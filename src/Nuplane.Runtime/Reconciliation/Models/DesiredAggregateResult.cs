using Nuplane.Abstractions;

namespace Nuplane.Runtime.Reconciliation.Models;

/// <summary>
/// The result of aggregating desired package requests from multiple sources, containing
/// successfully collected requests alongside any per-source errors that occurred.
/// </summary>
/// <param name="Requests">The deterministically ordered list of successfully aggregated package requests.</param>
/// <param name="SourceErrors">
/// A dictionary mapping source type names to the exception thrown by that source, if any.
/// Sources that completed successfully do not appear in this dictionary.
/// </param>
public sealed record DesiredAggregateResult(
    IReadOnlyList<PackageRequest> Requests,
    IReadOnlyDictionary<string, Exception> SourceErrors);
