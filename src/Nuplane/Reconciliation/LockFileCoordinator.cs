using System.Text.Json;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Reconciliation.LockFile;
using Nuplane.Reconciliation.Models;

namespace Nuplane.Reconciliation;


/// <summary>
/// Evaluates resolved packages against the lock file, enforcing or overriding
/// package versions and feeds based on the configured lock file mode.
/// </summary>
public sealed class LockFileCoordinator(LockFileStore store, IOptions<LockFileOptions> options) :
    ILockFileCoordinator,
    IPackagePathIndependentLockFileCoordinator,
    ILockFileCycleCoordinator
{
    private const string CurrentSchemaVersion = "2.0";
    private const string HashPrefix = "sha512:";
    private readonly LockFileStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly LockFileOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;

    /// <inheritdoc />
    public async Task<LockFileEvaluationResult> EvaluateAsync(ResolvedPackage resolved, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        var snapshot = await CaptureAsync(cancellationToken);
        return Evaluate(snapshot, resolved);
    }

    internal async Task<LockFileSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        PackageLockFile? lockFile;
        try
        {
            lockFile = await _store.ReadAsync(cancellationToken);
        }
        catch (JsonException exception)
        {
            return CreateSnapshot(null, new(
                "invalid-lock-file",
                $"The lock file is not valid JSON for the Nuplane lock schema: {exception.Message}"));
        }

        if (_options.Mode is LockFileMode.Enforce or LockFileMode.Strict &&
            lockFile is not null &&
            !string.Equals(lockFile.SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            return CreateSnapshot(lockFile, new(
                "unsupported-schema",
                $"Lock schema '{lockFile.SchemaVersion}' is not supported for {_options.Mode} mode. Run Generate mode to migrate the lock file to schema {CurrentSchemaVersion}."));
        }

        if (lockFile is not null)
        {
            if (lockFile.Packages is null ||
                lockFile.Packages.Any(static entry => entry is null || string.IsNullOrWhiteSpace(entry.Id)))
            {
                return CreateSnapshot(null, new(
                    "invalid-lock-file",
                    "The lock file must contain a packages array whose entries have non-empty package identifiers."));
            }

            var duplicate = lockFile.Packages
                .GroupBy(static entry => entry.Id, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(static group => group.Skip(1).Any());
            if (duplicate is not null)
            {
                return CreateSnapshot(lockFile, new(
                    "duplicate-entry",
                    $"Lock file contains more than one entry for package '{duplicate.Key}'. Regenerate the lock file."));
            }
        }

        return CreateSnapshot(lockFile, null);
    }

    internal PackageRequest ConstrainRequest(LockFileSnapshot snapshot, PackageRequest request)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(request);

        if (snapshot.Mode == LockFileMode.Generate)
        {
            return request;
        }

        ThrowIfSnapshotInvalid(snapshot);
        if (!snapshot.Entries.TryGetValue(request.Id, out var entry))
        {
            if (snapshot.Mode == LockFileMode.Strict)
            {
                throw new LockFilePolicyException(
                    "strict-missing-entry",
                    $"Strict lock mode requires an entry for acquired package '{request.Id}'. Run Generate mode to capture the complete dependency closure.");
            }

            return request;
        }

        ValidateEntryHash(entry);
        return request with
        {
            VersionRange = entry.Version,
            FeedName = entry.Feed,
            UpdatePolicy = PackageUpdatePolicy.Exact
        };
    }

    internal LockFileEvaluationResult Evaluate(LockFileSnapshot snapshot, ResolvedPackage resolved)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(resolved);

        if (snapshot.Mode == LockFileMode.Generate)
        {
            return IsCanonicalHash(resolved.PackageContentHash)
                ? new(true, "generate", resolved, null)
                : new(false, "actual-hash-missing", null, null);
        }

        if (snapshot.ValidationFailure is { } validationFailure)
        {
            return new(false, validationFailure.ReasonCode, null, null);
        }

        if (!snapshot.Entries.TryGetValue(resolved.Id, out var entry))
        {
            if (snapshot.Mode == LockFileMode.Strict)
            {
                return new(false, "strict-missing-entry", null, null);
            }

            return new(true, "enforce-no-entry", resolved, null);
        }

        try
        {
            ValidateEntryHash(entry);
        }
        catch (LockFilePolicyException exception)
        {
            return new(false, exception.ReasonCode, null, entry.Hash);
        }

        if (!VersionsEqual(entry.Version, resolved.Version))
        {
            return new(false, "version-mismatch", null, entry.Hash);
        }

        if (!string.Equals(entry.Feed, resolved.FeedName, StringComparison.OrdinalIgnoreCase))
        {
            return new(false, "feed-mismatch", null, entry.Hash);
        }

        if (resolved.PackageContentHash is null)
        {
            return new(false, "actual-hash-missing", null, entry.Hash);
        }

        if (!string.Equals(entry.Hash, resolved.PackageContentHash, StringComparison.Ordinal))
        {
            return new(false, "hash-mismatch", null, entry.Hash);
        }

        return new(true, "enforced", resolved, entry.Hash);

    }

    async Task<LockFileSnapshot> ILockFileCycleCoordinator.CaptureAsync(CancellationToken cancellationToken) =>
        await CaptureAsync(cancellationToken);

    PackageRequest ILockFileCycleCoordinator.ConstrainRequest(LockFileSnapshot snapshot, PackageRequest request) =>
        ConstrainRequest(snapshot, request);

    LockFileEvaluationResult ILockFileCycleCoordinator.Evaluate(LockFileSnapshot snapshot, ResolvedPackage resolved) =>
        Evaluate(snapshot, resolved);

    async Task ILockFileCycleCoordinator.GenerateAsync(
        LockFileSnapshot snapshot,
        IReadOnlyList<ResolvedPackage> resolvedPackages,
        DateTimeOffset generatedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(resolvedPackages);

        if (snapshot.Mode != LockFileMode.Generate)
        {
            return;
        }

        var missingHash = resolvedPackages.FirstOrDefault(static package => !IsCanonicalHash(package.PackageContentHash));
        if (missingHash is not null)
        {
            throw new LockFilePolicyException(
                "actual-hash-missing",
                $"Cannot generate a provenance lock because package '{missingHash.Id}@{missingHash.Version}' has no canonical archive hash.");
        }

        var existingLock = snapshot.ExistingLockFile;
        var previousEntries = snapshot.ValidationFailure is null && existingLock is not null &&
            string.Equals(existingLock.SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal)
            ? existingLock.Packages.ToDictionary(static entry => entry.Id, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, PackageLockEntry>(StringComparer.OrdinalIgnoreCase);
        var entries = resolvedPackages
            .Select(package =>
            {
                var hash = package.PackageContentHash!;
                var timestamp = previousEntries.TryGetValue(package.Id, out var previous) &&
                    EntryMatches(previous, package, hash)
                        ? previous.Timestamp
                        : generatedAt;
                return new PackageLockEntry(package.Id, package.Version, package.FeedName, hash, timestamp);
            })
            .OrderBy(static entry => entry.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static entry => entry.Id, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Version, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Feed, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Hash, StringComparer.Ordinal)
            .ToArray();

        var semanticContentsUnchanged = snapshot.ExistingLockFile is { } existing &&
            string.Equals(existing.SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal) &&
            existing.Packages.Count == entries.Length &&
            existing.Packages
                .OrderBy(static entry => entry.Id, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static entry => entry.Id, StringComparer.Ordinal)
                .SequenceEqual(entries);
        var effectiveGeneratedAt = semanticContentsUnchanged
            ? snapshot.ExistingLockFile!.GeneratedAt
            : generatedAt;

        await _store.WriteAsync(
            new(CurrentSchemaVersion, effectiveGeneratedAt, entries),
            cancellationToken);
    }

    private LockFileSnapshot CreateSnapshot(PackageLockFile? lockFile, LockFilePolicyException? validationFailure) =>
        new(
            _options.Mode,
            lockFile,
            lockFile?.Packages
                .GroupBy(static entry => entry.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, PackageLockEntry>(StringComparer.OrdinalIgnoreCase),
            validationFailure);

    private static void ThrowIfSnapshotInvalid(LockFileSnapshot snapshot)
    {
        if (snapshot.ValidationFailure is { } validationFailure)
        {
            throw validationFailure;
        }
    }

    private static void ValidateEntryHash(PackageLockEntry entry)
    {
        if (!IsCanonicalHash(entry.Hash))
        {
            throw new LockFilePolicyException(
                "invalid-hash",
                $"Lock entry for '{entry.Id}' does not contain a canonical schema 2.0 SHA-512 archive hash. Run Generate mode to migrate the lock file.");
        }
    }

    private static bool IsCanonicalHash(string? hash)
    {
        if (hash is null || !hash.StartsWith(HashPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var encoded = hash[HashPrefix.Length..];
        try
        {
            var bytes = Convert.FromBase64String(encoded);
            return bytes.Length == 64 && string.Equals(Convert.ToBase64String(bytes), encoded, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool VersionsEqual(string expected, string actual) =>
        NuGet.Versioning.NuGetVersion.TryParse(expected, out var expectedVersion) &&
        NuGet.Versioning.NuGetVersion.TryParse(actual, out var actualVersion) &&
        expectedVersion == actualVersion;

    private static bool EntryMatches(PackageLockEntry entry, ResolvedPackage package, string hash) =>
        string.Equals(entry.Id, package.Id, StringComparison.OrdinalIgnoreCase) &&
        VersionsEqual(entry.Version, package.Version) &&
        string.Equals(entry.Feed, package.FeedName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(entry.Hash, hash, StringComparison.Ordinal);
}
