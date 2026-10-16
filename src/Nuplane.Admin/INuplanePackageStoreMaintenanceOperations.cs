namespace Nuplane.Admin;

/// <summary>Provides explicitly registered manual package-store maintenance operations.</summary>
/// <remarks>No operation schedules pruning. Configured labels identify trusted roots; previews are descriptive only.</remarks>
public interface INuplanePackageStoreMaintenanceOperations
{
    /// <summary>Observes enrollment requirements without declaring quiescence or changing package files.</summary>
    /// <param name="rootLabel">The configured root label.</param>
    /// <param name="cancellationToken">Cancels the observation.</param>
    /// <returns>The descriptive enrollment report.</returns>
    Task<PackageStoreEnrollmentPreviewResult> PreviewEnrollmentAsync(
        string rootLabel = "default", CancellationToken cancellationToken = default);

    /// <summary>Enrolls a configured complete roster while every store user remains quiescent.</summary>
    /// <param name="request">Explicit root, epoch, roster and optional source-bound recovery evidence.</param>
    /// <param name="cancellationToken">Cancels the cutover; an interrupted Incomplete ledger requires explicit recovery.</param>
    /// <returns>Verified Complete publication, or a refusal; quiescence must continue until Complete is verified.</returns>
    Task<PackageStoreEnrollmentResult> EnrollAsync(
        PackageStoreEnrollmentRequest request, CancellationToken cancellationToken = default);

    /// <summary>Resumes an exact persisted Incomplete enrollment under explicit quiescence.</summary>
    /// <param name="request">Exact observed incomplete epoch/roster and intended configured roster.</param>
    /// <param name="cancellationToken">Cancels recovery without granting ordinary access.</param>
    /// <returns>Verified Complete publication, or a refusal requiring continued quiescence.</returns>
    Task<PackageStoreEnrollmentResult> RecoverEnrollmentAsync(
        PackageStoreEnrollmentRequest request, CancellationToken cancellationToken = default);

    /// <summary>Migrates the exact current roster to configured member state locations under full quiescence.</summary>
    /// <param name="request">Exact observed root, epoch and current roster, complete intended roster and explicit retirements.</param>
    /// <param name="cancellationToken">Cancels migration; an interrupted Incomplete ledger requires explicit recovery.</param>
    /// <returns>Verified Complete publication at the next epoch, or a refusal requiring continued quiescence.</returns>
    Task<PackageStoreEnrollmentResult> MigrateAsync(
        PackageStoreEnrollmentRequest request, CancellationToken cancellationToken = default);

    /// <summary>Inspects membership, protection, live uses and package inventory without pruning.</summary>
    /// <param name="rootLabel">The configured root label.</param>
    /// <param name="cancellationToken">Cancels the inspection.</param>
    /// <returns>A detached read-only report with no retention policy, retaining all installs.</returns>
    Task<PackageStoreMaintenanceResult> InspectAsync(
        string rootLabel = "default", CancellationToken cancellationToken = default);

    /// <summary>Previews by default; explicitly confirmed Execute freshly replans and performs physical work.</summary>
    /// <param name="request">Configured label, retention and optional explicit execution confirmation.</param>
    /// <param name="cancellationToken">Cancels before admission or produces an honest partial report after execution begins.</param>
    /// <returns>The observed plan and actual physical results, preserving earlier outcomes on later failure.</returns>
    Task<PackageStoreMaintenanceResult> PruneAsync(
        PackageStorePruneRequest request, CancellationToken cancellationToken = default);
}
