namespace Nuplane.Loading;

/// <summary>
/// Decides whether Nuplane may activate a resolved package graph. Gates are consulted after the
/// load mode for the graph has been selected and before any assembly load context for the graph
/// exists, so a blocked graph never loads a single assembly.
/// </summary>
/// <remarks>
/// <para>
/// Every registered gate is consulted, sequentially, in registration order; gates are never
/// evaluated in parallel. A <see cref="PackageActivationGateResult.Block(string)"/> from any gate
/// blocks the whole graph — roots and dependencies alike — and every blocking reason is collected
/// into a single ordinary load failure. Other graphs in the same load call are unaffected.
/// </para>
/// <para>
/// Gate evaluation is fail-closed. A gate that throws anything other than an
/// <see cref="OperationCanceledException"/> that honours the caller's cancellation token blocks the
/// graph, and the failure names the gate type; a gate fault is never treated as an allow. A
/// cancellation that honours the caller's token propagates as cancellation and is not reported as a
/// block. Gates are only consulted when the graph is genuinely about to be loaded: an already-loaded
/// graph generation reuses the decision its gates already allowed.
/// </para>
/// <para>
/// A blocked graph is not cached as loaded, so a later load attempt — the next reconcile, or the
/// next process start — re-evaluates every gate and loads the graph once the gates allow it.
/// </para>
/// <para>
/// Implementations must be deterministic, secret-safe, and must not load or execute any code from
/// the packages they are evaluating; read package metadata from the install path instead.
/// </para>
/// </remarks>
public interface IPackageActivationGate
{
    /// <summary>
    /// Evaluates whether the supplied package graph may be activated.
    /// </summary>
    /// <param name="context">The package graph that is about to be activated.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// <see cref="PackageActivationGateResult.Allow"/> to let the graph load, or
    /// <see cref="PackageActivationGateResult.Block(string)"/> with the reason activation was refused.
    /// </returns>
    ValueTask<PackageActivationGateResult> EvaluateAsync(
        PackageActivationContext context,
        CancellationToken cancellationToken);
}
