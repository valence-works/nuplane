using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;

namespace Nuplane.Store.Coordination.MembershipRecords;

internal static class MembershipRecordValueCopies
{
    internal static PhysicalRootIdentity CopyRoot(PhysicalRootIdentity value)
        => new(ProtectionRecordValueCopies.CopyIdentity(value.HandleIdentity));

    internal static PhysicalFileIdentity CopyIdentity(PhysicalFileIdentity value)
        => ProtectionRecordValueCopies.CopyIdentity(value);

    internal static PhysicalStoreNameSemantics CopyNameSemantics(PhysicalStoreNameSemantics value)
        => new(value.ProfileId, value.Encoding, value.CaseSensitive, value.NormalizationInsensitive);

    internal static StateSlotIdentity CopySlot(StateSlotIdentity value)
        => new(CopyIdentity(value.ParentIdentity), CopyNameSemantics(value.NameSemantics), value.CanonicalBasename);
}
