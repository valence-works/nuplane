using Nuplane.Abstractions;

namespace Nuplane.Runtime.Reconciliation;

/// <summary>
/// Represents a dry-run reconciliation plan showing projected changes without mutating state.
/// </summary>
/// <param name="ChangeSet">The projected package change set.</param>
/// <param name="MutatedState">Whether the plan would mutate state (always <see langword="false"/> for dry runs).</param>
public sealed record DryRunPlan(PackageChangeSet ChangeSet, bool MutatedState);

