using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Tests.Coordination;

[Trait("Platform", "Native")]
public sealed class PhysicalStoreDirectoryPublicationTests
{
    private const string FinalName = ".nuplane-store";

    [SupportedPhysicalStoreFact]
    public void Publish_PopulatedDirectoryPreservesChildIdentitiesAndContents()
    {
        using var context = CreateContext();
        var tree = PrepareTree(context, "Prepared", "initial-ledger"u8.ToArray());
        var observed = Directories(context).ObserveCanonicalDirectoryNameNoFollow(context.Parent, "Prepared", tree.Directory);
        Assert.Equal("Prepared", observed.Basename);
        Assert.Equal(context.FileSystem.InspectHandle(context.Parent).Identity, observed.ParentIdentity);

        var published = Directories(context).PublishDirectoryNoReplaceAt(context.Parent, "Prepared", tree.Directory, FinalName);

        Assert.Equal(PhysicalStoreEntryKind.Directory, published.Kind);
        Assert.Equal(tree.Directory, published.Identity);
        Assert.Null(context.FileSystem.InspectChildNoFollow(context.Parent, "Prepared"));
        AssertTree(context, FinalName, tree);
        var final = Directories(context).ObserveCanonicalDirectoryNameNoFollow(context.Parent, FinalName, tree.Directory);
        Assert.Equal(FinalName, final.Basename);
        Assert.Equal(observed.Semantics, final.Semantics);
    }

    [SupportedPhysicalStoreFact]
    public void Publish_OccupiedDirectoryFileOrLinkPreservesAllEvidence()
    {
        foreach (var kind in new[] { "directory", "file", "link" })
        {
            using var context = CreateContext();
            var stage = PrepareTree(context, "Prepared", "staged-ledger"u8.ToArray());
            PreparedTree? destination = null;
            if (kind == "directory")
                destination = PrepareTree(context, FinalName, "existing-ledger"u8.ToArray());
            else if (kind == "file")
                CreateFile(context.FileSystem, context.Parent, FinalName, "existing-file"u8.ToArray());
            else
            {
                PrepareTree(context, "link-target", "link-target"u8.ToArray());
                Directory.CreateSymbolicLink(Path.Combine(context.ParentPath, FinalName), Path.Combine(context.ParentPath, "link-target"));
            }
            var before = context.FileSystem.InspectChildNoFollow(context.Parent, FinalName)!;

            AssertRefused(() => Directories(context).PublishDirectoryNoReplaceAt(context.Parent, "Prepared", stage.Directory, FinalName));

            AssertTree(context, "Prepared", stage);
            var after = context.FileSystem.InspectChildNoFollow(context.Parent, FinalName)!;
            Assert.Equal(before.Identity, after.Identity);
            Assert.Equal(before.Kind, after.Kind);
            if (destination is not null)
                AssertTree(context, FinalName, destination);
            else if (kind == "file")
                Assert.Equal("existing-file"u8.ToArray(), ReadFile(context.FileSystem, context.Parent, FinalName));
            else
                Assert.Equal(PhysicalStoreEntryKind.SymbolicLink, after.Kind);
        }
    }

    [SupportedPhysicalStoreFact]
    public void Publish_RejectsWrongIdentityRegularFileAndDirectoryLink()
    {
        using var context = CreateContext();
        var stage = PrepareTree(context, "Prepared", "staged-ledger"u8.ToArray());
        var other = PrepareTree(context, "Other", "other-ledger"u8.ToArray());
        var file = CreateFile(context.FileSystem, context.Parent, "plain-file", "file"u8.ToArray());
        Directory.CreateSymbolicLink(Path.Combine(context.ParentPath, "stage-link"), Path.Combine(context.ParentPath, "Prepared"));

        AssertRefused(() => Directories(context).PublishDirectoryNoReplaceAt(context.Parent, "Prepared", other.Directory, FinalName));
        AssertRefused(() => Directories(context).PublishDirectoryNoReplaceAt(context.Parent, "plain-file", file, FinalName));
        AssertRefused(() => Directories(context).PublishDirectoryNoReplaceAt(context.Parent, "stage-link", stage.Directory, FinalName));
        AssertRefused(() => Directories(context).ObserveCanonicalDirectoryNameNoFollow(context.Parent, "Prepared", other.Directory));
        AssertRefused(() => Directories(context).ObserveCanonicalDirectoryNameNoFollow(context.Parent, "plain-file", file));
        AssertRefused(() => Directories(context).ObserveCanonicalDirectoryNameNoFollow(context.Parent, "stage-link", stage.Directory));

        Assert.Null(context.FileSystem.InspectChildNoFollow(context.Parent, FinalName));
        AssertTree(context, "Prepared", stage);
        AssertTree(context, "Other", other);
        Assert.Equal(file, context.FileSystem.InspectChildNoFollow(context.Parent, "plain-file")!.Identity);
        Assert.Equal("file"u8.ToArray(), ReadFile(context.FileSystem, context.Parent, "plain-file"));
    }

    [SupportedPhysicalStoreFact]
    public void Publish_RequiresCanonicalSpellingAndRefusesDestinationAliases()
    {
        using var context = CreateContext();
        var stage = PrepareTree(context, "Prepared", "staged-ledger"u8.ToArray());
        var names = Directories(context);
        var observed = names.ObserveCanonicalDirectoryNameNoFollow(context.Parent, "Prepared", stage.Directory);
        AssertRefused(() => names.PublishDirectoryNoReplaceAt(context.Parent, "prepared", stage.Directory, FinalName));
        if (!observed.Semantics.CaseSensitive)
            Assert.Equal("Prepared", names.ObserveCanonicalDirectoryNameNoFollow(context.Parent, "prepared", stage.Directory).Basename);
        AssertTree(context, "Prepared", stage);
        Assert.Null(context.FileSystem.InspectChildNoFollow(context.Parent, FinalName));

        var occupied = PrepareTree(context, "Occupied", "occupied-ledger"u8.ToArray());
        if (!observed.Semantics.CaseSensitive)
        {
            AssertRefused(() => names.PublishDirectoryNoReplaceAt(context.Parent, "Prepared", stage.Directory, "occupied"));
            AssertTree(context, "Occupied", occupied);
            AssertTree(context, "Prepared", stage);
        }
        else
        {
            var result = names.PublishDirectoryNoReplaceAt(context.Parent, "Prepared", stage.Directory, "occupied");
            Assert.Equal(stage.Directory, result.Identity);
            AssertTree(context, "occupied", stage);
            AssertTree(context, "Occupied", occupied);
        }
    }

    [SupportedPhysicalStoreFact]
    public void Publish_UsesNativeUnicodeSpellingAndRefusesNormalizationAliases()
    {
        using var context = CreateContext();
        const string composed = "Caf\u00e9";
        const string decomposed = "Cafe\u0301";
        var tree = PrepareTree(context, composed, "unicode-ledger"u8.ToArray());
        var directories = Directories(context);
        var actual = directories.ObserveCanonicalDirectoryNameNoFollow(context.Parent, composed, tree.Directory);
        Assert.Equal(composed, actual.Basename);
        if (actual.Semantics.NormalizationInsensitive)
        {
            Assert.Equal(composed, directories.ObserveCanonicalDirectoryNameNoFollow(context.Parent, decomposed, tree.Directory).Basename);
            AssertRefused(() => directories.PublishDirectoryNoReplaceAt(context.Parent, decomposed, tree.Directory, FinalName));
        }
        else
            AssertRefused(() => directories.ObserveCanonicalDirectoryNameNoFollow(context.Parent, decomposed, tree.Directory));
        AssertTree(context, composed, tree);
        directories.PublishDirectoryNoReplaceAt(context.Parent, composed, tree.Directory, FinalName);
        AssertTree(context, FinalName, tree);
        Assert.Null(context.FileSystem.InspectChildNoFollow(context.Parent, composed));
    }

    [SupportedPhysicalStoreFact]
    public void Publish_RefusesForeignClosedParentsAndInvalidComponents()
    {
        using var context = CreateContext();
        var stage = PrepareTree(context, "Prepared", "staged-ledger"u8.ToArray());
        var directories = Directories(context);
        IPhysicalStoreDirectoryPublicationFileSystem foreign = OperatingSystem.IsWindows()
            ? new WindowsPhysicalStoreFileSystem()
            : new UnixPhysicalStoreFileSystem();
        AssertRefused(() => foreign.PublishDirectoryNoReplaceAt(context.Parent, "Prepared", stage.Directory, FinalName), PackageStoreAdmissionReason.RootMismatch);
        foreach (var invalid in new[] { ".", "..", "child/name", "child\\name", "" })
        {
            Assert.ThrowsAny<ArgumentException>(() => directories.PublishDirectoryNoReplaceAt(context.Parent, invalid, stage.Directory, FinalName));
            Assert.ThrowsAny<ArgumentException>(() => directories.PublishDirectoryNoReplaceAt(context.Parent, "Prepared", stage.Directory, invalid));
        }
        AssertRefused(() => directories.PublishDirectoryNoReplaceAt(context.Parent, "Prepared", stage.Directory, "Prepared"));
        AssertTree(context, "Prepared", stage);
        Assert.Null(context.FileSystem.InspectChildNoFollow(context.Parent, FinalName));
        context.Parent.Dispose();
        AssertRefused(() => directories.PublishDirectoryNoReplaceAt(context.Parent, "Prepared", stage.Directory, FinalName), PackageStoreAdmissionReason.ExpiredScope);
    }

    [SupportedPhysicalStoreFact]
    public void Publish_SecondPublisherCannotOverwriteWinnerAndRetainsLosingStage()
    {
        using var context = CreateContext();
        var winner = PrepareTree(context, "First", "winner"u8.ToArray());
        var loser = PrepareTree(context, "Second", "loser"u8.ToArray());
        var directories = Directories(context);

        directories.PublishDirectoryNoReplaceAt(context.Parent, "First", winner.Directory, FinalName);
        AssertRefused(() => directories.PublishDirectoryNoReplaceAt(context.Parent, "Second", loser.Directory, FinalName));

        AssertTree(context, FinalName, winner);
        AssertTree(context, "Second", loser);
        Assert.Null(context.FileSystem.InspectChildNoFollow(context.Parent, "First"));
    }

    internal static PhysicalStorePublicationTestContext CreateContext()
        => OperatingSystem.IsWindows()
            ? WindowsPhysicalStorePublicationTests.CreateContext()
            : UnixPhysicalStorePublicationTests.CreateContext();

    internal static IPhysicalStoreDirectoryPublicationFileSystem Directories(PhysicalStorePublicationTestContext context)
        => (IPhysicalStoreDirectoryPublicationFileSystem)context.FileSystem;

    internal static PreparedTree PrepareTree(PhysicalStorePublicationTestContext context, string name, byte[] contents)
    {
        using var directory = context.FileSystem.CreateDirectoryExclusiveAt(context.Parent, name);
        var directoryIdentity = context.FileSystem.InspectHandle(directory).Identity;
        var ledger = CreateFile(context.FileSystem, directory, "membership.json", contents);
        using var nested = context.FileSystem.CreateDirectoryExclusiveAt(directory, "nested");
        var nestedIdentity = context.FileSystem.InspectHandle(nested).Identity;
        var payload = CreateFile(context.FileSystem, nested, "payload.bin", contents);
        return new PreparedTree(directoryIdentity, nestedIdentity, ledger, payload, contents);
    }

    internal static void AssertTree(PhysicalStorePublicationTestContext context, string name, PreparedTree expected)
    {
        using var directory = context.FileSystem.OpenDirectoryChildNoFollow(context.Parent, name);
        Assert.Equal(expected.Directory, context.FileSystem.InspectHandle(directory).Identity);
        Assert.Equal(expected.Ledger, context.FileSystem.InspectChildNoFollow(directory, "membership.json")!.Identity);
        Assert.Equal(expected.Contents, ReadFile(context.FileSystem, directory, "membership.json"));
        using var nested = context.FileSystem.OpenDirectoryChildNoFollow(directory, "nested");
        Assert.Equal(expected.Nested, context.FileSystem.InspectHandle(nested).Identity);
        Assert.Equal(expected.Payload, context.FileSystem.InspectChildNoFollow(nested, "payload.bin")!.Identity);
        Assert.Equal(expected.Contents, ReadFile(context.FileSystem, nested, "payload.bin"));
    }

    private static PhysicalFileIdentity CreateFile(IPhysicalStoreFileSystem files,
        PhysicalStoreDirectoryHandle parent, string name, byte[] contents)
    {
        using var file = files.CreateFileExclusiveAt(parent, name);
        files.WriteNewControlFile(file, contents);
        return files.InspectHandle(file).Identity;
    }

    private static byte[] ReadFile(IPhysicalStoreFileSystem files, PhysicalStoreDirectoryHandle parent, string name)
    {
        using var file = files.OpenFileChildNoFollow(parent, name, FileAccess.Read);
        return files.ReadControlFile(file, 1024);
    }

    private static void AssertRefused(Action action, PackageStoreAdmissionReason reason = PackageStoreAdmissionReason.UnknownAuthority)
    {
        var exception = Assert.Throws<PackageStoreAdmissionException>(action);
        Assert.Equal(reason, exception.Reason);
    }

    internal sealed record PreparedTree(PhysicalFileIdentity Directory, PhysicalFileIdentity Nested,
        PhysicalFileIdentity Ledger, PhysicalFileIdentity Payload, byte[] Contents);
}
