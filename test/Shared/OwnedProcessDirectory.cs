using System.Runtime.InteropServices;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Tests.Shared;

/// <summary>Opens an owned process-test directory through a no-follow component walk.</summary>
internal static class OwnedProcessDirectory
{
    /// <summary>Opens an existing absolute directory path without following its final components.</summary>
    internal static PhysicalStoreDirectoryHandle Open(IPhysicalStoreFileSystem files, string path)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("An owned process-test directory path must be absolute.", nameof(path));

        var canonicalPath = OperatingSystem.IsWindows() ? Path.GetFullPath(path) : ResolveExistingPath(path);
        var anchor = Path.GetPathRoot(canonicalPath)
            ?? throw new ArgumentException("The directory path has no namespace root.", nameof(path));
        var current = files.OpenNamespaceRoot(anchor);
        try
        {
            foreach (var component in canonicalPath[anchor.Length..].Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                var next = files.OpenDirectoryChildNoFollow(current, component);
                current.Dispose();
                current = next;
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static string ResolveExistingPath(string path)
    {
        var result = OperatingSystem.IsMacOS()
            ? DarwinRealPath(path, IntPtr.Zero)
            : LinuxRealPath(path, IntPtr.Zero);
        if (result == IntPtr.Zero)
            throw new IOException($"Could not resolve the owned process-test directory ({Marshal.GetLastPInvokeError()}).");

        try
        {
            return Marshal.PtrToStringUTF8(result)
                ?? throw new IOException("Native path resolution returned no path.");
        }
        finally
        {
            if (OperatingSystem.IsMacOS())
                DarwinFree(result);
            else
                LinuxFree(result);
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
