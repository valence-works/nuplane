using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Sources.Directory;

namespace Nuplane.Runtime.Tests.Sources.Directory;

/// <summary>
/// Unit tests verifying that <see cref="DirectoryNupkgDesiredSource"/> sets
/// FeedName and SourceName attribution correctly on produced package requests.
/// </summary>
public sealed class DirectoryNupkgDesiredSourceTests : IDisposable
{
    private readonly string _tempDir;

    public DirectoryNupkgDesiredSourceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"nuplane-test-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (System.IO.Directory.Exists(_tempDir))
        {
            System.IO.Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task GetDesiredAsync_SetsFeedName_WhenProvided()
    {
        CreateNupkg("MyPlugin.1.0.0.nupkg");
        var source = new DirectoryNupkgDesiredSource("src-name", _tempDir, ["*"], feedName: "local-drop");

        var results = await source.GetDesiredAsync(CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("local-drop", results[0].FeedName);
    }

    [Fact]
    public async Task GetDesiredAsync_SetsSourceName_ToProvidedValue()
    {
        CreateNupkg("MyPlugin.1.0.0.nupkg");
        var source = new DirectoryNupkgDesiredSource("my-custom-source", _tempDir, ["*"], feedName: "local-drop");

        var results = await source.GetDesiredAsync(CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("my-custom-source", results[0].SourceName);
    }

    [Fact]
    public async Task GetDesiredAsync_FeedNameNull_SetsFeedNameToNull()
    {
        CreateNupkg("MyPlugin.1.0.0.nupkg");
        var source = new DirectoryNupkgDesiredSource("src-name", _tempDir, ["*"], feedName: null);

        var results = await source.GetDesiredAsync(CancellationToken.None);

        Assert.Single(results);
        Assert.Null(results[0].FeedName);
    }

    [Fact]
    public async Task GetDesiredAsync_ParsesPackageIdAndVersion()
    {
        CreateNupkg("Acme.Widgets.2.3.1.nupkg");
        var source = new DirectoryNupkgDesiredSource("src-name", _tempDir, ["*"], feedName: "local-drop");

        var results = await source.GetDesiredAsync(CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("Acme.Widgets", results[0].Id);
        Assert.Equal("2.3.1", results[0].VersionRange);
    }

    [Fact]
    public async Task GetDesiredAsync_SetsExactUpdatePolicy()
    {
        CreateNupkg("MyPlugin.1.0.0.nupkg");
        var source = new DirectoryNupkgDesiredSource("src-name", _tempDir, ["*"], feedName: "local-drop");

        var results = await source.GetDesiredAsync(CancellationToken.None);

        Assert.Single(results);
        Assert.Equal(PackageUpdatePolicy.Exact, results[0].UpdatePolicy);
    }

    [Fact]
    public async Task GetDesiredAsync_MultiplePackages_AllHaveSameFeedAndSource()
    {
        CreateNupkg("PluginA.1.0.0.nupkg");
        CreateNupkg("PluginB.2.0.0.nupkg");
        var source = new DirectoryNupkgDesiredSource("dir-src", _tempDir, ["*"], feedName: "feed-x");

        var results = await source.GetDesiredAsync(CancellationToken.None);

        Assert.Equal(2, results.Count);
        foreach (var result in results)
        {
            Assert.Equal("feed-x", result.FeedName);
            Assert.Equal("dir-src", result.SourceName);
        }
    }

    [Fact]
    public async Task GetDesiredAsync_WithoutIncludePatterns_ReturnsEmpty()
    {
        CreateNupkg("MyPlugin.1.0.0.nupkg");
        var source = new DirectoryNupkgDesiredSource("src-name", _tempDir, feedName: "local-drop");

        var results = await source.GetDesiredAsync(CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetDesiredAsync_PatternFilter_MatchesOnlySelectedPackages()
    {
        CreateNupkg("PluginA.1.0.0.nupkg");
        CreateNupkg("Other.Plugin.2.0.0.nupkg");
        var source = new DirectoryNupkgDesiredSource("src-name", _tempDir, ["Plugin*"], feedName: "local-drop");

        var results = await source.GetDesiredAsync(CancellationToken.None);

        var request = Assert.Single(results);
        Assert.Equal("PluginA", request.Id);
    }

    [Fact]
    public async Task GetDesiredAsync_EmptyDirectory_ReturnsEmpty()
    {
        var source = new DirectoryNupkgDesiredSource("src-name", _tempDir, ["*"], feedName: "local-drop");

        var results = await source.GetDesiredAsync(CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetDesiredAsync_NonExistentDirectory_ReturnsEmpty()
    {
        var nonExistent = Path.Combine(_tempDir, "does-not-exist");
        var source = new DirectoryNupkgDesiredSource("src-name", nonExistent, ["*"], feedName: "local-drop");

        var results = await source.GetDesiredAsync(CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetDesiredAsync_ExcludesPackageSuffixDirectories()
    {
        CreateNupkg("Good.Package.1.0.0.nupkg");
        System.IO.Directory.CreateDirectory(Path.Combine(_tempDir, "Folder.Package.2.0.0.nupkg"));
        var source = new DirectoryNupkgDesiredSource("src-name", _tempDir, ["*"]);

        var results = await source.GetDesiredAsync(CancellationToken.None);

        var request = Assert.Single(results);
        Assert.Equal("Good.Package", request.Id);
    }

    [Fact]
    public async Task GetDesiredAsync_UsesLegacyPlatformDefaultExtensionMatching()
    {
        const string uppercaseName = "Uppercase.Package.1.0.0.NUPKG";
        CreateNupkg(uppercaseName);
        var legacyEnumeratedUppercase = System.IO.Directory
            .EnumerateFiles(_tempDir, "*.nupkg")
            .Any(path => string.Equals(Path.GetFileName(path), uppercaseName,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        var source = new DirectoryNupkgDesiredSource("src-name", _tempDir, ["*"]);

        var results = await source.GetDesiredAsync(CancellationToken.None);

        Assert.Equal(legacyEnumeratedUppercase, results.Any(request => request.Id == "Uppercase.Package"));
    }

    [SupportedWindowsFact]
    [Trait("Platform", "Windows")]
    public async Task GetDesiredAsync_WithoutStabilityProbeIncludesMetadataVerifiedBusyRegularCandidateOnWindows()
    {
        const string fileName = "Busy.Package.1.0.0.nupkg";
        CreateNupkg(fileName);
        using var writer = new FileStream(
            Path.Combine(_tempDir, fileName), FileMode.Open, FileAccess.Write, FileShare.Write);
        var legacyEnumerationContainsCandidate = System.IO.Directory
            .EnumerateFiles(_tempDir, "*.nupkg")
            .Any(path => string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase));
        var source = new DirectoryNupkgDesiredSource("src-name", _tempDir, ["*"]);

        var results = await source.GetDesiredAsync(CancellationToken.None);

        Assert.Equal(legacyEnumerationContainsCandidate, results.Any(request => request.Id == "Busy.Package"));
    }

    [SupportedUnixFact]
    [Trait("Platform", "Unix")]
    public async Task GetDesiredAsync_RefusesPackageSuffixLinksBeforeStabilityCallback()
    {
        var target = Path.Combine(_tempDir, "target.bin");
        var linkedCandidate = Path.Combine(_tempDir, "Linked.Package.1.0.0.nupkg");
        File.WriteAllBytes(target, [0x50, 0x4B]);
        File.CreateSymbolicLink(linkedCandidate, target);
        var callbackCount = 0;
        var probe = new NupkgFileStabilityProbe(
            NullLogger<NupkgFileStabilityProbe>.Instance,
            maxAttempts: 2,
            retryDelay: TimeSpan.Zero,
            onBeforeRetryAsync: (_, _) =>
            {
                Interlocked.Increment(ref callbackCount);
                return Task.CompletedTask;
            });
        var source = new DirectoryNupkgDesiredSource("src-name", _tempDir, ["*"], stabilityProbe: probe);

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(
            () => source.GetDesiredAsync(CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
        Assert.Equal(0, Volatile.Read(ref callbackCount));
    }

    [Fact]
    [Trait("Platform", "Native")]
    public async Task GetDesiredAsync_RefusesAuthorityChangeDuringStabilityRetryBeforeAnotherRetry()
    {
        const string candidate = "Changing.Package.1.0.0.nupkg";
        CreateNupkg(candidate);
        var filePath = Path.Combine(_tempDir, candidate);
        var retryAttempts = new List<int>();
        var probe = new NupkgFileStabilityProbe(
            NullLogger<NupkgFileStabilityProbe>.Instance,
            maxAttempts: 3,
            retryDelay: TimeSpan.Zero,
            onBeforeRetryAsync: (attempt, _) =>
            {
                retryAttempts.Add(attempt);
                if (attempt == 1)
                {
                    // Turn the positively Unenrolled source directory into an ambiguous reserved
                    // authority namespace while the stability loop is suspended.
                    System.IO.Directory.CreateDirectory(Path.Combine(_tempDir, ".nuplane-store"));
                    using var writer = new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.Read);
                    writer.SetLength(8);
                    writer.Flush();
                }

                return Task.CompletedTask;
            });
        var source = new DirectoryNupkgDesiredSource("src-name", _tempDir, ["*"], stabilityProbe: probe);

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(
            () => source.GetDesiredAsync(CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
        // If the retry loop continued through another changed-length attempt, it would invoke the
        // callback again. Source replay ordering separately ensures refusal happens before sampling.
        Assert.Equal([1], retryAttempts);
    }

    [Fact]
    public async Task GetDesiredAsync_RefusesHardLinkedPackagesBeforeStabilityCallback()
    {
        var original = Path.Combine(_tempDir, "Hard.Package.1.0.0.nupkg");
        var alias = Path.Combine(_tempDir, "Hard.Package.1.0.1.nupkg");
        File.WriteAllBytes(original, [0x50, 0x4B]);
        CreateHardLink(original, alias);
        var callbackCount = 0;
        var probe = new NupkgFileStabilityProbe(
            NullLogger<NupkgFileStabilityProbe>.Instance,
            maxAttempts: 2,
            retryDelay: TimeSpan.Zero,
            onBeforeRetryAsync: (_, _) =>
            {
                Interlocked.Increment(ref callbackCount);
                return Task.CompletedTask;
            });
        var source = new DirectoryNupkgDesiredSource("src-name", _tempDir, ["*"], stabilityProbe: probe);

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(
            () => source.GetDesiredAsync(CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
        Assert.Equal(0, Volatile.Read(ref callbackCount));
    }

    [Fact]
    public async Task GetDesiredAsync_MultipleVersionsOfSamePackage_ReturnsHighestVersionOnly()
    {
        CreateNupkg("MyPlugin.1.0.0.nupkg");
        CreateNupkg("MyPlugin.1.0.1.nupkg");
        CreateNupkg("MyPlugin.2.0.0.nupkg");
        var source = new DirectoryNupkgDesiredSource("src-name", _tempDir, ["*"], feedName: "local-drop");

        var results = await source.GetDesiredAsync(CancellationToken.None);

        var request = Assert.Single(results);
        Assert.Equal("MyPlugin", request.Id);
        Assert.Equal("2.0.0", request.VersionRange);
    }

    [Theory]
    [InlineData("MyPlugin.1.9.0.nupkg", "MyPlugin.1.10.0.nupkg", "1.10.0")]
    [InlineData("MyPlugin.1.1.0-beta.2.nupkg", "MyPlugin.1.1.0-beta.10.nupkg", "1.1.0-beta.10")]
    [InlineData("MyPlugin.1.1.0-beta.10.nupkg", "MyPlugin.1.1.0.nupkg", "1.1.0")]
    [InlineData("MyPlugin.1.9.0.nupkg", "MyPlugin.2.0.0-alpha.nupkg", "2.0.0-alpha")]
    public async Task GetDesiredAsync_MultipleVersions_UsesNuGetSemanticVersionOrdering(
        string firstFileName,
        string secondFileName,
        string expectedVersion)
    {
        CreateNupkg(firstFileName);
        CreateNupkg(secondFileName);
        var source = new DirectoryNupkgDesiredSource("src-name", _tempDir, ["*"], feedName: "local-drop");

        var request = Assert.Single(await source.GetDesiredAsync(CancellationToken.None));

        Assert.Equal(expectedVersion, request.VersionRange);
    }

    [Fact]
    public async Task DesiredRole_EmitsHighestVersionOnly()
    {
        CreateNupkg("DesiredPlugin.1.0.0.nupkg");
        CreateNupkg("DesiredPlugin.1.5.0.nupkg");
        var source = new DirectoryNupkgDesiredSource("src-name", _tempDir, ["*"], feedName: "local-drop");

        var results = await source.GetDesiredAsync(CancellationToken.None);

        var request = Assert.Single(results);
        Assert.Equal("DesiredPlugin", request.Id);
        Assert.Equal("1.5.0", request.VersionRange);
    }

    [Fact]
    public async Task GetDesiredAsync_MultipleVersionsOfDifferentPackages_ReturnsHighestVersionPerPackage()
    {
        CreateNupkg("PluginA.1.0.0.nupkg");
        CreateNupkg("PluginA.1.2.0.nupkg");
        CreateNupkg("PluginB.3.0.0.nupkg");
        CreateNupkg("PluginB.2.0.0.nupkg");
        var source = new DirectoryNupkgDesiredSource("src-name", _tempDir, ["*"], feedName: "local-drop");

        var results = await source.GetDesiredAsync(CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal("PluginA", results[0].Id);
        Assert.Equal("1.2.0", results[0].VersionRange);
        Assert.Equal("PluginB", results[1].Id);
        Assert.Equal("3.0.0", results[1].VersionRange);
    }

    private void CreateNupkg(string fileName)
    {
        File.WriteAllBytes(Path.Combine(_tempDir, fileName), [0x50, 0x4B, 0x03, 0x04]);
    }

    private static void CreateHardLink(string existingPath, string newPath)
    {
        var result = OperatingSystem.IsWindows()
            ? CreateHardLinkWindows(newPath, existingPath, IntPtr.Zero) ? 0 : Marshal.GetLastPInvokeError()
            : CreateHardLinkUnix(existingPath, newPath);
        if (result != 0)
            throw new IOException($"The native hard-link operation failed with {Marshal.GetLastPInvokeError()}.");
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(string newFileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int CreateHardLinkUnix(string existingPath, string newPath);
}

public sealed class SupportedWindowsFactAttribute : FactAttribute
{
    public SupportedWindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Requires the Windows native filesystem implementation.";
    }
}

public sealed class SupportedUnixFactAttribute : FactAttribute
{
    public SupportedUnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
            Skip = "Requires the Unix native filesystem implementation.";
    }
}
