using Microsoft.Extensions.Options;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.Maintenance;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

public sealed partial class RootMembershipNativeGroupPublicationTests
{
    [SupportedPhysicalStoreFact]
    public async Task InspectRootAsync_V2CatalogProjectsProtectionForSelectedRootOnly()
    {
        using var context = await Context.CreateWithGraphAsync();
        await context.Registry().PublishNativeGroupAsync(context.Descriptor, context.Requests,
            context.NextState, CancellationToken.None);
        var catalog = Catalog(context.RootAPath, context.RootBPath);
        var resolver = new PackageStoreRootResolver(catalog, Options.Create(new PackageStoreMaintenanceOptions()));
        var service = new PackageStoreInspectionService(context.Files, context.Registry(), catalog, resolver,
            new PackageStoreInventory(context.Files), new PackageStoreRetentionPlanner());

        var selectedA = await service.InspectRootAsync("root-0", keepNewestInactiveVersionsPerPackage: 0);
        var selectedB = await service.InspectRootAsync("root-1", keepNewestInactiveVersionsPerPackage: 0);

        Assert.Equal(context.RootAIdentity, selectedA.Root);
        Assert.Equal(context.RootBIdentity, selectedB.Root);
        AssertLocalProtection(selectedA, context.RootAIdentity, context.NextState);
        AssertLocalProtection(selectedB, context.RootBIdentity, context.NextState);
    }

    [SupportedPhysicalStoreFact]
    public async Task InspectRootAsync_CorruptConfiguredPeerAcknowledgementRefusesValidSelectedRootWithoutMutation()
    {
        using var context = await Context.CreateWithGraphAsync();
        await context.Registry().PublishNativeGroupAsync(context.Descriptor, context.Requests,
            context.NextState, CancellationToken.None);
        var peer = await context.AddIndependentCompleteV1RootAsync();
        File.AppendAllText(peer.StateFilePath, "corrupted-acknowledged-state");
        using var peerRoot = PhysicalStoreTestDirectory.Open(context.Files, peer.PackageInstallRoot);
        using var peerControl = context.Files.OpenDirectoryChildNoFollow(peerRoot, RootMembershipRegistry.ControlDirectoryName);
        using var peerStateParent = PhysicalStoreTestDirectory.Open(context.Files,
            Path.GetDirectoryName(peer.StateFilePath)!);
        var peerStateName = Path.GetFileName(peer.StateFilePath);
        var peerStateIdentity = context.Files.InspectChildNoFollow(peerStateParent, peerStateName)!.Identity;
        var peerLedgerIdentity = context.Files.InspectChildNoFollow(peerControl, RootMembershipRegistry.LedgerName)!.Identity;
        var peerStateBytes = await File.ReadAllBytesAsync(peer.StateFilePath);
        var before = CaptureInspectionEvidence(context, [peer.PackageInstallRoot], [peer.StateFilePath]);
        var catalog = Catalog(context.RootAPath, context.RootBPath, peer.PackageInstallRoot);
        var resolver = new PackageStoreRootResolver(catalog, Options.Create(new PackageStoreMaintenanceOptions()));
        var service = new PackageStoreInspectionService(context.Files, context.Registry(), catalog, resolver,
            new PackageStoreInventory(context.Files), new PackageStoreRetentionPlanner());

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(
            () => service.InspectRootAsync("root-0"));

        Assert.Contains(refusal.Reason, new[] { PackageStoreAdmissionReason.UnknownAuthority,
            PackageStoreAdmissionReason.StateMismatch });
        Assert.Equal(before, CaptureInspectionEvidence(context, [peer.PackageInstallRoot], [peer.StateFilePath]));
        Assert.Equal(peerStateIdentity, context.Files.InspectChildNoFollow(peerStateParent, peerStateName)!.Identity);
        Assert.Equal(peerStateBytes, await File.ReadAllBytesAsync(peer.StateFilePath));
        Assert.Equal(peerLedgerIdentity,
            context.Files.InspectChildNoFollow(peerControl, RootMembershipRegistry.LedgerName)!.Identity);
    }

    [SupportedPhysicalStoreTheory]
    [InlineData("root-0")]
    [InlineData("root-1")]
    public async Task InspectRootAsync_PendingGroupOnSelectedOrPeerRefusesWithoutMutation(string selectedLabel)
    {
        using var context = await Context.CreateWithGraphAsync();
        await context.Registry().PublishNativeGroupAsync(context.Descriptor, context.Requests,
            context.NextState, CancellationToken.None);
        var next = context.PrepareNextGraphPublication();
        await Assert.ThrowsAsync<SimulatedCrashException>(() => context.Registry().PublishNativeGroupAsync(
            next.Descriptor, context.Requests, next.NextState, CancellationToken.None,
            CrashOnce(RootMembershipRegistry.NativeGroupPublicationPoint.IntentPublished, context.RootAIdentity)));
        var transactionStem = $".nuplane-group-{next.Descriptor.TransactionId:N}";
        var nextStage = Path.Combine(Path.GetDirectoryName(context.SharedStatePath)!, transactionStem + ".tmp");
        var nextBackup = Path.Combine(Path.GetDirectoryName(context.SharedStatePath)!, transactionStem + ".bak");
        var before = CaptureInspectionEvidence(context, extraPaths: [nextStage, nextBackup]);
        var catalog = Catalog(context.RootAPath, context.RootBPath);
        var resolver = new PackageStoreRootResolver(catalog, Options.Create(new PackageStoreMaintenanceOptions()));
        var service = new PackageStoreInspectionService(context.Files, context.Registry(), catalog, resolver,
            new PackageStoreInventory(context.Files), new PackageStoreRetentionPlanner());

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(
            () => service.InspectRootAsync(selectedLabel));

        Assert.Contains(refusal.Reason, new[] { PackageStoreAdmissionReason.UnknownAuthority,
            PackageStoreAdmissionReason.IncompleteEnrollment });
        Assert.Equal(before, CaptureInspectionEvidence(context, extraPaths: [nextStage, nextBackup]));
    }

    private static void AssertLocalProtection(
        PackageStoreInspectionSnapshot snapshot,
        PhysicalRootIdentity root,
        StoreStateRecord nextState)
    {
        Assert.Equal(PackageStoreAdmissionStatus.Enrolled, snapshot.AdmissionStatus);
        Assert.Equal(PackageStoreRetentionProtectionKnowledge.Known, snapshot.ProtectionKnowledge);
        var bundle = Assert.IsType<PackageProtectionBundle>(nextState.ProtectionBundle);
        var row = Assert.Single(bundle.Rows, candidate => candidate.RootIdentity == root);
        var expectedActive = row.ActiveClosure.Graphs!.SelectMany(static graph => graph.Nodes)
            .Select(static node => node.Install).Where(install => install.Root == root).ToHashSet();
        var expectedLkg = row.RecoverableClosure.Graphs!.SelectMany(static graph => graph.Nodes)
            .Select(static node => node.Install).Where(install => install.Root == root).ToHashSet();
        var entries = Assert.IsType<PackageStoreRetentionPlan>(snapshot.RetentionPlan).Entries;
        var actualActive = entries.Where(static entry => entry.Reasons.Contains(PackageStoreRetentionReason.ProtectedActive))
            .Select(static entry => entry.Install).ToHashSet();
        var actualLkg = entries.Where(static entry => entry.Reasons.Contains(PackageStoreRetentionReason.ProtectedRecoverableLastKnownGood))
            .Select(static entry => entry.Install).ToHashSet();
        Assert.True(expectedActive.SetEquals(actualActive), "The selected root's exact active installs must remain protected.");
        Assert.True(expectedLkg.SetEquals(actualLkg), "The selected root's exact recoverable LKG installs must remain protected.");
    }

    private static string CaptureInspectionEvidence(
        Context context,
        IEnumerable<string>? additionalRootPaths = null,
        IEnumerable<string>? extraPaths = null)
    {
        var paths = context.ReadCurrentLedgers().SelectMany(static ledger => ledger.Members)
            .Select(static member => member.ConfiguredLocator)
            .Append(context.SharedStatePath)
            .Concat([context.StagePath, context.BackupPath, context.GroupMarkerPath])
            .Concat(extraPaths ?? []);
        return PackageStoreInspectionEvidenceCapture.Capture(context.Files,
            [context.RootAPath, context.RootBPath, .. additionalRootPaths ?? []], paths);
    }
}

internal static class PackageStoreInspectionEvidenceCapture
{
    internal static string Capture(
        IPhysicalStoreFileSystem files,
        IEnumerable<string> roots,
        IEnumerable<string> explicitPaths)
    {
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var entries = new SortedDictionary<string, (string Kind, string Identity, string? Bytes)>(pathComparer);
        var visitedDirectories = new HashSet<string>(pathComparer);

        foreach (var root in roots)
            AddTree(Path.GetFullPath(root));
        foreach (var path in explicitPaths.Select(Path.GetFullPath))
        {
            AddTree(Path.GetFullPath(Path.GetDirectoryName(path)!));
            AddEntry(path);
        }

        return System.Text.Json.JsonSerializer.Serialize(entries.Select(static pair => new
        {
            Path = pair.Key,
            pair.Value.Kind,
            pair.Value.Identity,
            pair.Value.Bytes
        }).ToArray());

        void AddTree(string directoryPath)
        {
            if (!visitedDirectories.Add(directoryPath))
                return;
            using (var directory = PhysicalStoreTestDirectory.Open(files, directoryPath))
            {
                var info = files.InspectHandle(directory);
                entries[directoryPath] = ("Directory", info.Identity.ToString(), null);
            }
            foreach (var entryPath in Directory.EnumerateFileSystemEntries(directoryPath)
                         .OrderBy(static path => path, pathComparer))
            {
                var entry = AddEntry(entryPath);
                if (entry?.Kind == PhysicalStoreEntryKind.Directory)
                    AddTree(entryPath);
            }
        }

        PhysicalStoreEntryInfo? AddEntry(string path)
        {
            var parentPath = Path.GetDirectoryName(path)!;
            using var parent = PhysicalStoreTestDirectory.Open(files, parentPath);
            var info = files.InspectChildNoFollow(parent, Path.GetFileName(path));
            if (info is null)
            {
                entries[path] = ("Missing", string.Empty, null);
                return null;
            }

            var lockFile = Path.GetFileName(path).EndsWith(".lock", StringComparison.OrdinalIgnoreCase);
            var bytes = info.Kind == PhysicalStoreEntryKind.RegularFile && !lockFile
                ? Convert.ToHexString(File.ReadAllBytes(path))
                : null;
            entries[path] = (info.Kind.ToString(), info.Identity.ToString(), bytes);
            return info;
        }
    }
}
