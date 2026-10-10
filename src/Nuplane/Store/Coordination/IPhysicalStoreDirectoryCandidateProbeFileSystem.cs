using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

/// <summary>Samples one direct child package candidate with its first candidate-file open performed natively.</summary>
/// <remarks>Only positively observed sharing or lock contention is reported as Busy; uncertain native outcomes refuse.</remarks>
internal interface IPhysicalStoreDirectoryCandidateProbeFileSystem
{
    PhysicalStoreDirectoryCandidateSample SamplePackageCandidateNoFollow(
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        PhysicalFileIdentity? expectedIdentity,
        IDisposable? identityPin);

    void RevalidatePackageCandidate(
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        PhysicalFileIdentity expectedIdentity,
        IDisposable? identityPin);
}

internal enum PhysicalStoreDirectoryCandidateKind
{
    Missing,
    Directory,
    Busy,
    RegularFile
}

internal sealed record PhysicalStoreDirectoryCandidateSample(
    PhysicalStoreDirectoryCandidateKind Kind,
    PhysicalFileIdentity? Identity = null,
    long Length = 0,
    IDisposable? IdentityPin = null,
    bool RegularFileMetadataVerified = false)
{
    internal bool IsVerifiedRegularFile
        => Kind == PhysicalStoreDirectoryCandidateKind.RegularFile ||
           Kind == PhysicalStoreDirectoryCandidateKind.Busy && RegularFileMetadataVerified;
}
