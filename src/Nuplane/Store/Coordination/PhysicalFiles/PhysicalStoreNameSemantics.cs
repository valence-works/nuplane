namespace Nuplane.Store.Coordination.PhysicalFiles;

/// <summary>Describes a qualified native lookup profile; it does not implement name folding.</summary>
internal sealed record PhysicalStoreNameSemantics
{
    internal PhysicalStoreNameSemantics(
        string profileId,
        PhysicalStoreNameEncoding encoding,
        bool caseSensitive,
        bool normalizationInsensitive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        if (!Enum.IsDefined(encoding))
            throw new ArgumentOutOfRangeException(nameof(encoding));

        ProfileId = profileId;
        Encoding = encoding;
        CaseSensitive = caseSensitive;
        NormalizationInsensitive = normalizationInsensitive;
    }

    public string ProfileId { get; }
    public PhysicalStoreNameEncoding Encoding { get; }
    public bool CaseSensitive { get; }
    public bool NormalizationInsensitive { get; }
}
