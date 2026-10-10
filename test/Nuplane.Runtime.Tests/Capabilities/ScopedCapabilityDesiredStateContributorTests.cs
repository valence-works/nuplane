using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Capabilities;
using Nuplane.Feeds;
using Nuplane.Metadata;
using Nuplane.Reconciliation.Configuration;
using Nuplane.Runtime.Tests.TestSupport;

namespace Nuplane.Runtime.Tests.Capabilities;

public sealed class ScopedCapabilityDesiredStateContributorTests
{
    [Fact]
    public async Task ContributeAsync_NativeMetadataMemo_RequiresTheSameLiveOwnerAndNeverServesLegacyReads()
    {
        using var fixture = await EnrolledEmptyPackageStoreFixture.CreateAsync();
        var context = PrepareContext(fixture);
        var reader = new CountingScopedReader();
        var contributor = CreateContributor(reader);
        await using var operation = await fixture.Admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Reconciliation);
        using var borrow = operation.Owner!.Borrow();

        var first = await contributor.ContributeAsync(context, borrow, CancellationToken.None);
        var repeated = await contributor.ContributeAsync(context, borrow, CancellationToken.None);

        Assert.Equal("Acme.Engine", Assert.Single(first.Requests).Request.Id);
        Assert.Equal("Acme.Engine", Assert.Single(repeated.Requests).Request.Id);
        Assert.Equal(1, reader.ScopedReads);
        borrow.Dispose();
        var expired = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            contributor.ContributeAsync(context, borrow, CancellationToken.None));
        Assert.Equal(PackageStoreAdmissionReason.ExpiredScope, expired.Reason);
        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            contributor.ContributeAsync(context, CancellationToken.None));
        Assert.Equal(1, reader.ScopedReads);
        Assert.Equal(1, reader.LegacyReads);
    }

    [Fact]
    public async Task ContributeAsync_SameCorrelationWithANewOwner_ReadsFreshNativeMetadata()
    {
        using var fixture = await EnrolledEmptyPackageStoreFixture.CreateAsync();
        var context = PrepareContext(fixture);
        var reader = new CountingScopedReader();
        var contributor = CreateContributor(reader);
        await using (var firstOperation = await fixture.Admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Reconciliation))
        {
            using var firstBorrow = firstOperation.Owner!.Borrow();
            var first = await contributor.ContributeAsync(context, firstBorrow, CancellationToken.None);
            Assert.Equal("Acme.Engine", Assert.Single(first.Requests).Request.Id);
        }

        // Isolated fixture mutation between operations proves the old owner's memo cannot grant access.
        File.WriteAllText(Path.Combine(context.ResolvedPackages[0].InstallPath, NuplanePackageMetadataReader.MetadataFileName),
            InstalledPackageStore.CapabilityMetadata("engine", ("chosen", "Acme.Replacement", "[1.0.0]")));
        await using var secondOperation = await fixture.Admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Reconciliation);
        using var secondBorrow = secondOperation.Owner!.Borrow();
        var second = await contributor.ContributeAsync(context, secondBorrow, CancellationToken.None);

        Assert.Equal("Acme.Replacement", Assert.Single(second.Requests).Request.Id);
        Assert.Equal(2, reader.ScopedReads);
        Assert.Equal(0, reader.LegacyReads);
    }

    [Fact]
    public async Task ContributeAsync_UnscopedMetadataReader_RefusesBeforeItsCallback()
    {
        using var fixture = await EnrolledEmptyPackageStoreFixture.CreateAsync();
        var context = PrepareContext(fixture);
        var reader = new RecordingPackageMetadataReader();
        var contributor = CreateContributor(reader);
        await using var operation = await fixture.Admission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Reconciliation);
        using var borrow = operation.Owner!.Borrow();

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            contributor.ContributeAsync(context, borrow, CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, refusal.Reason);
        Assert.Empty(reader.Reads);
    }

    private static CapabilityDesiredStateContributor CreateContributor(IPackageMetadataReader reader)
    {
        var options = new CapabilityOptions();
        options.Selections["engine"] = new CapabilitySelection { Options = ["chosen"] };
        return new CapabilityDesiredStateContributor(Options.Create(options), Options.Create(new ReconciliationOptions()),
            new CapabilityContributionLedger(), new RecordingReconciliationLogger(), reader);
    }

    private static DesiredStateContributionContext PrepareContext(EnrolledEmptyPackageStoreFixture fixture)
    {
        var path = PackageInstallStore.GetInstallDirectory(fixture.InstallRoot, "feed", "Acme.Module", "1.0.0");
        // Test-owned installation setup; the acquisition production path is verified separately.
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, PackageInstallStore.CompletionMarkerFileName), []);
        File.WriteAllText(Path.Combine(path, NuplanePackageMetadataReader.MetadataFileName),
            InstalledPackageStore.CapabilityMetadata("engine", ("chosen", "Acme.Engine", "[1.0.0]")));
        return new DesiredStateContributionContext("reused-correlation", [],
            [new ResolvedPackage("Acme.Module", "1.0.0", "feed", path, DateTimeOffset.UnixEpoch, "source")], []);
    }

    private sealed class CountingScopedReader : IScopedPackageMetadataReader
    {
        private readonly NuplanePackageMetadataReader _reader = new();
        internal int ScopedReads { get; private set; }
        internal int LegacyReads { get; private set; }

        public NuplanePackageMetadataReadResult Read(string packageId, string version, string installPath)
        {
            LegacyReads++;
            return _reader.Read(packageId, version, installPath);
        }

        public NuplanePackageMetadataReadResult Read(string packageId, string version, string installPath,
            PackageStoreOperationBorrow borrow)
        {
            ScopedReads++;
            return ((IScopedPackageMetadataReader)_reader).Read(packageId, version, installPath, borrow);
        }
    }
}
