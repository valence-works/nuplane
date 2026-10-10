using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Builder;
using Nuplane.Integration.Tests.Fixtures;
using Nuplane.Sources;
using Nuplane.Sources.Directory.Builder;
using Nuplane.Store.Coordination;
using Nuplane.Tests.Shared;

namespace Nuplane.Integration.Tests.Restore;

[Trait("Platform", "Native")]
public sealed class NuplaneRestoreManifestAdmissionTests
{
    [Fact]
    public async Task DescribeDesiredAsync_AddNuplaneManifestAndDirectorySourcesShareCompleteRootAcrossAwaitedRead()
    {
        using var root = await CreateCompleteRootAsync();
        var manifestPath = Path.Combine(root.RootPath, "desired.json");
        WriteManifest(manifestPath, ("Manifest.Package", "1.0.0", "local-feed"));
        var directoryFeedPath = Path.Combine(root.RootPath, "directory-feed");
        Directory.CreateDirectory(directoryFeedPath);
        File.WriteAllBytes(Path.Combine(directoryFeedPath, "Directory.Package.2.0.0.nupkg"), [0x50, 0x4b]);

        var nativeReadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNativeRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new DesiredManifestReader(async cancellationToken =>
        {
            nativeReadStarted.TrySetResult();
            await releaseNativeRead.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
        });
        var options = Options(root, (builder, _) =>
        {
            builder.Services.RemoveAll<DesiredManifestReader>();
            builder.Services.AddSingleton(reader);
            builder.AddDirectoryFeed("local-feed", directoryFeedPath, feed =>
            {
                feed.Include("Directory.Package");
                feed.Watch = false;
            });
        });
        var competitor = new PackageStoreAdmission(root.Files, root.Registry, root.RootPath);

        var describe = NuplaneRestore.DescribeDesiredAsync(Configuration(manifestPath), options);
        try
        {
            await nativeReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await Assert.ThrowsAsync<PackageStoreAdmissionException>(async () =>
                await competitor.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Maintenance));
        }
        finally
        {
            releaseNativeRead.TrySetResult();
        }

        var description = await describe;

        Assert.Equal(
            ["Directory.Package", "Manifest.Package"],
            description.Requests.Select(static request => request.PackageId).Order(StringComparer.Ordinal));
        Assert.Empty(description.SourceErrors);
        Assert.Contains(description.Requests, static request => request.SourceName == "local-feed");
        Assert.Contains(description.Requests, request => request.SourceName == $"manifest:{manifestPath}");
        await using var afterDrain = await competitor.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Maintenance);
        Assert.Equal(PackageStoreAdmissionStatus.Enrolled, afterDrain.Status);
    }

    [Fact]
    public async Task DescribeDesiredAsync_UnenrolledManifestUsesAddNuplaneAndNativeReader()
    {
        using var store = new PackageStoreFixture();
        var manifestPath = Path.Combine(store.PackageInstallRoot, "unenrolled-desired.json");
        WriteManifest(manifestPath, ("Unenrolled.Package", "1.2.3", null));
        var nativeReadCount = 0;
        var reader = new DesiredManifestReader(_ =>
        {
            Interlocked.Increment(ref nativeReadCount);
            return Task.CompletedTask;
        });
        var options = new NuplaneRestoreOptions
        {
            InstallRoot = store.PackageInstallRoot,
            StateFilePath = store.StateFilePath,
            LockFilePath = Path.Combine(Path.GetDirectoryName(store.StateFilePath)!, "restore.lock.json"),
            ConfigureBuilder = (builder, _) =>
            {
                builder.Services.RemoveAll<DesiredManifestReader>();
                builder.Services.AddSingleton(reader);
            }
        };

        var description = await NuplaneRestore.DescribeDesiredAsync(Configuration(manifestPath), options);

        var request = Assert.Single(description.Requests);
        Assert.Equal("Unenrolled.Package", request.PackageId);
        Assert.Equal("1.2.3", request.PinnedVersion);
        Assert.Equal(1, Volatile.Read(ref nativeReadCount));
        Assert.Empty(description.SourceErrors);
    }

    [Fact]
    public async Task DescribeDesiredAsync_ForeignCompleteManifestRefusesBeforeNativeRead()
    {
        using var root = await CreateCompleteRootAsync();
        using var foreignRoot = await CreateCompleteRootAsync();
        var manifestPath = Path.Combine(foreignRoot.RootPath, "foreign-desired.json");
        WriteManifest(manifestPath, ("Foreign.Package", "1.0.0", null));
        var nativeReadCount = 0;
        var reader = new DesiredManifestReader(_ =>
        {
            Interlocked.Increment(ref nativeReadCount);
            return Task.CompletedTask;
        });
        var options = Options(root, (builder, _) =>
        {
            builder.Services.RemoveAll<DesiredManifestReader>();
            builder.Services.AddSingleton(reader);
        });

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            NuplaneRestore.DescribeDesiredAsync(Configuration(manifestPath), options));

        Assert.Equal(PackageStoreAdmissionReason.RootMismatch, refusal.Reason);
        Assert.Equal(0, Volatile.Read(ref nativeReadCount));
    }

    [Fact]
    public async Task DescribeDesiredAsync_IncompleteManifestRootRefusesBeforeNativeRead()
    {
        using var root = await CreateRootAsync(complete: false);
        var manifestPath = Path.Combine(root.RootPath, "incomplete-desired.json");
        WriteManifest(manifestPath, ("Incomplete.Package", "1.0.0", null));
        var nativeReadCount = 0;
        var reader = new DesiredManifestReader(_ =>
        {
            Interlocked.Increment(ref nativeReadCount);
            return Task.CompletedTask;
        });
        var options = Options(root, (builder, _) =>
        {
            builder.Services.RemoveAll<DesiredManifestReader>();
            builder.Services.AddSingleton(reader);
        });

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            NuplaneRestore.DescribeDesiredAsync(Configuration(manifestPath), options));

        Assert.Equal(PackageStoreAdmissionReason.IncompleteEnrollment, refusal.Reason);
        Assert.Equal(0, Volatile.Read(ref nativeReadCount));
    }

    [Fact]
    public async Task DescribeDesiredAsync_UnknownManifestRootRefusesBeforeNativeRead()
    {
        using var store = new PackageStoreFixture();
        var manifestPath = Path.Combine(store.PackageInstallRoot, "unknown-authority.json");
        WriteManifest(manifestPath, ("Unknown.Package", "1.0.0", null));
        Directory.CreateDirectory(Path.Combine(store.PackageInstallRoot, RootMembershipRegistry.ControlDirectoryName));
        var nativeReadCount = 0;
        var reader = new DesiredManifestReader(_ =>
        {
            Interlocked.Increment(ref nativeReadCount);
            return Task.CompletedTask;
        });
        var options = new NuplaneRestoreOptions
        {
            InstallRoot = store.PackageInstallRoot,
            StateFilePath = store.StateFilePath,
            LockFilePath = Path.Combine(Path.GetDirectoryName(store.StateFilePath)!, "restore.lock.json"),
            ConfigureBuilder = (builder, _) =>
            {
                builder.Services.RemoveAll<DesiredManifestReader>();
                builder.Services.AddSingleton(reader);
            }
        };

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            NuplaneRestore.DescribeDesiredAsync(Configuration(manifestPath), options));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
        Assert.Equal(0, Volatile.Read(ref nativeReadCount));
    }

    [Fact]
    public async Task DescribeDesiredAsync_DisabledManifestDoesNotReadConfiguredPath()
    {
        using var root = await CreateCompleteRootAsync();
        var feedDirectory = Directory.CreateDirectory(Path.Combine(root.RootPath, "disabled-manifest-feed")).FullName;
        var nativeReadCount = 0;
        var reader = new DesiredManifestReader(_ =>
        {
            Interlocked.Increment(ref nativeReadCount);
            return Task.CompletedTask;
        });
        var options = Options(root, (builder, _) =>
        {
            builder.Services.RemoveAll<DesiredManifestReader>();
            builder.Services.AddSingleton(reader);
            builder.AddDirectoryFeed("disabled-feed", feedDirectory, feed =>
            {
                feed.Include("No.Package");
                feed.Watch = false;
            });
        });

        var description = await NuplaneRestore.DescribeDesiredAsync(
            Configuration(Path.Combine(root.RootPath, "must-not-read.json"), manifestEnabled: false), options);

        Assert.Empty(description.Requests);
        Assert.Empty(description.SourceErrors);
        Assert.Equal(0, Volatile.Read(ref nativeReadCount));
    }

    [Fact]
    public async Task DescribeDesiredAsync_CancellationDrainsManifestBorrowAndRootOwner()
    {
        using var root = await CreateCompleteRootAsync();
        var manifestPath = Path.Combine(root.RootPath, "cancel-desired.json");
        WriteManifest(manifestPath, ("Cancellation.Package", "1.0.0", null));
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var reader = new DesiredManifestReader(async token =>
        {
            readStarted.TrySetResult();
            await releaseRead.Task.WaitAsync(token);
        });
        var options = Options(root, (builder, _) =>
        {
            builder.Services.RemoveAll<DesiredManifestReader>();
            builder.Services.AddSingleton(reader);
        });
        var competitor = new PackageStoreAdmission(root.Files, root.Registry, root.RootPath);
        var describe = NuplaneRestore.DescribeDesiredAsync(
            Configuration(manifestPath), options, cancellation.Token);

        try
        {
            await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => describe);

            await using var afterDrain = await competitor.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Maintenance);
            Assert.Equal(PackageStoreAdmissionStatus.Enrolled, afterDrain.Status);
        }
        finally
        {
            releaseRead.TrySetResult();
            if (!describe.IsCompleted)
            {
                try { await describe; }
                catch (OperationCanceledException) { }
            }
        }
    }

    [Fact]
    public async Task RestoreAsync_PinnedPreflightReadsEnabledManifestAndDirectorySourcesThroughAddNuplane()
    {
        using var root = await CreateCompleteRootAsync();
        var manifestPath = Path.Combine(root.RootPath, "pinned-desired.json");
        var feedDirectory = Path.Combine(root.RootPath, "pinned-feed");
        Directory.CreateDirectory(feedDirectory);
        HostFreeRestoreTestSupport.WriteNupkg(feedDirectory, prefix: "Directory");
        var observedFeedDirectory = Path.Combine(root.RootPath, "pinned-probe-feed");
        Directory.CreateDirectory(observedFeedDirectory);
        HostFreeRestoreTestSupport.WriteNupkg(observedFeedDirectory, prefix: "Probe");
        WriteManifest(
            manifestPath,
            ("Manifest.Package", "1.0.0", "local-feed"));

        var nativeReadCount = 0;
        var directoryProbeCount = 0;
        var directoryProbe = new Nuplane.Sources.Directory.NupkgFileStabilityProbe(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Nuplane.Sources.Directory.NupkgFileStabilityProbe>.Instance,
            maxAttempts: 2,
            retryDelay: TimeSpan.Zero,
            onBeforeRetryAsync: (_, _) =>
            {
                Interlocked.Increment(ref directoryProbeCount);
                return Task.CompletedTask;
            });
        var observedDirectorySource = new Nuplane.Sources.Directory.DirectoryNupkgDesiredSource(
            "pinned-directory-probe",
            observedFeedDirectory,
            ["Probe.Package"],
            feedName: "local-feed",
            stabilityProbe: directoryProbe);
        var options = Options(root, (builder, _) =>
        {
            builder.Services.RemoveAll<DesiredManifestReader>();
            builder.Services.AddSingleton(new DesiredManifestReader(_ =>
            {
                Interlocked.Increment(ref nativeReadCount);
                return Task.CompletedTask;
            }));
            builder.Services.AddSingleton<IDesiredPackageSource>(observedDirectorySource);
            builder.AddDirectoryFeed("local-feed", feedDirectory, feed =>
            {
                feed.Include("Directory.Package");
                feed.Watch = false;
            });
        });
        options.RequirePinnedVersions = true;

        var result = await NuplaneRestore.RestoreAsync(Configuration(manifestPath, includeUnpinnedRequest: true), options);

        // Both local sources are read under the preflight borrow. This deliberately includes one
        // unrelated floating setup-feed request so the pinned preflight returns before the local
        // package resolver, whose scoped directory capability is a separate slice.
        Assert.Equal(1, Volatile.Read(ref nativeReadCount));
        Assert.True(Volatile.Read(ref directoryProbeCount) > 0,
            "The enabled built-in directory feed must finish a native stability probe during pinned preflight.");
        Assert.True(result.Skipped);
        Assert.Equal(NuplaneRestoreSkipReason.UnpinnedRequests, result.SkipReason);
        Assert.Equal("Unpinned.Package", Assert.Single(result.UnpinnedRequests).PackageId);
        Assert.Empty(result.FailedPackages);
    }

    private static NuplaneRestoreOptions Options(
        RootMembershipProcessFixture root,
        Action<NuplaneBuilder, IConfiguration> configureBuilder) => new()
    {
        InstallRoot = root.RootPath,
        StateFilePath = root.StateFilePath,
        LockFilePath = Path.Combine(Path.GetDirectoryName(root.StateFilePath)!, "restore.lock.json"),
        ConfigureBuilder = configureBuilder
    };

    private static IConfiguration Configuration(
        string manifestPath,
        bool includeUnpinnedRequest = false,
        bool manifestEnabled = true)
    {
        var values = new Dictionary<string, string?>
        {
            ["Nuplane:Reconciliation:PollInterval"] = "00:05:00",
            ["Nuplane:Convergence:Manifest:Enabled"] = manifestEnabled ? "true" : "false",
            ["Nuplane:Convergence:Manifest:Path"] = manifestPath,
            ["Nuplane:Convergence:Manifest:SchemaVersion"] = "1.0"
        };
        if (includeUnpinnedRequest)
        {
            values["Nuplane:Setup:Feeds:preflight:ServiceIndex"] = "https://packages.example.com/v3/index.json";
            values["Nuplane:Setup:Feeds:preflight:IncludePatterns:0"] = "Unpinned.Package";
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static async Task<RootMembershipProcessFixture> CreateRootAsync(bool complete)
    {
        var fixture = await RootMembershipProcessFixture.CreateAsync(RootMembershipPriorShape.Acknowledged);
        if (!complete)
            return fixture;

        try
        {
            await fixture.Registry.CompleteEnrollmentAsync(fixture.Root, fixture.Initial.RootIdentity,
                fixture.Initial.EnrollmentEpoch, quiescentCutoverConfirmed: true, cancellationToken: CancellationToken.None);
            return fixture;
        }
        catch
        {
            fixture.Dispose();
            throw;
        }
    }

    private static Task<RootMembershipProcessFixture> CreateCompleteRootAsync() => CreateRootAsync(complete: true);

    private static void WriteManifest(string path, params (string Id, string Version, string? SourceHint)[] packages)
    {
        var json = JsonSerializer.Serialize(new
        {
            SchemaVersion = "1.0",
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Packages = packages.Select(static package => new
            {
                Id = package.Id,
                Version = package.Version,
                SourceHint = package.SourceHint
            })
        });
        File.WriteAllText(path, json);
    }

}
