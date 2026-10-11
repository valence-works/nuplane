using System.Text;
using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Store.Coordination.PhysicalFiles;

/// <summary>Describes one verified native name observation, without minting a store capability.</summary>
internal sealed record PhysicalStoreCanonicalName
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UnicodeEncoding StrictUtf16 = new(false, false, true);

    internal PhysicalStoreCanonicalName(
        PhysicalFileIdentity parentIdentity,
        PhysicalFileIdentity fileIdentity,
        string basename,
        PhysicalStoreNameSemantics semantics)
    {
        ArgumentNullException.ThrowIfNull(parentIdentity);
        ArgumentNullException.ThrowIfNull(fileIdentity);
        ArgumentNullException.ThrowIfNull(semantics);
        ValidateBasename(basename, semantics);

        ParentIdentity = parentIdentity;
        FileIdentity = fileIdentity;
        Basename = basename;
        Semantics = semantics;
    }

    public PhysicalFileIdentity ParentIdentity { get; }
    public PhysicalFileIdentity FileIdentity { get; }
    public string Basename { get; }
    public PhysicalStoreNameSemantics Semantics { get; }

    internal static void ValidateBasename(string basename, PhysicalStoreNameSemantics semantics)
    {
        PhysicalStoreNames.ValidateSingleComponent(basename);
        _ = semantics.Encoding == PhysicalStoreNameEncoding.Utf8
            ? StrictUtf8.GetByteCount(basename)
            : StrictUtf16.GetByteCount(basename);
    }
}
