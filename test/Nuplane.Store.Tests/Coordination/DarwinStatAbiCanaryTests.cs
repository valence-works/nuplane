using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles.Unix;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

[Trait("Platform", "Darwin")]
public sealed class DarwinStatAbiCanaryTests
{
    [DarwinFact]
    public async Task DarwinStatAbiCanary_CompiledNativeLayout_MatchesManagedDarwinStat()
    {
        using var fixture = new PackageStoreFixture();
        var sourcePath = Path.Combine(AppContext.BaseDirectory, "Native", "darwin-stat-layout.c");
        var executablePath = fixture.GetPath("darwin-stat-layout");

        var compile = await RunAsync(
            "xcrun",
            fixture.RootPath,
            "clang",
            "-std=c11",
            "-Wall",
            "-Wextra",
            "-Werror",
            sourcePath,
            "-o",
            executablePath);
        Assert.True(compile.ExitCode == 0, $"Darwin C ABI canary compilation failed: {compile.StandardError}");

        var native = await RunAsync(executablePath, fixture.RootPath, fixture.RootPath);
        Assert.True(native.ExitCode == 0, $"Darwin C ABI canary failed: {native.StandardError}");
        var nativeLayout = JsonSerializer.Deserialize<NativeStatLayout>(native.StandardOutput)
            ?? throw new InvalidOperationException("The native Darwin stat ABI canary returned no layout.");

        var expectedArchitecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new InvalidOperationException(
                $"The Darwin stat ABI canary does not support process architecture {RuntimeInformation.ProcessArchitecture}.")
        };
        Assert.Equal(expectedArchitecture, nativeLayout.Architecture);

        var managedStat = typeof(UnixPhysicalStoreFileSystem).Assembly.GetType(
            "Nuplane.Store.Coordination.PhysicalFiles.Unix.UnixNative+Darwin+DarwinStat",
            throwOnError: true)!;
        Assert.Equal(nativeLayout.Size, Marshal.SizeOf(managedStat));
        Assert.Equal(nativeLayout.Device, OffsetOf(managedStat, "Device"));
        Assert.Equal(nativeLayout.Mode, OffsetOf(managedStat, "Mode"));
        Assert.Equal(nativeLayout.LinkCount, OffsetOf(managedStat, "LinkCount"));
        Assert.Equal(nativeLayout.Inode, OffsetOf(managedStat, "Inode"));
        Assert.Equal(nativeLayout.Length, OffsetOf(managedStat, "Length"));

        using var directory = new SafeFileHandle(
            (IntPtr)UnixNative.OpenDirectoryAt(UnixPlatform.Darwin, -2, fixture.RootPath), ownsHandle: true);
        var directoryFd = checked((int)directory.DangerousGetHandle());
        AssertMetadata(nativeLayout.ByHandle, UnixNative.StatHandle(UnixPlatform.Darwin, directoryFd));
        var byName = UnixNative.StatAt(UnixPlatform.Darwin, directoryFd, ".");
        Assert.Equal(UnixStatResultStatus.Success, byName.Status);
        AssertMetadata(nativeLayout.ByName, byName.Metadata);
    }

    private static void AssertMetadata(NativeStatMetadata expected, UnixMetadata actual)
    {
        Assert.Equal(expected.Device.ToString(CultureInfo.InvariantCulture), actual.Device);
        Assert.Equal(expected.Mode, actual.Mode);
        Assert.Equal(expected.LinkCount, actual.LinkCount);
        Assert.Equal(expected.Inode, actual.Inode);
        Assert.Equal(expected.Length, actual.Length);
    }

    private static int OffsetOf(Type structureType, string fieldName)
        => checked((int)Marshal.OffsetOf(structureType, fieldName));

    private static async Task<ProcessResult> RunAsync(string fileName, string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
    }

    private sealed record NativeStatLayout(
        string Architecture,
        int Size,
        int Device,
        int Mode,
        int LinkCount,
        int Inode,
        int Length,
        NativeStatMetadata ByHandle,
        NativeStatMetadata ByName);

    private sealed record NativeStatMetadata(int Device, int Mode, ulong LinkCount, ulong Inode, long Length);

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}

public sealed class DarwinFactAttribute : FactAttribute
{
    public DarwinFactAttribute()
    {
        if (!OperatingSystem.IsMacOS())
            Skip = "The Darwin stat ABI canary runs only on a native macOS process.";
    }
}
