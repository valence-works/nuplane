using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles.Unix;
using Nuplane.Store.Coordination.PhysicalFiles.Windows;

namespace Nuplane.Store.Coordination;

/// <summary>Marks an exact no-follow observation that an exclusive native child name is already occupied.</summary>
internal sealed class PhysicalStoreExclusiveCreateCollisionException : IOException
{
    internal PhysicalStoreExclusiveCreateCollisionException(string message) : base(message)
    {
    }
}

/// <summary>Recognizes only positively identified native exclusive-create collisions.</summary>
internal static class PhysicalStoreExclusiveCreateCollision
{
    internal static bool IsCollision(Exception exception)
    {
        if (exception is not PackageStoreAdmissionException { InnerException: { } inner })
            return false;

        return inner is PhysicalStoreExclusiveCreateCollisionException ||
               inner is UnixNativeCallException unix && unix.Error == 17 ||
               inner is WindowsNativeCallException windows &&
               windows.NtStatus == WindowsNative.StatusObjectNameCollision;
    }
}
