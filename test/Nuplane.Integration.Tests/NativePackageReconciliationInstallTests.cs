using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Feeds;
using Nuplane.Feeds.Configuration;
using Nuplane.Feeds.Credentials;
using Nuplane.Loading;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.LockFile;
using Nuplane.Reconciliation.Models;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.GraphUseRecords;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;

namespace Nuplane.Integration.Tests;

public sealed partial class OverlappingPackageGraphProtectionTests
{
    [Fact]
    public async Task Reconciliation_AcquiresMissingAuthenticatedPackage_NativelyProtectsAndLoadsExactGraph()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        var packageBytes = CreatePackageArchive("Root.Second", "3.0.0", "Shared.Dependency", "2.1.0");
        var expectedHash = "sha512:" + Convert.ToBase64String(SHA512.HashData(packageBytes));
        const string secret = "native-reconciliation-loopback-secret";
        var variableName = "NUPLANE_TEST_NATIVE_RECONCILIATION_" + Guid.NewGuid().ToString("N");
        var request = new PackageRequest("Root.Second", "[3.0.0]", "feed", PackageUpdatePolicy.Exact, "native-install-test");

        await using var feed = new AuthenticatedLoopbackNuGetFeed("Root.Second", "3.0.0", packageBytes,
            secret, new AuthenticatedLoopbackNuGetFeed.Dependency("Shared.Dependency", "2.1.0"));
        using var secretScope = new EnvironmentVariableScope(variableName, secret);
        var remoteFeed = new FeedDefinition("feed", feed.ServiceIndexUri, "secrets://env/" + variableName);
        await using var provider = fixture.CreateProvider("member-b", desiredRequests: [request], remoteFeed: remoteFeed);

        Assert.IsType<MultiFeedPackageResolver>(provider.GetRequiredService<IPackageResolver>());
        Assert.IsType<NuGetRemotePackageAcquirer>(provider.GetRequiredService<IRemotePackageAcquirer>());

        var installPath = PackageInstallStore.GetInstallDirectory(fixture.RootPath, "feed", "Root.Second", "3.0.0");
        var installRelativePath = Path.GetRelativePath(fixture.RootPath, installPath)
            .Replace(Path.DirectorySeparatorChar, '/');
        var expectedAssemblyPath = Path.Combine(installPath, "lib", "net10.0", "Root.Second.dll");
        var stateRegistry = Assert.IsAssignableFrom<ICoordinatedStoreRegistry>(provider.GetRequiredService<IStoreRegistry>());
        var admission = provider.GetRequiredService<IPackageStoreAdmission>();

        await using (var initialOperation = await admission.AcquireConfiguredRootOperationAsync(
                         PackageStoreAdmissionKind.Reconciliation))
        {
            using var initialBorrow = initialOperation.Owner!.Borrow();
            var before = await stateRegistry.ReadCoordinatedStateAsync(initialBorrow, CancellationToken.None);
            Assert.Equal("1.0.0", before.ActiveVersionById["Root.Second"]);
            Assert.Equal("1.0.0", before.LastKnownGoodById["Root.Second"]);
            Assert.False(PackageInstallStore.IsInstalled(installPath, initialBorrow));
        }

        var lockPath = Path.Combine(Path.GetDirectoryName(fixture.StatePaths["member-b"])!, "nuplane.lock.json");
        var lockStore = new LockFileStore(Options.Create(new LockFileOptions { Path = lockPath }));
        await lockStore.WriteAsync(new PackageLockFile("2.0", DateTimeOffset.UtcNow,
            [new PackageLockEntry("Root.Second", "3.0.0", "feed", expectedHash, DateTimeOffset.UtcNow)]),
            CancellationToken.None);

        try
        {
            var cycle = await provider.GetRequiredService<IReconciliationService>()
                .TriggerAsync(ReconciliationTrigger.Manual("native-package-install"), CancellationToken.None);

            Assert.False(cycle.IsDegraded);
            var updated = Assert.Single(cycle.ChangeSet.Updated);
            Assert.Equal("Root.Second", updated.Id);
            Assert.Equal("3.0.0", updated.Version);
            Assert.Equal(1, feed.PackageDownloads);
            Assert.Equal(2, feed.AuthorizedRequests);
            Assert.Equal(0, feed.UnauthorizedRequests);
            Assert.Equal(2, feed.Requests);

            string activeGenerationId;
            await using (var verificationOperation = await admission.AcquireConfiguredRootOperationAsync(
                             PackageStoreAdmissionKind.Reconciliation))
            {
                using var borrow = verificationOperation.Owner!.Borrow();
                var state = await stateRegistry.ReadCoordinatedStateAsync(borrow, CancellationToken.None);
                Assert.Equal("3.0.0", state.ActiveVersionById["Root.Second"]);
                Assert.Equal("3.0.0", state.LastKnownGoodById["Root.Second"]);
                Assert.True(PackageInstallStore.IsInstalled(installPath, borrow));
                Assert.Equal(expectedHash,
                    await PackageInstallStore.ReadContentHashAsync(installPath, borrow, CancellationToken.None));

                var activeRecord = Assert.Single(state.ActiveGraphsByIdNormalized.Values);
                Assert.Equal(GraphActivationStatus.Active, activeRecord.Status);
                Assert.Equal("3.0.0", activeRecord.NodeVersionsByPackageId!["Root.Second"]);
                Assert.Equal("2.1.0", activeRecord.NodeVersionsByPackageId["Shared.Dependency"]);
                Assert.Equal("Root.Second", Assert.Single(activeRecord.RootPackageIds));
                Assert.Equal(new HashSet<string>(["Root.Second", "Shared.Dependency"], StringComparer.OrdinalIgnoreCase),
                    activeRecord.NodePackageIds.ToHashSet(StringComparer.OrdinalIgnoreCase));

                var protectedGraph = Assert.Single(state.ProtectionRecord!.ActiveClosure.Graphs!);
                Assert.Equal(activeRecord.GenerationId, protectedGraph.GenerationId);
                Assert.Equal(PackageProtectionClosureKnowledge.Known, state.ProtectionRecord.ActiveClosure.Knowledge);
                var selectedRequest = Assert.Single(protectedGraph.RequestedRoots).Request;
                Assert.Equal(request, selectedRequest);
                var protectedNodes = protectedGraph.Nodes.ToDictionary(static node => node.Install.PackageId,
                    StringComparer.OrdinalIgnoreCase);
                Assert.Equal("3.0.0", protectedNodes["Root.Second"].Install.Version);
                Assert.Equal(installRelativePath, protectedNodes["Root.Second"].Install.RootRelativeInstallPath);
                Assert.Equal(expectedHash, protectedNodes["Root.Second"].Install.VerifiedArchiveHash);
                Assert.Equal("2.1.0", protectedNodes["Shared.Dependency"].Install.Version);
                Assert.Equal("Shared.Dependency", Assert.Single(protectedGraph.Edges).RequestedPackageId);
                _ = PersistedStoreStateGraphVerifier.Verify(state);

                var assemblyContext = Assert.Single(AssemblyLoadContext.All, context => context.IsCollectible &&
                    context.Assemblies.Any(assembly => PathEquals(assembly.Location, expectedAssemblyPath)));
                Assert.Contains(assemblyContext.Assemblies, assembly =>
                    assembly.GetName().Name == "Root.Second" && PathEquals(assembly.Location, expectedAssemblyPath));
                Assert.Contains(assemblyContext.Assemblies, assembly =>
                    assembly.GetName().Name == "Shared.Dependency" &&
                    PathEquals(assembly.Location, Path.Combine(fixture.RootPath, "feed", "Shared.Dependency", "2.1.0", "Shared.Dependency.dll")));

                activeGenerationId = activeRecord.GenerationId;
            }

            var liveRecords = await InspectGraphUseRecordsAsync(admission, fixture);
            Assert.Contains(liveRecords.Entries, entry => entry.OwnershipState == GraphUseRecordOwnershipState.Live &&
                entry.Record.GraphSnapshot.GenerationId == activeGenerationId);
        }
        finally
        {
            provider.GetRequiredService<PackageLoader>().UnloadContextsNotActive(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            await fixture.WaitForGraphUseReleaseAsync(admission);
        }
    }

    private static byte[] CreatePackageArchive(string packageId, string version, string dependencyId, string dependencyVersion)
    {
        using var assemblyFixture = new PackageStoreFixture();
        var assemblyDirectory = assemblyFixture.CreateDirectory("archive-builder");
        var assemblyPath = Path.Combine(assemblyDirectory, packageId + ".dll");
        EmitPackageAssembly(packageId, assemblyPath);

        var nuspec = $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{{packageId}}</id>
                <version>{{version}}</version>
                <authors>test</authors>
                <description>Native reconciliation integration package.</description>
                <dependencies><dependency id="{{dependencyId}}" version="[{{dependencyVersion}}]" /></dependencies>
              </metadata>
            </package>
            """;

        using var bytes = new MemoryStream();
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, packageId + ".nuspec", Encoding.UTF8.GetBytes(nuspec));
            WriteEntry(archive, "lib/net10.0/" + packageId + ".dll", File.ReadAllBytes(assemblyPath));
            WriteEntry(archive, Nuplane.Metadata.NuplanePackageMetadataReader.MetadataFileName,
                "{\"schemaVersion\":2,\"loading\":{\"loadMode\":\"Collectible\",\"scope\":\"DependencyClosure\"}}"u8.ToArray());
        }

        return bytes.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string path, byte[] contents)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        using var output = entry.Open();
        output.Write(contents);
    }

    private static void EmitPackageAssembly(string assemblyName, string assemblyPath)
    {
        var assembly = new PersistedAssemblyBuilder(
            new AssemblyName(assemblyName) { Version = new Version(1, 0, 0, 0) }, typeof(object).Assembly);
        var module = assembly.DefineDynamicModule(assemblyName);
        module.DefineType($"{assemblyName}.Marker", TypeAttributes.Public | TypeAttributes.Class).CreateType();
        assembly.Save(assemblyPath);
    }

    private static bool PathEquals(string left, string right)
        => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private sealed class AuthenticatedLoopbackNuGetFeed : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _serveLoop;
        private readonly byte[] _packageBytes;
        private readonly string _packageId;
        private readonly string _version;
        private readonly string _expectedAuthorization;
        private readonly string _baseAddress;
        private int _requests;
        private int _authorizedRequests;
        private int _unauthorizedRequests;
        private int _packageDownloads;

        internal int Requests => Volatile.Read(ref _requests);
        internal int AuthorizedRequests => Volatile.Read(ref _authorizedRequests);
        internal int UnauthorizedRequests => Volatile.Read(ref _unauthorizedRequests);
        internal int PackageDownloads => Volatile.Read(ref _packageDownloads);
        internal Uri ServiceIndexUri => new(new Uri(_baseAddress), "v3/index.json");

        internal AuthenticatedLoopbackNuGetFeed(string packageId, string version, byte[] packageBytes,
            string token, Dependency dependency)
        {
            _packageId = packageId;
            _version = version;
            _packageBytes = packageBytes;
            _expectedAuthorization = "Basic " + Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{FeedCredential.TokenUserName}:{token}"));

            var port = GetFreePort();
            _baseAddress = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(_baseAddress);
            _listener.Start();
            _serveLoop = Task.Run(() => ServeAsync(dependency));
        }

        public async ValueTask DisposeAsync()
        {
            _shutdown.Cancel();
            _listener.Stop();
            try
            {
                await _serveLoop;
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
            finally
            {
                _listener.Close();
                _shutdown.Dispose();
            }
        }

        private async Task ServeAsync(Dependency dependency)
        {
            while (!_shutdown.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (HttpListenerException) when (_shutdown.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested)
                {
                    break;
                }

                await HandleAsync(context, dependency);
            }
        }

        private async Task HandleAsync(HttpListenerContext context, Dependency dependency)
        {
            Interlocked.Increment(ref _requests);
            if (!string.Equals(context.Request.Headers["Authorization"], _expectedAuthorization, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _unauthorizedRequests);
                context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                context.Response.AddHeader("WWW-Authenticate", "Basic realm=\"nuplane-test\"");
                context.Response.Close();
                return;
            }

            Interlocked.Increment(ref _authorizedRequests);
            var requestPath = context.Request.Url?.AbsolutePath ?? string.Empty;
            if (requestPath.Equals("/v3/index.json", StringComparison.OrdinalIgnoreCase))
            {
                var packageBaseAddress = new Uri(new Uri(_baseAddress), "flatcontainer/").AbsoluteUri;
                await WriteJsonAsync(context.Response, $$"""
                    {
                      "version": "3.0.0",
                      "resources": [
                        { "@id": "{{packageBaseAddress}}", "@type": "PackageBaseAddress/3.0.0" }
                      ]
                    }
                    """);
                return;
            }

            var lowerPackageId = _packageId.ToLowerInvariant();
            var lowerVersion = _version.ToLowerInvariant();
            var expectedPackagePath = $"/flatcontainer/{lowerPackageId}/{lowerVersion}/{lowerPackageId}.{lowerVersion}.nupkg";
            if (requestPath.Equals(expectedPackagePath, StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref _packageDownloads);
                context.Response.StatusCode = (int)HttpStatusCode.OK;
                context.Response.ContentType = "application/octet-stream";
                context.Response.ContentLength64 = _packageBytes.Length;
                await context.Response.OutputStream.WriteAsync(_packageBytes);
                context.Response.Close();
                return;
            }

            if (requestPath.StartsWith("/flatcontainer/", StringComparison.OrdinalIgnoreCase))
            {
                var sharedId = dependency.PackageId.ToLowerInvariant();
                if (requestPath.Equals($"/flatcontainer/{sharedId}/index.json", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteJsonAsync(context.Response, $$"""
                        { "versions": ["{{dependency.Version}}"] }
                        """);
                    return;
                }

                if (requestPath.Equals($"/flatcontainer/{sharedId}/{dependency.Version}/{sharedId}.{dependency.Version}.nupkg",
                        StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    context.Response.Close();
                    return;
                }
            }

            context.Response.StatusCode = (int)HttpStatusCode.NotFound;
            context.Response.Close();
        }

        private static async Task WriteJsonAsync(HttpListenerResponse response, string json)
        {
            var contents = Encoding.UTF8.GetBytes(json);
            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = "application/json";
            response.ContentLength64 = contents.Length;
            await response.OutputStream.WriteAsync(contents);
            response.Close();
        }

        private static int GetFreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        internal sealed record Dependency(string PackageId, string Version);
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        internal EnvironmentVariableScope(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}
