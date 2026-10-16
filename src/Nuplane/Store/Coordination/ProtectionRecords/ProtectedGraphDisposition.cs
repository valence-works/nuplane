using System;

namespace Nuplane.Store.Coordination.ProtectionRecords;

/// <summary>Describes the independent protection closures in which a graph snapshot appears.</summary>
[Flags]
public enum ProtectedGraphDisposition
{
    /// <summary>The snapshot describes the current active graph selection.</summary>
    Active = 1,

    /// <summary>The snapshot is a graph the recovery policy may still select.</summary>
    Recoverable = 2,

    /// <summary>The same snapshot is both active and selectable by recovery.</summary>
    ActiveAndRecoverable = Active | Recoverable
}
