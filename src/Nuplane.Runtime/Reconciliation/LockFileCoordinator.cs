using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Reconciliation.Models;

namespace Nuplane.Runtime.Reconciliation;


/// <summary>
/// Evaluates resolved packages against the lock file, enforcing or overriding
/// package versions and feeds based on the configured lock file mode.
/// </summary>
public sealed class LockFileCoordinator(LockFileStore store, LockFileOptions options) : ILockFileCoordinator
{
    private readonly LockFileStore store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly LockFileOptions options = options ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc />
    public async Task<LockFileEvaluationResult> EvaluateAsync(ResolvedPackage resolved, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        if (options.Mode == LockFileMode.Generate)
        {
            return new(true, "generate", resolved, null);
        }

        var lockFile = await store.ReadAsync(cancellationToken);
        var entry = lockFile?.Packages.FirstOrDefault(x => string.Equals(x.Id, resolved.Id, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            if (options.Mode == LockFileMode.Strict && options.RequireEntryInStrictMode)
            {
                return new(false, "strict-missing-entry", null, null);
            }

            return new(true, "enforce-no-entry", resolved, null);
        }

        if (options.Mode is LockFileMode.Enforce or LockFileMode.Strict)
        {
            var effective = new ResolvedPackage(
                resolved.Id,
                entry.Version,
                entry.Feed,
                resolved.InstallPath,
                resolved.InstalledAt,
                resolved.SourceName);

            return new(true, "enforced", effective, entry.Hash);
        }

        return new(true, "generate", resolved, null);
    }
}
