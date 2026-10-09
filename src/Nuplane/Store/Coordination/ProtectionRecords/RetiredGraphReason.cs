namespace Nuplane.Store.Coordination.ProtectionRecords;

/// <summary>Describes why a previously protected graph is no longer recoverable.</summary>
public enum RetiredGraphReason
{
    /// <summary>Durable recovery metadata no longer selects this graph.</summary>
    RecoveryPolicyNoLongerSelects = 1,

    /// <summary>An operator explicitly ended this recovery promise during a quiescent migration.</summary>
    QuiescentOperatorRetirement = 2
}
