using System.Buffers.Binary;
using System.Text;
using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Store.Coordination;

/// <summary>An immutable permanent binding between one state slot and one fixed multi-root member set.</summary>
/// <remarks>The value carries no filesystem path or authority to open a participant.</remarks>
internal sealed record PhysicalStoreGroupBindingMarker
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("NUPLANE-STATE-GROUP\0");
    private const int SchemaVersion = 1;
    private const int DigestBytes = 32;
    private const int GuidBytes = 16;
    private const int VersionBytes = sizeof(int);
    internal static int MaximumBytes => Magic.Length + VersionBytes + DigestBytes + GuidBytes + DigestBytes;

    private PhysicalStoreGroupBindingMarker(
        string stateSlotIdentityDigest,
        Guid logicalMemberId,
        string participantSetDigest)
    {
        StateSlotIdentityDigest = stateSlotIdentityDigest;
        LogicalMemberId = logicalMemberId;
        ParticipantSetDigest = participantSetDigest;
    }

    internal string StateSlotIdentityDigest { get; }
    internal Guid LogicalMemberId { get; }
    internal string ParticipantSetDigest { get; }

    internal static PhysicalStoreGroupBindingMarker Create(
        string stateSlotIdentityDigest,
        Guid logicalMemberId,
        string participantSetDigest)
    {
        ArgumentNullException.ThrowIfNull(stateSlotIdentityDigest);
        ArgumentNullException.ThrowIfNull(participantSetDigest);
        if (logicalMemberId == Guid.Empty)
            throw new ArgumentException("A group marker requires a non-empty logical member identity.", nameof(logicalMemberId));
        ProtectionDigest.ValidateCanonicalDigest(stateSlotIdentityDigest);
        ProtectionDigest.ValidateCanonicalDigest(participantSetDigest);
        return new PhysicalStoreGroupBindingMarker(stateSlotIdentityDigest, logicalMemberId, participantSetDigest);
    }

    internal static byte[] Encode(PhysicalStoreGroupBindingMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);
        var verified = Create(marker.StateSlotIdentityDigest, marker.LogicalMemberId, marker.ParticipantSetDigest);
        if (verified != marker)
            throw new ArgumentException("A group marker is not in canonical form.", nameof(marker));

        var bytes = new byte[MaximumBytes];
        var offset = 0;
        Magic.CopyTo(bytes, offset);
        offset += Magic.Length;
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(offset, VersionBytes), SchemaVersion);
        offset += VersionBytes;
        Convert.FromHexString(marker.StateSlotIdentityDigest).CopyTo(bytes, offset);
        offset += DigestBytes;
        Convert.FromHexString(marker.LogicalMemberId.ToString("N")).CopyTo(bytes, offset);
        offset += GuidBytes;
        Convert.FromHexString(marker.ParticipantSetDigest).CopyTo(bytes, offset);
        return bytes;
    }

    internal static PhysicalStoreGroupBindingMarker Decode(ReadOnlySpan<byte> bytes, string expectedSlotDigest)
    {
        try
        {
            ProtectionDigest.ValidateCanonicalDigest(expectedSlotDigest);
            if (bytes.Length != MaximumBytes || !bytes[..Magic.Length].SequenceEqual(Magic))
                throw Refusal("The group marker has an invalid header or length.");

            var offset = Magic.Length;
            if (BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(offset, VersionBytes)) != SchemaVersion)
                throw Refusal("The group marker schema is unsupported.");
            offset += VersionBytes;

            var slotDigest = Convert.ToHexString(bytes.Slice(offset, DigestBytes)).ToLowerInvariant();
            if (!string.Equals(slotDigest, expectedSlotDigest, StringComparison.Ordinal))
                throw Refusal("The group marker binds a different native state slot.");
            offset += DigestBytes;

            var logicalIdText = Convert.ToHexString(bytes.Slice(offset, GuidBytes));
            if (!Guid.TryParseExact(logicalIdText, "N", out var logicalMemberId) || logicalMemberId == Guid.Empty)
                throw Refusal("The group marker logical member identity is invalid.");
            offset += GuidBytes;

            var participantDigest = Convert.ToHexString(bytes.Slice(offset, DigestBytes)).ToLowerInvariant();
            return Create(slotDigest, logicalMemberId, participantDigest);
        }
        catch (PackageStoreAdmissionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
        {
            throw Refusal("The group marker contents are malformed.", exception);
        }
    }

    private static PackageStoreAdmissionException Refusal(string message, Exception? innerException = null)
        => new(PackageStoreAdmissionReason.UnknownAuthority, message, innerException: innerException);
}
