using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;
using static Nuplane.Store.Tests.Coordination.PhysicalStoreDirectoryPublicationTests;

namespace Nuplane.Store.Tests.Coordination;

[Trait("Platform", "Native")]
public sealed class PhysicalStoreDeletionTests
{
    [SupportedPhysicalStoreFact]
    public void Remove_DeletesExactFileAndEmptyDirectoryAndPreservesSibling()
    {
        using var context = CreateContext();
        var deletion = (IPhysicalStoreDeletionFileSystem)context.FileSystem;
        var file = CreateFile(context, "payload", "remove");
        var directory = CreateDirectory(context, "empty");
        var sibling = CreateFile(context, "sibling", "keep");

        deletion.RemoveFileAt(context.Parent, "payload", file);
        deletion.RemoveEmptyDirectoryAt(context.Parent, "empty", directory);

        Assert.Null(context.FileSystem.InspectChildNoFollow(context.Parent, "payload"));
        Assert.Null(context.FileSystem.InspectChildNoFollow(context.Parent, "empty"));
        Assert.Equal(sibling, context.FileSystem.InspectChildNoFollow(context.Parent, "sibling")!.Identity);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(context.ParentPath, "sibling")));
    }

    [SupportedPhysicalStoreFact]
    public void RemoveFile_RefusesReplacedIdentityAndPreservesBothFiles()
    {
        using var context = CreateContext();
        var deletion = (IPhysicalStoreDeletionFileSystem)context.FileSystem;
        var original = CreateFile(context, "payload", "original");
        File.Move(Path.Combine(context.ParentPath, "payload"), Path.Combine(context.ParentPath, "saved"));
        var replacement = CreateFile(context, "payload", "replacement");

        AssertRefused(() => deletion.RemoveFileAt(context.Parent, "payload", original));

        Assert.Equal(replacement, context.FileSystem.InspectChildNoFollow(context.Parent, "payload")!.Identity);
        Assert.Equal(original, context.FileSystem.InspectChildNoFollow(context.Parent, "saved")!.Identity);
        Assert.Equal("replacement", File.ReadAllText(Path.Combine(context.ParentPath, "payload")));
        Assert.Equal("original", File.ReadAllText(Path.Combine(context.ParentPath, "saved")));
    }

    [SupportedPhysicalStoreFact]
    public void RemoveDirectory_RefusesReplacedIdentityAndPreservesBothDirectories()
    {
        using var context = CreateContext();
        var deletion = (IPhysicalStoreDeletionFileSystem)context.FileSystem;
        var original = CreateDirectory(context, "empty");
        Directory.Move(Path.Combine(context.ParentPath, "empty"), Path.Combine(context.ParentPath, "saved"));
        var replacement = CreateDirectory(context, "empty");

        AssertRefused(() => deletion.RemoveEmptyDirectoryAt(context.Parent, "empty", original));

        Assert.Equal(replacement, context.FileSystem.InspectChildNoFollow(context.Parent, "empty")!.Identity);
        Assert.Equal(original, context.FileSystem.InspectChildNoFollow(context.Parent, "saved")!.Identity);
    }

    [SupportedPhysicalStoreFact]
    public void RemoveDirectory_RefusesNonemptyDirectoryAndPreservesNestedPayload()
    {
        using var context = CreateContext();
        var deletion = (IPhysicalStoreDeletionFileSystem)context.FileSystem;
        var directory = CreateDirectory(context, "nonempty");
        using (var held = context.FileSystem.OpenDirectoryChildNoFollow(context.Parent, "nonempty"))
        using (var nested = context.FileSystem.CreateFileExclusiveAt(held, "payload"))
            context.FileSystem.WriteNewControlFile(nested, "keep"u8.ToArray());

        AssertRefused(() => deletion.RemoveEmptyDirectoryAt(context.Parent, "nonempty", directory));

        Assert.Equal(directory, context.FileSystem.InspectChildNoFollow(context.Parent, "nonempty")!.Identity);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(context.ParentPath, "nonempty", "payload")));
    }

    [SupportedPhysicalStoreFact]
    public void Remove_RefusesWrongEntryKindsWithoutMutation()
    {
        using var context = CreateContext();
        var deletion = (IPhysicalStoreDeletionFileSystem)context.FileSystem;
        var file = CreateFile(context, "payload", "keep");
        var directory = CreateDirectory(context, "empty");

        AssertRefused(() => deletion.RemoveEmptyDirectoryAt(context.Parent, "payload", file));
        AssertRefused(() => deletion.RemoveFileAt(context.Parent, "empty", directory));

        Assert.Equal(file, context.FileSystem.InspectChildNoFollow(context.Parent, "payload")!.Identity);
        Assert.Equal(directory, context.FileSystem.InspectChildNoFollow(context.Parent, "empty")!.Identity);
    }

    [SupportedPhysicalStoreFact]
    public void Remove_RefusesMissingEntriesInsteadOfCreditingRemoval()
    {
        using var context = CreateContext();
        var deletion = (IPhysicalStoreDeletionFileSystem)context.FileSystem;
        var file = CreateFile(context, "payload", "keep");

        AssertRefused(() => deletion.RemoveFileAt(context.Parent, "missing", file));
        AssertRefused(() => deletion.RemoveEmptyDirectoryAt(context.Parent, "missing", file));

        Assert.Equal(file, context.FileSystem.InspectChildNoFollow(context.Parent, "payload")!.Identity);
    }

    [SupportedPhysicalStoreFact]
    public void Remove_RejectsTraversalAndRootedNamesBeforeMutation()
    {
        using var context = CreateContext();
        var deletion = (IPhysicalStoreDeletionFileSystem)context.FileSystem;
        var file = CreateFile(context, "payload", "keep");
        foreach (var name in new[] { "", ".", "..", "../payload", "nested/payload", "nested\\payload", "/payload" })
        {
            Assert.ThrowsAny<ArgumentException>(() => deletion.RemoveFileAt(context.Parent, name, file));
            Assert.ThrowsAny<ArgumentException>(() => deletion.RemoveEmptyDirectoryAt(context.Parent, name, file));
        }

        Assert.Equal("keep", File.ReadAllText(Path.Combine(context.ParentPath, "payload")));
    }

    [SupportedPhysicalStoreFact]
    public void Remove_RefusesSymbolicLinksAndPreservesTargetsOutsideHeldParent()
    {
        using var context = CreateContext();
        var deletion = (IPhysicalStoreDeletionFileSystem)context.FileSystem;
        var targetFile = context.Fixture.GetPath("outside-file");
        var targetDirectory = context.Fixture.CreateDirectory("outside-directory");
        File.WriteAllText(targetFile, "outside");
        File.WriteAllText(Path.Combine(targetDirectory, "payload"), "outside-directory");
        File.CreateSymbolicLink(Path.Combine(context.ParentPath, "file-link"), targetFile);
        Directory.CreateSymbolicLink(Path.Combine(context.ParentPath, "directory-link"), targetDirectory);
        var fileLink = context.FileSystem.InspectChildNoFollow(context.Parent, "file-link")!.Identity;
        var directoryLink = context.FileSystem.InspectChildNoFollow(context.Parent, "directory-link")!.Identity;

        AssertRefused(() => deletion.RemoveFileAt(context.Parent, "file-link", fileLink));
        AssertRefused(() => deletion.RemoveEmptyDirectoryAt(context.Parent, "directory-link", directoryLink));

        Assert.NotNull(context.FileSystem.InspectChildNoFollow(context.Parent, "file-link"));
        Assert.NotNull(context.FileSystem.InspectChildNoFollow(context.Parent, "directory-link"));
        Assert.Equal("outside", File.ReadAllText(targetFile));
        Assert.Equal("outside-directory", File.ReadAllText(Path.Combine(targetDirectory, "payload")));
    }

    [SupportedPhysicalStoreFact]
    public void RemoveFile_RefusesHardLinkAmbiguityAndPreservesBothNames()
    {
        using var context = CreateContext();
        var deletion = (IPhysicalStoreDeletionFileSystem)context.FileSystem;
        var file = CreateFile(context, "payload", "keep");
        var originalPath = Path.Combine(context.ParentPath, "payload");
        var linkPath = Path.Combine(context.ParentPath, "hard-link");
        if (OperatingSystem.IsWindows())
            WindowsPhysicalStorePublicationTests.CreateHardLink(originalPath, linkPath);
        else
            UnixPhysicalStorePublicationTests.CreateHardLink(originalPath, linkPath);

        AssertRefused(() => deletion.RemoveFileAt(context.Parent, "payload", file));
        AssertRefused(() => deletion.RemoveFileAt(context.Parent, "hard-link", file));

        Assert.Equal(file, context.FileSystem.InspectChildNoFollow(context.Parent, "payload")!.Identity);
        Assert.Equal(file, context.FileSystem.InspectChildNoFollow(context.Parent, "hard-link")!.Identity);
        Assert.Equal("keep", File.ReadAllText(originalPath));
        Assert.Equal("keep", File.ReadAllText(linkPath));
    }

    [SupportedPhysicalStoreFact]
    public void Remove_RefusesAliasSpellingWithoutMutation()
    {
        using var context = CreateContext();
        var deletion = (IPhysicalStoreDeletionFileSystem)context.FileSystem;
        var file = CreateFile(context, "Payload", "keep");
        var directory = CreateDirectory(context, "Empty");

        AssertRefused(() => deletion.RemoveFileAt(context.Parent, "pAYLOAD", file));
        AssertRefused(() => deletion.RemoveEmptyDirectoryAt(context.Parent, "eMPTY", directory));

        Assert.Equal(file, context.FileSystem.InspectChildNoFollow(context.Parent, "Payload")!.Identity);
        Assert.Equal(directory, context.FileSystem.InspectChildNoFollow(context.Parent, "Empty")!.Identity);
    }

    [SupportedPhysicalStoreFact]
    public void Remove_RefusesForeignAndDisposedParentsWithoutMutation()
    {
        using var context = CreateContext();
        using var foreign = CreateContext();
        var deletion = (IPhysicalStoreDeletionFileSystem)context.FileSystem;
        var foreignDeletion = (IPhysicalStoreDeletionFileSystem)foreign.FileSystem;
        var file = CreateFile(context, "payload", "keep");
        var directory = CreateDirectory(context, "empty");

        AssertRefused(() => foreignDeletion.RemoveFileAt(context.Parent, "payload", file), PackageStoreAdmissionReason.RootMismatch);
        AssertRefused(() => foreignDeletion.RemoveEmptyDirectoryAt(context.Parent, "empty", directory), PackageStoreAdmissionReason.RootMismatch);
        context.Parent.Dispose();
        AssertRefused(() => deletion.RemoveFileAt(context.Parent, "payload", file), PackageStoreAdmissionReason.ExpiredScope);
        AssertRefused(() => deletion.RemoveEmptyDirectoryAt(context.Parent, "empty", directory), PackageStoreAdmissionReason.ExpiredScope);

        Assert.Equal("keep", File.ReadAllText(Path.Combine(context.ParentPath, "payload")));
        Assert.True(Directory.Exists(Path.Combine(context.ParentPath, "empty")));
    }

    [SupportedPhysicalStoreFact]
    public void Remove_UsesHeldParentAfterTextualRenameAttempt()
    {
        using var context = CreateContext();
        var deletion = (IPhysicalStoreDeletionFileSystem)context.FileSystem;
        var file = CreateFile(context, "payload", "remove");
        var directory = CreateDirectory(context, "empty");
        var movedParent = context.ParentPath + "-moved";
        if (OperatingSystem.IsWindows())
        {
            var renameError = Record.Exception(() => Directory.Move(context.ParentPath, movedParent));
            Assert.True(renameError is IOException or UnauthorizedAccessException,
                "The held Windows parent should refuse a textual rename.");
            Assert.True(Directory.Exists(context.ParentPath));
            Assert.False(Directory.Exists(movedParent));

            deletion.RemoveFileAt(context.Parent, "payload", file);
            deletion.RemoveEmptyDirectoryAt(context.Parent, "empty", directory);

            Assert.Null(context.FileSystem.InspectChildNoFollow(context.Parent, "payload"));
            Assert.Null(context.FileSystem.InspectChildNoFollow(context.Parent, "empty"));
            return;
        }

        Directory.Move(context.ParentPath, movedParent);
        Directory.CreateDirectory(context.ParentPath);
        File.WriteAllText(Path.Combine(context.ParentPath, "payload"), "replacement-parent");
        Directory.CreateDirectory(Path.Combine(context.ParentPath, "empty"));

        deletion.RemoveFileAt(context.Parent, "payload", file);
        deletion.RemoveEmptyDirectoryAt(context.Parent, "empty", directory);

        Assert.False(File.Exists(Path.Combine(movedParent, "payload")));
        Assert.False(Directory.Exists(Path.Combine(movedParent, "empty")));
        Assert.Equal("replacement-parent", File.ReadAllText(Path.Combine(context.ParentPath, "payload")));
        Assert.True(Directory.Exists(Path.Combine(context.ParentPath, "empty")));
    }

    private static PhysicalFileIdentity CreateFile(PhysicalStorePublicationTestContext context, string name, string contents)
    {
        using var file = context.FileSystem.CreateFileExclusiveAt(context.Parent, name);
        context.FileSystem.WriteNewControlFile(file, System.Text.Encoding.UTF8.GetBytes(contents));
        return context.FileSystem.InspectHandle(file).Identity;
    }

    private static PhysicalFileIdentity CreateDirectory(PhysicalStorePublicationTestContext context, string name)
    {
        using var directory = context.FileSystem.CreateDirectoryExclusiveAt(context.Parent, name);
        return context.FileSystem.InspectHandle(directory).Identity;
    }

    private static void AssertRefused(Action action, PackageStoreAdmissionReason reason = PackageStoreAdmissionReason.UnknownAuthority)
    {
        var error = Assert.Throws<PackageStoreAdmissionException>(action);
        Assert.Equal(reason, error.Reason);
    }
}
