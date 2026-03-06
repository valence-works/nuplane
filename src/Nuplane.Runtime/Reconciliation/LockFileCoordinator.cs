using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Reconciliation.Models;

namespace Nuplane.Runtime.Reconciliation;


/// <summary>
/// Evaluates resolved packages against the lock file, enforcing or overriding
/// package versions and feeds based on the configured lock file mode.
/// </summary>
public sealed class LockFileCoordinator(LockFileStore store, IOptions<LockFileOptions> options) : ILockFileCoordinator
{
    private readonly LockFileStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly LockFileOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;

    /// <inheritdoc />
    public async Task<LockFileEvaluationResult> EvaluateAsync(ResolvedPackage resolved, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        if (_options.Mode == LockFileMode.Generate)
        {
            return new(true, "generate", resolved, null);
        }

        var lockFile = await _store.ReadAsync(cancellationToken);
        var entry = lockFile?.Packages.FirstOrDefault(x => string.Equals(x.Id, resolved.Id, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            if (_options.Mode == LockFileMode.Strict && _options.RequireEntryInStrictMode)
            {
                return new(false, "strict-missing-entry", null, null);
            }

            return new(true, "enforce-no-entry", resolved, null);
        }

        if (_options.Mode is LockFileMode.Enforce or LockFileMode.Strict)
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
