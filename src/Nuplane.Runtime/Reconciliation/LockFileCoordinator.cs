using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;

namespace Nuplane.Runtime.Reconciliation;

public sealed record LockFileEvaluationResult(
    bool Allowed,
    string ReasonCode,
    ResolvedPackage? EffectivePackage,
    string? ExpectedHash);

public sealed class LockFileCoordinator
{
    private readonly LockFileStore store;
    private readonly LockFileOptions options;

    public LockFileCoordinator(LockFileStore store, LockFileOptions options)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

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
