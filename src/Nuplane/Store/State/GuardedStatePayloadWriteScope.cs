using System.Runtime.ExceptionServices;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.State;

/// <summary>Owns all lexical/referent guards and retained native evidence for one ordinary state write.</summary>
internal sealed class GuardedStatePayloadWriteScope : IGuardedStatePayloadWriteScope
{
    private readonly IPhysicalStoreFileSystem _files;
    private readonly IPhysicalStoreNameFileSystem _names;
    private readonly IPhysicalStoreStateFileSystem _stateFiles;
    private readonly ResolvedNativeStateSlot _lexical;
    private readonly ResolvedStatePayloadReferent _referent;
    private readonly List<GuardBinding> _guards = [];
    private PlannedPublication? _publication;
    private bool _disposed;

    private GuardedStatePayloadWriteScope(
        IPhysicalStoreFileSystem files,
        ResolvedNativeStateSlot lexical,
        ResolvedStatePayloadReferent referent)
    {
        _files = files;
        _names = files as IPhysicalStoreNameFileSystem
            ?? throw Unknown("Native state parent name semantics are unavailable.");
        _stateFiles = files as IPhysicalStoreStateFileSystem
            ?? throw Unknown("Native state payload I/O is unavailable.");
        _lexical = lexical;
        _referent = referent;
    }

    public PhysicalStoreDirectoryHandle Parent => _lexical.Parent;
    public StateSlotIdentity Slot => _lexical.Slot;
    public PhysicalStoreStateSlotWriteGuard.PhysicalStoreStateSlotWriteLease PrimaryLease
        => _guards.Single(binding => binding.Slot == Slot).Lease;

    /// <summary>Resolves metadata and holds every unique guard before admitting any payload open.</summary>
    internal static async Task<GuardedStatePayloadWriteScope> AcquireAsync(
        IPhysicalStoreFileSystem files,
        PackageStoreAuthorityResolver resolver,
        string capturedAbsolutePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentException.ThrowIfNullOrWhiteSpace(capturedAbsolutePath);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(capturedAbsolutePath))
            throw new ArgumentException("A state operation requires one captured absolute path.", nameof(capturedAbsolutePath));
        ResolvedNativeStateSlot? lexical = null;
        ResolvedStatePayloadReferent? referent = null;
        GuardedStatePayloadWriteScope? scope = null;
        try
        {
            lexical = resolver.ResolveOrdinaryStateSlotForWrite(capturedAbsolutePath, cancellationToken);
            referent = resolver.ResolveStatePayloadReferentMetadata(capturedAbsolutePath, cancellationToken);
            scope = new GuardedStatePayloadWriteScope(files, lexical, referent);
            lexical = null;
            referent = null;
            var requested = scope._referent.RequestedSlot;
            if (requested.ParentIdentity != scope.Slot.ParentIdentity || requested.NameSemantics != scope.Slot.NameSemantics ||
                scope.ObserveDestination() != requested.Entry)
                throw Unknown("Lexical and payload resolutions disagree about the selected state entry.");

            var guard = new PhysicalStoreStateSlotWriteGuard(files);
            await scope.AddGuardAsync(guard, scope.Parent, scope.Slot, cancellationToken).ConfigureAwait(false);
            foreach (var metadata in scope._referent.TerminalLinkSlots.Append(scope._referent.FinalPayloadSlot))
            {
                var selected = scope.SelectSlot(metadata);
                await scope.AddGuardAsync(guard, metadata.Parent, selected, cancellationToken).ConfigureAwait(false);
            }
            scope.RevalidateCore(cancellationToken);
            if (scope._referent.TerminalLinkSlots.Count > 0 && scope._referent.FinalPayloadSlot.Entry is null)
                throw new FileNotFoundException("The final state symbolic-link referent does not exist.", capturedAbsolutePath);
            return scope;
        }
        catch (Exception operationError)
        {
            var errors = new List<Exception>();
            try
            {
                if (scope is not null)
                    await scope.DisposeAsync().ConfigureAwait(false);
                else
                    referent?.Dispose();
            }
            catch (Exception error) { errors.Add(error); }
            try { lexical?.Dispose(); }
            catch (Exception error) { errors.Add(error); }
            if (errors.Count > 0)
                throw new AggregateException("State guard acquisition failed and retained evidence could not be fully released.",
                    new[] { operationError }.Concat(errors));
            ExceptionDispatchInfo.Capture(operationError).Throw();
            throw;
        }
    }

    public ValueTask RevalidateAsync(CancellationToken cancellationToken)
    {
        RevalidateCore(cancellationToken);
        return ValueTask.CompletedTask;
    }

    public void RequireMarkerAbsenceAndGuards()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var binding in _guards)
        {
            binding.Lease.RequireGroupBindingMarkerAbsent();
            binding.Lease.Revalidate();
        }
    }

    public PhysicalStoreEntryInfo? ObserveDestination()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireParentAndProfile(Parent, Slot);
        var entry = _files.InspectChildNoFollow(Parent, Slot.CanonicalBasename);
        PhysicalStoreStateFileChecks.RequireEntry(entry, entry);
        if (entry is not null && _files is WindowsPhysicalStoreFileSystem windows)
        {
            var observed = windows.ObserveStateDestinationEntryNoFollow(
                Parent, Slot.CanonicalBasename, Slot.ParentIdentity, Slot.NameSemantics);
            PhysicalStoreStateFileChecks.RequireEntry(observed.Entry, entry);
            if (observed.StoredName != Slot.CanonicalBasename)
                throw Unknown("The lexical state entry no longer has its selected stored name.");
        }
        PhysicalStoreStateFileChecks.RequireEntry(_files.InspectChildNoFollow(Parent, Slot.CanonicalBasename), entry);
        RequireParentAndProfile(Parent, Slot);
        return entry;
    }

    public Stream OpenPayloadRead(PhysicalStoreEntryInfo expectedLexicalEntry, long maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(expectedLexicalEntry);
        RevalidateCore(CancellationToken.None);
        PhysicalStoreStateFileChecks.RequireEntry(ObserveDestination(), expectedLexicalEntry);
        if (IsPublished())
            return _stateFiles.OpenStatePayloadReadStream(Parent, Slot, expectedLexicalEntry, maximumBytes);

        var payload = _referent.FinalPayloadSlot;
        var expected = payload.Entry ?? throw Unknown("An absent state payload cannot be opened for reading.");
        var selected = SelectSlot(payload);
        PhysicalStoreStateFileChecks.RequireEntry(_files.InspectChildNoFollow(payload.Parent, payload.Name), expected);
        RequireMarkerAbsenceAndGuards();
        return _stateFiles.OpenStatePayloadReadStream(payload.Parent, selected, expected, maximumBytes);
    }

    public void Publish(string stagedName, PhysicalFileIdentity stagedIdentity, PhysicalStoreEntryInfo? prior)
    {
        RevalidateCore(CancellationToken.None);
        PhysicalStoreStateFileChecks.RequireEntry(ObserveDestination(), prior);
        // Preserve the qualified BCL Windows refusal rather than silently broadening Save semantics.
        if (_files is WindowsPhysicalStoreFileSystem && prior?.Kind == PhysicalStoreEntryKind.SymbolicLink)
            throw new IOException("The existing Windows state replacement does not support a symbolic-link destination.");
        if (_publication is not null)
            throw new InvalidOperationException("One state scope can attempt only one publication.");
        _publication = new PlannedPublication(stagedName, stagedIdentity);
        _stateFiles.PublishStateFileAt(Parent, stagedName, stagedIdentity, Slot, prior);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        var errors = new List<Exception>();
        for (var index = _guards.Count - 1; index >= 0; index--)
        {
            try { await _guards[index].Lease.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { errors.Add(error); }
        }
        try { _referent.Dispose(); }
        catch (Exception error) { errors.Add(error); }
        try { _lexical.Dispose(); }
        catch (Exception error) { errors.Add(error); }
        if (errors.Count == 1)
            ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 1)
            throw new AggregateException("State guards and retained path evidence could not be fully released.", errors);
    }

    private void RevalidateCore(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireMarkerAbsenceAndGuards();
        _lexical.RevalidateRetainedParent(cancellationToken);
        if (IsPublished())
        {
            _referent.RevalidateAfterLexicalReplacement(_publication!.Identity, cancellationToken);
            foreach (var alias in _referent.TerminalLinkSlots.Skip(1))
                RequireMetadataUnchanged(alias);
            if (!ReferenceEquals(_referent.RequestedSlot, _referent.FinalPayloadSlot))
                RequireMetadataUnchanged(_referent.FinalPayloadSlot);
        }
        else
        {
            _referent.Revalidate(cancellationToken);
            foreach (var alias in _referent.TerminalLinkSlots)
                RequireMetadataUnchanged(alias);
            RequireMetadataUnchanged(_referent.FinalPayloadSlot);
        }
        RequireMarkerAbsenceAndGuards();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private bool IsPublished()
    {
        if (_publication is null)
            return false;
        var current = ObserveDestination();
        if (current?.Identity != _publication.Identity)
            return false;
        if (current.Kind != PhysicalStoreEntryKind.RegularFile || current.LinkCount != 1 ||
            _files.InspectChildNoFollow(Parent, _publication.Name) is not null)
            throw Unknown("The planned state publication does not have one exact committed destination and absent stage.");
        return true;
    }

    private void RequireMetadataUnchanged(StatePayloadSlotMetadata metadata)
    {
        var selected = SelectSlot(metadata);
        RequireParentAndProfile(metadata.Parent, selected);
        PhysicalStoreStateFileChecks.RequireEntry(_files.InspectChildNoFollow(metadata.Parent, metadata.Name), metadata.Entry);
        if (metadata.LinkTargetText is not null &&
            _files.ReadLinkTargetNoFollow(metadata.Parent, metadata.Name, metadata.Entry!.Identity) != metadata.LinkTargetText)
            throw Unknown("A retained terminal state link changed its exact target text.");
    }

    private StateSlotIdentity SelectSlot(StatePayloadSlotMetadata metadata)
    {
        var name = metadata.Name;
        RequireParentAndProfile(metadata.Parent, new StateSlotIdentity(metadata.ParentIdentity, metadata.NameSemantics, name));
        PhysicalStoreStateFileChecks.RequireEntry(_files.InspectChildNoFollow(metadata.Parent, name), metadata.Entry);
        if (metadata.Entry is not null && _files is WindowsPhysicalStoreFileSystem windows)
        {
            var observed = windows.ObserveStateDestinationEntryNoFollow(
                metadata.Parent, name, metadata.ParentIdentity, metadata.NameSemantics);
            PhysicalStoreStateFileChecks.RequireEntry(observed.Entry, metadata.Entry);
            name = observed.StoredName;
        }
        return new StateSlotIdentity(metadata.ParentIdentity, metadata.NameSemantics, name);
    }

    private async Task AddGuardAsync(
        PhysicalStoreStateSlotWriteGuard guard,
        PhysicalStoreDirectoryHandle parent,
        StateSlotIdentity slot,
        CancellationToken cancellationToken)
    {
        if (_guards.Any(binding => binding.Slot == slot))
            return;
        var lease = await guard.AcquireAsync(parent, slot, cancellationToken).ConfigureAwait(false);
        _guards.Add(new GuardBinding(slot, lease));
    }

    private void RequireParentAndProfile(PhysicalStoreDirectoryHandle parent, StateSlotIdentity slot)
    {
        PhysicalStoreStateFileChecks.RequireParent(_files.InspectHandle(parent), slot);
        if (_names.ObserveDirectoryNameSemantics(parent) != slot.NameSemantics)
            throw Unknown("A retained state parent changed its native lookup profile.");
    }

    private static PackageStoreAdmissionException Unknown(string message)
        => new(PackageStoreAdmissionReason.UnknownAuthority, message);

    private sealed record GuardBinding(StateSlotIdentity Slot, PhysicalStoreStateSlotWriteGuard.PhysicalStoreStateSlotWriteLease Lease);
    private sealed record PlannedPublication(string Name, PhysicalFileIdentity Identity);
}
