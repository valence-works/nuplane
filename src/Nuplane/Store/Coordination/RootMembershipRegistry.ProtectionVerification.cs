using System.Collections.ObjectModel;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;

namespace Nuplane.Store.Coordination;

internal sealed partial class RootMembershipRegistry
{
    /// <summary>Reopens every acknowledged state and verifies its semantics and native protected install bindings.</summary>
    /// <remarks>
    /// The caller retains root and all member locks throughout this operation. Returned payloads are descriptive
    /// observations, never admission capabilities. Completion/admission must retain that ownership without a gap.
    /// </remarks>
    internal async Task<IReadOnlyDictionary<string, StoreStateRecord>> VerifyAllMemberProtectionAsync(
        LockedMemberLocations context,
        PhysicalStoreDirectoryHandle root,
        PhysicalRootIdentity expectedRoot,
        long expectedEpoch,
        RootMembershipStatus requiredStatus,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(expectedRoot);
        context.Revalidate();
        return await VerifyAllMemberProtectionCoreAsync(context.Ledger, root, expectedRoot, expectedEpoch, requiredStatus,
            context.ReadMemberStateAsync, context.Revalidate, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Publishes Complete only after a fresh full locked reread of every acknowledged member and protected install.</summary>
    internal async Task<RootMembershipRecord> CompleteEnrollmentAsync(
        PhysicalStoreDirectoryHandle root,
        PhysicalRootIdentity expectedRoot,
        long expectedEpoch,
        bool quiescentCutoverConfirmed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(expectedRoot);
        RequireEpoch(expectedEpoch);
        if (!quiescentCutoverConfirmed)
            throw Refused("Completing membership requires explicit confirmation that all declared users remain quiescent.");
        cancellationToken.ThrowIfCancellationRequested();
        RequireRoot(root, expectedRoot);
        await using var transaction = await OpenLockedAsync(root, cancellationToken).ConfigureAwait(false);
        var ledger = transaction.ReadCurrent();
        if (ledger.RootIdentity != expectedRoot || ledger.EnrollmentEpoch != expectedEpoch ||
            ledger.Status != RootMembershipStatus.Incomplete || ledger.PendingStateCommit is not null ||
            ledger.Members.Any(member => member.Binding is not RootMemberRecord.AcknowledgedBinding))
            throw Refused("Enrollment completion requires the exact non-pending Incomplete acknowledged membership.");
        var scope = new MemberLocatorReplayScope(ledger);
        IReadOnlyDictionary<string, ResolvedMemberStateLocation>? locations = null;
        try
        {
            locations = ResolveMemberLocatorMap(ledger, scope, LocatorReplayBindingPolicy.Acknowledged);
            var capturedLocations = locations;
            void Revalidate()
            {
                var current = transaction.ReadCurrent();
                RequireSameDigest(ledger, current);
                RevalidateMemberLocationMap(current, capturedLocations, LocatorReplayBindingPolicy.Acknowledged);
            }
            async Task<StoreStateRecord?> ReadState(string memberId, CancellationToken token)
            {
                Revalidate();
                var member = FindMember(ledger, memberId);
                var observed = await ReadPriorAsync(member.Binding, capturedLocations[memberId].Parent, token).ConfigureAwait(false);
                Revalidate();
                return observed?.State;
            }
            await VerifyAllMemberProtectionCoreAsync(ledger, root, expectedRoot, expectedEpoch,
                RootMembershipStatus.Incomplete, ReadState, Revalidate, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Revalidate();
            var complete = Rebuild(ledger, RootMembershipStatus.Complete, ledger.Members, pending: null);
            transaction.Publish(complete);
            return transaction.Ledger;
        }
        finally
        {
            try
            {
                if (locations is not null)
                    DisposeLocations(locations.Values);
            }
            finally { scope.Expire(); }
        }
    }

    private async Task<IReadOnlyDictionary<string, StoreStateRecord>> VerifyAllMemberProtectionCoreAsync(
        RootMembershipRecord ledger,
        PhysicalStoreDirectoryHandle root,
        PhysicalRootIdentity expectedRoot,
        long expectedEpoch,
        RootMembershipStatus requiredStatus,
        Func<string, CancellationToken, Task<StoreStateRecord?>> readMember,
        Action revalidateMembers,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        revalidateMembers();
        RequireRoot(root, expectedRoot);
        if (ledger.RootIdentity != expectedRoot || ledger.EnrollmentEpoch != expectedEpoch ||
            ledger.Status != requiredStatus || ledger.PendingStateCommit is not null)
            throw Refused("Full member verification requires the exact locked root, epoch and non-pending status.");

        var memberIds = ledger.Members.Select(member => member.MemberId).ToHashSet(StringComparer.Ordinal);
        if (memberIds.Count == 0 || memberIds.Count != ledger.Members.Count || !memberIds.SetEquals(ledger.TargetMemberIds))
            throw Refused("Full member verification requires the exact non-empty member and target union.");
        var result = new Dictionary<string, StoreStateRecord>(StringComparer.Ordinal);
        var observations = new List<IDisposable>();
        var revalidations = new List<Action>();
        var scope = new MemberLocatorReplayScope(ledger);
        try
        {
            foreach (var member in ledger.Members)
            {
                cancellationToken.ThrowIfCancellationRequested();
                revalidateMembers();
                var state = await readMember(member.MemberId, cancellationToken).ConfigureAwait(false)
                    ?? throw Refused("A prospective member cannot pass full protection verification.");
                var graphs = PersistedStoreStateGraphVerifier.VerifyAcknowledgedMember(
                    state, ledger, member, expectedRoot, expectedEpoch, requiredStatus);
                ObserveProtectedInstalls(root, expectedRoot, state,
                    graphs.ActiveGraphs.Concat(graphs.RecoverableGraphs), scope, observations, revalidations,
                    cancellationToken);
                result.Add(member.MemberId, state);
            }

            // Recheck the full union after the final state/install observation, while the same root ownership remains held.
            revalidateMembers();
            foreach (var revalidate in revalidations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                revalidate();
            }
            revalidateMembers();
            return new ReadOnlyDictionary<string, StoreStateRecord>(result);
        }
        finally
        {
            try
            {
                for (var index = observations.Count - 1; index >= 0; index--)
                    observations[index].Dispose();
            }
            finally { scope.Expire(); }
        }
    }

    private void ObserveProtectedInstalls(
        PhysicalStoreDirectoryHandle root,
        PhysicalRootIdentity expectedRoot,
        StoreStateRecord state,
        IEnumerable<ProtectedGraphSnapshot> graphs,
        MemberLocatorReplayScope scope,
        ICollection<IDisposable> observations,
        ICollection<Action> revalidations,
        CancellationToken cancellationToken = default)
    {
        var installs = graphs.SelectMany(graph => graph.Nodes).Select(node => node.Install).Distinct().ToArray();
        var paths = new List<ActiveInstallPathEvidence>();
        foreach (var descriptor in state.ActivePackageDescriptorsByIdNormalized.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var associated = installs.Where(install =>
                string.Equals(install.PackageId, descriptor.PackageId, StringComparison.OrdinalIgnoreCase) &&
                NuGet.Versioning.NuGetVersion.TryParse(install.Version, out var expectedVersion) &&
                NuGet.Versioning.NuGetVersion.TryParse(descriptor.Version, out var descriptorVersion) &&
                NuGet.Versioning.VersionComparer.VersionRelease.Equals(expectedVersion, descriptorVersion)).ToArray();
            if (associated.Length == 0)
                throw Refused("An active descriptor has no exact protected install identity.");
            paths.Add(new ActiveInstallPathEvidence(descriptor, associated));
        }

        ObserveNativeInstallEvidence(root, expectedRoot, installs, paths, scope, observations, revalidations,
            cancellationToken);
    }

    private void ObserveNativeInstallEvidence(
        PhysicalStoreDirectoryHandle root,
        PhysicalRootIdentity expectedRoot,
        IEnumerable<PackageInstallIdentity> installs,
        IEnumerable<ActiveInstallPathEvidence> paths,
        MemberLocatorReplayScope scope,
        ICollection<IDisposable> observations,
        ICollection<Action> revalidations,
        CancellationToken cancellationToken)
    {
        var reader = new PackageInstallIdentityReader(_files);
        foreach (var install in installs.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (install.Root != expectedRoot)
                throw Refused("A member's protected graph names another physical package root.");
            var observed = reader.Observe(root, expectedRoot, install.RootRelativeInstallPath,
                install.PackageId, install.Version);
            observations.Add(observed);
            if (observed.InstallIdentity.DirectoryIdentity != install.DirectoryIdentity ||
                !string.Equals(observed.InstallIdentity.CompletionIdentity, install.CompletionIdentity, StringComparison.Ordinal))
                throw Refused("A protected install no longer has its persisted native directory/completion identity.");
            revalidations.Add(observed.Revalidate);
        }

        var resolver = new PackageStoreAuthorityResolver(_files, this);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolved = resolver.ResolveProtectedInstallPath(path.Descriptor.InstallPath, scope);
            observations.Add(resolved);
            var target = _files.InspectHandle(resolved.Target);
            if (target.Kind != PhysicalStoreEntryKind.Directory ||
                path.AssociatedInstalls.Any(install => install.DirectoryIdentity != target.Identity))
                throw Refused("An active descriptor's actual path does not identify its protected native install.");
            revalidations.Add(resolved.Revalidate);
        }
    }

    private sealed record ActiveInstallPathEvidence(
        Nuplane.Abstractions.ActivePackageDescriptor Descriptor,
        IReadOnlyList<PackageInstallIdentity> AssociatedInstalls);
}
