using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Store.State;

/// <summary>Opts failure persistence into an already-held package-store operation.</summary>
public interface IScopedFailureRecorder : IFailureRecorder
{
    /// <summary>Persists a failure under the caller's existing owner.</summary>
    /// <param name="packageId">The failed package or source.</param>
    /// <param name="stage">The failed stage.</param>
    /// <param name="message">The failure details.</param>
    /// <param name="correlationId">The reconciliation correlation identifier.</param>
    /// <param name="borrow">A counted borrow owned by the caller for this awaited operation.</param>
    /// <param name="cancellationToken">A token to cancel persistence.</param>
    Task RecordAsync(string packageId, string stage, string message, string correlationId,
        PackageStoreOperationBorrow borrow, CancellationToken cancellationToken);
}
