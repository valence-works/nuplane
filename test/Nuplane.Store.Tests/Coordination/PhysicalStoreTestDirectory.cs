using System.Runtime.InteropServices;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Tests.Coordination;

/// <summary>Opens only an owned test directory, resolving fixture ancestry before a no-follow component walk.</summary>
internal static class PhysicalStoreTestDirectory
{
    internal static PhysicalStoreDirectoryHandle Open(IPhysicalStoreFileSystem files, string ownedPath)
    {
        var path = OperatingSystem.IsWindows() ? Path.GetFullPath(ownedPath) : RealPath(ownedPath);
        var anchor = OperatingSystem.IsWindows() ? Path.GetPathRoot(path)! : "/";
        var current = files.OpenNamespaceRoot(anchor);
        try
        {
            foreach (var component in path[anchor.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                var next = files.OpenDirectoryChildNoFollow(current, component);
                current.Dispose();
                current = next;
            }
            return current;
        }
        catch { current.Dispose(); throw; }
    }

    private static string RealPath(string path)
    {
        var pointer = OperatingSystem.IsMacOS() ? DarwinRealPath(path, IntPtr.Zero) : LinuxRealPath(path, IntPtr.Zero);
        if (pointer == IntPtr.Zero)
            throw new IOException($"Owned fixture realpath failed ({Marshal.GetLastPInvokeError()}).");
        try { return Marshal.PtrToStringUTF8(pointer) ?? throw new IOException("Owned fixture realpath returned no path."); }
        finally
        {
            if (OperatingSystem.IsMacOS()) DarwinFree(pointer);
            else LinuxFree(pointer);
        }
    }

    [DllImport("libSystem.B.dylib", EntryPoint = "realpath", SetLastError = true)]
    private static extern IntPtr DarwinRealPath(string path, IntPtr buffer);
    [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
    private static extern IntPtr LinuxRealPath(string path, IntPtr buffer);
    [DllImport("libSystem.B.dylib", EntryPoint = "free")]
    private static extern void DarwinFree(IntPtr pointer);
    [DllImport("libc", EntryPoint = "free")]
    private static extern void LinuxFree(IntPtr pointer);
}
