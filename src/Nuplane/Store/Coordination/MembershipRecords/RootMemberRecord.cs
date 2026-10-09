using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;

namespace Nuplane.Store.Coordination.MembershipRecords;

/// <summary>Describes one member's structural binding in a package-store root ledger.</summary>
/// <remarks>
/// The configured locator is descriptive input. These immutable values are candidates only and do
/// not authorize filesystem access or prove that a persisted protection graph is complete.
/// </remarks>
internal sealed class RootMemberRecord
{
    internal RootMemberRecord(string memberId, string configuredLocator, MemberBinding binding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memberId);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredLocator);
        ArgumentNullException.ThrowIfNull(binding);

        MemberId = memberId;
        ConfiguredLocator = configuredLocator;
        Binding = binding.Copy();
    }

    internal string MemberId { get; }

    internal string ConfiguredLocator { get; }

    internal MemberBinding Binding { get; }

    internal RootMemberRecord Copy() => new(MemberId, ConfiguredLocator, Binding);

    /// <summary>Describes the member binding state; each variant carries only its matching evidence.</summary>
    internal abstract record MemberBinding
    {
        internal abstract MemberBinding Copy();
    }

    /// <summary>A declared member for which no physical state location has yet been verified.</summary>
    internal sealed record DeclaredBinding : MemberBinding
    {
        internal override MemberBinding Copy() => new DeclaredBinding();
    }

    /// <summary>A verified prospective parent/name for a state file that does not yet exist.</summary>
    internal sealed record ProspectiveBinding : MemberBinding
    {
        internal ProspectiveBinding(
            PhysicalFileIdentity verifiedParentIdentity,
            PhysicalStoreNameSemantics nameSemantics,
            string requestedBasename)
        {
            ArgumentNullException.ThrowIfNull(verifiedParentIdentity);
            ArgumentNullException.ThrowIfNull(nameSemantics);
            PhysicalStoreCanonicalName.ValidateBasename(requestedBasename, nameSemantics);

            VerifiedParentIdentity = MembershipRecordValueCopies.CopyIdentity(verifiedParentIdentity);
            NameSemantics = MembershipRecordValueCopies.CopyNameSemantics(nameSemantics);
            RequestedBasename = requestedBasename;
        }

        internal PhysicalFileIdentity VerifiedParentIdentity { get; }

        internal PhysicalStoreNameSemantics NameSemantics { get; }

        internal string RequestedBasename { get; }

        internal override MemberBinding Copy() => new ProspectiveBinding(VerifiedParentIdentity, NameSemantics, RequestedBasename);
    }

    /// <summary>Captures an existing state whose protection property is explicitly absent.</summary>
    /// <remarks>This binding is valid only during incomplete quiescent migration and is never authority.</remarks>
    internal sealed record ExistingUnprotectedBinding : MemberBinding
    {
        internal ExistingUnprotectedBinding(
            StateSlotIdentity stateSlot,
            PhysicalFileIdentity observedStateFileIdentity,
            string stateBodyDigest,
            bool protectionMetadataAbsent)
        {
            ArgumentNullException.ThrowIfNull(stateSlot);
            ArgumentNullException.ThrowIfNull(observedStateFileIdentity);
            ArgumentException.ThrowIfNullOrWhiteSpace(stateBodyDigest);
            if (!protectionMetadataAbsent)
                throw new ArgumentException("An existing-unprotected binding requires protection metadata to be absent.", nameof(protectionMetadataAbsent));

            StateSlot = MembershipRecordValueCopies.CopySlot(stateSlot);
            ObservedStateFileIdentity = MembershipRecordValueCopies.CopyIdentity(observedStateFileIdentity);
            StateBodyDigest = stateBodyDigest;
            ProtectionMetadataAbsent = true;
        }

        internal StateSlotIdentity StateSlot { get; }

        internal PhysicalFileIdentity ObservedStateFileIdentity { get; }

        internal string StateBodyDigest { get; }

        internal bool ProtectionMetadataAbsent { get; }

        internal override MemberBinding Copy()
            => new ExistingUnprotectedBinding(StateSlot, ObservedStateFileIdentity, StateBodyDigest, ProtectionMetadataAbsent);
    }

    /// <summary>An acknowledged state binding with a matching immutable protection candidate.</summary>
    internal sealed record AcknowledgedBinding : MemberBinding
    {
        internal AcknowledgedBinding(
            StateSlotIdentity stateSlot,
            PhysicalFileIdentity observedStateFileIdentity,
            PackageProtectionRecord protectionRecord)
        {
            ArgumentNullException.ThrowIfNull(stateSlot);
            ArgumentNullException.ThrowIfNull(observedStateFileIdentity);
            ArgumentNullException.ThrowIfNull(protectionRecord);

            StateSlot = MembershipRecordValueCopies.CopySlot(stateSlot);
            ObservedStateFileIdentity = MembershipRecordValueCopies.CopyIdentity(observedStateFileIdentity);
            ProtectionRecord = protectionRecord;
        }

        internal StateSlotIdentity StateSlot { get; }

        internal PhysicalFileIdentity ObservedStateFileIdentity { get; }

        internal PackageProtectionRecord ProtectionRecord { get; }

        internal override MemberBinding Copy() => new AcknowledgedBinding(StateSlot, ObservedStateFileIdentity, ProtectionRecord);
    }
}
