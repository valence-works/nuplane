using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.DeletionRecords;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Maintenance;

/// <summary>Joins exact native quarantine trees into one fresh execution planner input.</summary>
/// <remarks>
/// Ordinary inspection remains unchanged. Journals are descriptive only: every quarantine is reopened,
/// fully checked against its bounded manifest, and joined only while the exact execution operation lives.
/// </remarks>
internal sealed class PackageStoreDeletionExecutionJoin : IDisposable
{
    private const string QuarantinePrefix = ".nuplane-quarantine-";

    private readonly PackageStoreDeletionJournalStore _store;
    private readonly PackageStoreDeletionCandidateBinder _binder;
    private readonly PhysicalStoreDirectoryHandle _rootHandle;
    private readonly PhysicalRootIdentity _root;
    private readonly long _epoch;
    private readonly Action<CancellationToken> _revalidateOperation;
    private readonly List<JournalBinding> _bindings;
    private readonly object _gate = new();
    private bool _built;
    private bool _disposed;

    internal PackageStoreDeletionExecutionJoin(
        PackageStoreDeletionJournalStore store,
        PackageStoreDeletionCandidateBinder binder,
        PhysicalStoreDirectoryHandle rootHandle,
        PhysicalRootIdentity root,
        long epoch,
        Action<CancellationToken> revalidateOperation,
        CancellationToken cancellationToken)
    {
        _store = store;
        _binder = binder;
        _rootHandle = rootHandle;
        _root = root;
        _epoch = epoch;
        _revalidateOperation = revalidateOperation;
        _bindings = store.Discover(rootHandle, root, epoch, cancellationToken)
            .Select(static journal => new JournalBinding(journal))
            .ToList();
    }

    internal IReadOnlyList<PackageInstallIdentity> BuildOverlay(
        PackageStoreInventorySnapshot inventory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        EnsureLive(cancellationToken);
        lock (_gate)
        {
            if (_built)
                throw Refuse("The quarantine inventory join can be built only once per execution operation.");
            _built = true;
        }

        if (inventory.Root != _root || inventory.Epoch != _epoch)
            throw Refuse("The inventory root or epoch changed before quarantine joining.");

        var reserved = inventory.Entries
            .Where(static entry => entry.RootRelativePath.Split('/').Any(static component =>
                component.StartsWith(QuarantinePrefix, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (reserved.Length > PackageStoreDeletionJournalStore.MaximumPruneArtifacts)
            throw Refuse("The native quarantine namespace exceeds its bounded inventory-entry limit.");

        var quarantineEntriesByPath = new Dictionary<string, PackageStoreInventoryEntry>(StringComparer.Ordinal);
        foreach (var entry in reserved)
        {
            var components = entry.RootRelativePath.Split('/');
            if (components.Length != 3 || !components[2].StartsWith(QuarantinePrefix, StringComparison.OrdinalIgnoreCase))
                throw Refuse("A reserved quarantine prefix appeared outside one exact package-child path.");
            if (!quarantineEntriesByPath.TryAdd(entry.RootRelativePath, entry))
                throw Refuse("The native inventory reported one quarantine name more than once.");
        }

        var overlays = new List<PackageInstallIdentity>();
        var matchedQuarantinePaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in _bindings)
        {
            EnsureLive(cancellationToken);
            var journal = binding.Discovered.Journal;
            var originalRows = inventory.Entries.Where(entry => string.Equals(
                    entry.RootRelativePath, journal.OriginalInstall.RootRelativeInstallPath, StringComparison.Ordinal))
                .ToArray();
            if (originalRows.Length > 1)
                throw Refuse("The inventory contains duplicate rows at a journal-bound original install path.");

            var qPath = string.Join("/", journal.PackageParentRelativeComponents.Append(journal.QuarantineBasename));
            var hasQ = quarantineEntriesByPath.TryGetValue(qPath, out var qEntry);
            if (hasQ)
                matchedQuarantinePaths.Add(qPath);

            if (!hasQ)
            {
                _store.VerifyDiscovered(_rootHandle, _root, binding.Discovered, cancellationToken);
                if (journal.Phase == PackageStoreDeletionPhase.Prepared && originalRows.Length == 1 &&
                    originalRows[0].Kind == PackageStoreInventoryEntryKind.CompletedInstallCandidate &&
                    originalRows[0].InstallIdentity == journal.OriginalInstall)
                {
                    _binder.VerifyPreparedOriginal(binding.Discovered, _rootHandle, EnsureLive, cancellationToken);
                    binding.OriginalAtOriginalName = true;
                }
                else if (journal.Phase == PackageStoreDeletionPhase.Finalizing && originalRows.Length == 0)
                {
                    _binder.VerifyFinalizingQuarantineAbsent(binding.Discovered, _rootHandle, EnsureLive, cancellationToken);
                    binding.QuarantinePositivelyAbsent = true;
                    overlays.Add(journal.OriginalInstall);
                }
                else
                {
                    throw Refuse("The journal has neither its exact original Prepared install nor the phase-allowed quarantine tree.");
                }
                continue;
            }

            if (qEntry!.Kind != PackageStoreInventoryEntryKind.IncompleteInstall)
                throw Refuse("A reserved quarantine directory was not classified as one incomplete version child.");

            _store.VerifyDiscovered(_rootHandle, _root, binding.Discovered, cancellationToken);
            var quarantined = _binder.BindQuarantinedCandidate(binding.Discovered,
                _rootHandle, EnsureLive, cancellationToken);
            binding.QuarantinedCandidate = quarantined;

            var originalSlot = quarantined.InspectOriginalSlot(cancellationToken);
            if (originalSlot is null)
            {
                if (originalRows.Length != 0)
                    throw Refuse("The inventory reports an original install although the held native parent reports its slot absent.");
            }
            else
            {
                if (originalRows.Length != 1 ||
                    originalRows[0].Kind != PackageStoreInventoryEntryKind.CompletedInstallCandidate ||
                    originalRows[0].InstallIdentity is not { } replacement ||
                    replacement.DirectoryIdentity != originalSlot.Identity ||
                    replacement == journal.OriginalInstall)
                    throw Refuse("The original logical slot contains an unverified, incomplete, or contradictory replacement.");
            }

            overlays.Add(journal.OriginalInstall);
        }

        if (matchedQuarantinePaths.Count != quarantineEntriesByPath.Count)
            throw Refuse("An inventoried quarantine prefix has no exact canonical journal and remains untouched.");

        EnsureLive(cancellationToken);
        return overlays;
    }

    internal bool TryFind(PackageInstallIdentity install, out JournalBinding binding)
    {
        ArgumentNullException.ThrowIfNull(install);
        EnsureLive(CancellationToken.None);
        binding = _bindings.SingleOrDefault(item => item.Discovered.Journal.OriginalInstall == install)!;
        return binding is not null;
    }

    internal JournalBinding RequireQuarantined(PackageInstallIdentity install)
    {
        if (!TryFind(install, out var binding) || binding.QuarantinedCandidate is null)
            throw Refuse("The exact current logical install row has no verified quarantine binding.");
        return binding;
    }

    internal JournalBinding RequireOriginalPrepared(PackageStoreDeletionCandidate candidate)
    {
        if (!TryFind(candidate.InstallIdentity, out var binding) ||
            binding.Discovered.Journal.Phase != PackageStoreDeletionPhase.Prepared ||
            !binding.OriginalAtOriginalName || binding.QuarantinedCandidate is not null ||
            (binding.PreparedCandidate is not null && !ReferenceEquals(binding.PreparedCandidate, candidate)) ||
            binding.Discovered.Journal.OriginalInstall != candidate.InstallIdentity)
            throw Refuse("The exact eligible install has no current Prepared journal for its original native slot.");
        return binding;
    }

    internal void RegisterPrepared(
        PackageStoreDeletionCandidate candidate,
        PackageStoreDeletionJournalStore.PublishedJournal published)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(published);
        EnsureLive(CancellationToken.None);
        var journal = published.Journal;
        if (journal.Phase != PackageStoreDeletionPhase.Prepared || journal.StoreRoot != _root ||
            journal.EnrollmentEpoch != _epoch || journal.OriginalInstall != candidate.InstallIdentity ||
            _bindings.Any(binding => binding.Discovered.Journal.CandidateId == journal.CandidateId ||
                                     binding.Discovered.Journal.OriginalInstall.DirectoryIdentity == journal.OriginalInstall.DirectoryIdentity))
            throw Refuse("The newly published Prepared journal does not uniquely bind this exact current candidate.");

        var discovered = new PackageStoreDeletionJournalStore.DiscoveredJournal(
            published.Journal,
            published.CanonicalName,
            published.FileIdentity,
            published.Payload,
            published.RootSemantics,
            published.ControlDirectoryIdentity,
            published.ControlDirectorySemantics);
        _bindings.Add(new JournalBinding(discovered)
        {
            OriginalAtOriginalName = true,
            PreparedCandidate = candidate
        });
    }

    internal void AttachQuarantinedCandidate(
        JournalBinding binding,
        PackageStoreQuarantinedCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(candidate);
        EnsureLive(CancellationToken.None);
        if (!_bindings.Contains(binding) || binding.QuarantinedCandidate is not null ||
            candidate.Journal.CandidateId != binding.Discovered.Journal.CandidateId ||
            candidate.Journal.OriginalInstall != binding.Discovered.Journal.OriginalInstall)
            throw Refuse("The rebound quarantine does not belong to this exact current journal binding.");
        binding.QuarantinedCandidate = candidate;
        binding.OriginalAtOriginalName = false;
    }

    internal void Advance(JournalBinding binding, PackageStoreDeletionJournalStore.PublishedJournal published)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(published);
        EnsureLive(CancellationToken.None);
        if (!_bindings.Contains(binding) || published.Journal.CandidateId != binding.Discovered.Journal.CandidateId ||
            !IsNextPhase(binding.Discovered.Journal.Phase, published.Journal.Phase))
            throw Refuse("The phase replacement does not belong to this exact discovered quarantine candidate.");

        binding.QuarantinedCandidate?.AdvanceJournal(published.Journal, CancellationToken.None);

        binding.Discovered = new PackageStoreDeletionJournalStore.DiscoveredJournal(
            published.Journal,
            published.CanonicalName,
            published.FileIdentity,
            published.Payload,
            published.RootSemantics,
            published.ControlDirectoryIdentity,
            published.ControlDirectorySemantics);
        binding.OriginalAtOriginalName = false;
    }

    private static bool IsNextPhase(PackageStoreDeletionPhase prior, PackageStoreDeletionPhase next)
        => (prior, next) is
            (PackageStoreDeletionPhase.Prepared, PackageStoreDeletionPhase.Quarantined) or
            (PackageStoreDeletionPhase.Quarantined, PackageStoreDeletionPhase.Deleting) or
            (PackageStoreDeletionPhase.Deleting, PackageStoreDeletionPhase.Finalizing);

    public void Dispose()
    {
        JournalBinding[] bindings;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            bindings = _bindings.ToArray();
            _bindings.Clear();
        }

        List<Exception>? failures = null;
        for (var index = bindings.Length - 1; index >= 0; index--)
        {
            try { bindings[index].QuarantinedCandidate?.Dispose(); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
        if (failures is { Count: 1 })
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures is { Count: > 1 })
            throw new AggregateException("Multiple joined quarantine candidates failed to close.", failures);
    }

    private void EnsureLive(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed)
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.ExpiredScope,
                "The quarantine execution join has expired.", _root);
        _revalidateOperation(cancellationToken);
    }

    private PackageStoreAdmissionException Refuse(string message)
        => new(PackageStoreAdmissionReason.UnknownAuthority, message, _root);

    internal sealed class JournalBinding(PackageStoreDeletionJournalStore.DiscoveredJournal discovered)
    {
        internal PackageStoreDeletionJournalStore.DiscoveredJournal Discovered { get; set; } = discovered;
        internal bool OriginalAtOriginalName { get; set; }
        internal bool QuarantinePositivelyAbsent { get; set; }
        internal PackageStoreDeletionCandidate? PreparedCandidate { get; set; }
        internal PackageStoreQuarantinedCandidate? QuarantinedCandidate { get; set; }
    }
}
