namespace Nuplane.Abstractions.PackageStoreProtection;

/// <summary>Describes whether a physical package-store root participates in protection.</summary>
public enum PackageStoreAdmissionStatus
{
    /// <summary>The root is not enrolled and keeps ordinary package-store behavior.</summary>
    Unenrolled,
    /// <summary>The root is enrolled and has a live operation owner.</summary>
    Enrolled
}
