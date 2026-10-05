using Nuplane.Abstractions;

namespace Nuplane.Reconciliation.LockFile;

internal sealed record LockFileSnapshot(
    LockFileMode Mode,
    PackageLockFile? ExistingLockFile,
    IReadOnlyDictionary<string, PackageLockEntry> Entries,
    LockFilePolicyException? ValidationFailure);
