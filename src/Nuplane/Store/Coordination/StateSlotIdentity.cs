using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Coordination;

/// <summary>Identifies a stable state entry independently of its replaceable file identity.</summary>
/// <remarks>Canonical spelling is observed from the filesystem and compared exactly, without managed folding.</remarks>
internal sealed record StateSlotIdentity
{
    internal StateSlotIdentity(
        PhysicalFileIdentity parentIdentity,
        PhysicalStoreNameSemantics nameSemantics,
        string canonicalBasename)
    {
        ArgumentNullException.ThrowIfNull(parentIdentity);
        ArgumentNullException.ThrowIfNull(nameSemantics);
        PhysicalStoreCanonicalName.ValidateBasename(canonicalBasename, nameSemantics);

        ParentIdentity = parentIdentity;
        NameSemantics = nameSemantics;
        CanonicalBasename = canonicalBasename;
    }

    public PhysicalFileIdentity ParentIdentity { get; }
    public PhysicalStoreNameSemantics NameSemantics { get; }
    public string CanonicalBasename { get; }
}
