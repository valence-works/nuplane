namespace Nuplane.Store.Coordination.PhysicalFiles;

/// <summary>Reports whether an exclusive-lock attempt may proceed for one file-handle wrapper.</summary>
internal enum PhysicalStoreLockReservationResult
{
    Acquired,
    Busy,
    Poisoned
}
