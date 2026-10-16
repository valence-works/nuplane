using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Maintenance;

/// <summary>One detached descriptive row in a package-store inventory.</summary>
internal sealed record PackageStoreInventoryEntry
{
    internal PackageStoreInventoryEntry(
        string rootRelativePath,
        PackageStoreInventoryEntryKind kind,
        string? reason = null,
        PackageInstallIdentity? installIdentity = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootRelativePath);
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));

        var components = rootRelativePath.Split('/', StringSplitOptions.None);
        if (components.Length == 0 || components.Any(static component => component is "" or "." or ".."))
            throw new ArgumentException("An inventory path must be root-relative and slash-separated.", nameof(rootRelativePath));
        foreach (var component in components)
            PhysicalStoreNames.ValidateSingleComponent(component);

        if (kind == PackageStoreInventoryEntryKind.CompletedInstallCandidate)
        {
            ArgumentNullException.ThrowIfNull(installIdentity);
            if (!string.Equals(rootRelativePath, installIdentity.RootRelativeInstallPath, StringComparison.Ordinal))
                throw new ArgumentException("A candidate path must match its exact install identity path.", nameof(installIdentity));
        }
        else if (installIdentity is not null)
        {
            throw new ArgumentException("Only a completed-install candidate may carry an install identity.", nameof(installIdentity));
        }

        if (kind is PackageStoreInventoryEntryKind.IncompleteInstall or PackageStoreInventoryEntryKind.Unknown)
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        RootRelativePath = rootRelativePath;
        Kind = kind;
        Reason = reason;
        InstallIdentity = installIdentity;
    }

    internal string RootRelativePath { get; }
    internal PackageStoreInventoryEntryKind Kind { get; }
    internal string? Reason { get; }
    internal PackageInstallIdentity? InstallIdentity { get; }
}
