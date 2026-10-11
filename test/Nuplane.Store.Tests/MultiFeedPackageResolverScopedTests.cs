using Microsoft.Extensions.Options;
using NSubstitute;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Feeds;
using Nuplane.Feeds.Configuration;
using Nuplane.Feeds.Policy;
using Nuplane.Feeds.Versioning;
using Nuplane.Store.Coordination;
using Nuplane.Store.Tests.Coordination;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests;

[Trait("Platform", "Native")]
public sealed class MultiFeedPackageResolverScopedTests
{
    [SupportedPhysicalStoreFact]
    public async Task ScopedResolutionPassesExactBorrowAndUsesItForCompletionAndHashReads()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var expectedHash = "sha512:" + Convert.ToBase64String(Enumerable.Range(0, 64).Select(static value => (byte)value).ToArray());
        await File.WriteAllTextAsync(Path.Combine(context.SharedInstallPath, PackageInstallStore.ContentHashFileName), expectedHash);

        await using var admitted = await CreateAdmission(context).AcquireForInstallPathsAsync(
            [context.SharedInstallPath], PackageStoreAdmissionKind.Loading);
        using var borrow = admitted.BorrowFor(context.SharedInstallPath);
        var options = OptionsFor(new FeedDefinition("feed", new Uri("https://feed.example/v3/index.json")));
        var acquirer = new RecordingScopedAcquirer(context.SharedInstallPath);
        var resolver = CreateResolver(options, acquirer);

        var result = await resolver.ResolveAsync(Request("feed"), borrow, CancellationToken.None);

        Assert.Equal(context.SharedInstallPath, result.InstallPath);
        Assert.Equal(expectedHash, result.PackageContentHash);
        Assert.Equal(1, acquirer.ScopedCallCount);
        Assert.Equal(0, acquirer.UnscopedCallCount);
        Assert.Same(borrow, acquirer.LastBorrow);
    }

    [SupportedPhysicalStoreFact]
    public async Task ScopedResolutionRefusesLocalFeedBeforeIndexingOrFailingOver()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        await using var admitted = await CreateAdmission(context).AcquireForInstallPathsAsync(
            [context.SharedInstallPath], PackageStoreAdmissionKind.Loading);
        using var borrow = admitted.BorrowFor(context.SharedInstallPath);
        var localFeedPath = context.Fixture.CreateDirectory("local-feed");
        var options = OptionsFor(
            new FeedDefinition("local", new Uri(localFeedPath + Path.DirectorySeparatorChar)),
            new FeedDefinition("remote", new Uri("https://feed.example/v3/index.json")));
        options.RemoteFallbackMode = RemoteFallbackMode.Always;
        var enumerator = Substitute.For<IFeedVersionEnumerator>();
        var acquirer = new RecordingScopedAcquirer(context.SharedInstallPath);
        var resolver = CreateResolver(options, acquirer, enumerator);

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            resolver.ResolveAsync(Request(feedName: null), borrow, CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.UnsupportedFilesystem, refusal.Reason);
        Assert.Equal(0, acquirer.ScopedCallCount);
        Assert.Equal(0, acquirer.UnscopedCallCount);
        await enumerator.DidNotReceive().EnumerateVersionsAsync(
            Arg.Any<FeedDefinition>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [SupportedPhysicalStoreFact]
    public async Task ScopedAdmissionRefusalFromAcquirerDoesNotFallBackToAnotherFeed()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        await using var admitted = await CreateAdmission(context).AcquireForInstallPathsAsync(
            [context.SharedInstallPath], PackageStoreAdmissionKind.Loading);
        using var borrow = admitted.BorrowFor(context.SharedInstallPath);
        var options = OptionsFor(
            new FeedDefinition("first", new Uri("https://first.example/v3/index.json")),
            new FeedDefinition("second", new Uri("https://second.example/v3/index.json")));
        options.RemoteFallbackMode = RemoteFallbackMode.Always;
        options.SetPriority("first", 1);
        options.SetPriority("second", 2);
        var refusal = new PackageStoreAdmissionException(
            PackageStoreAdmissionReason.UnknownAuthority, "Native package-store authority changed.", borrow.Root);
        var acquirer = new RecordingScopedAcquirer(context.SharedInstallPath, refusal);
        var resolver = CreateResolver(options, acquirer);

        var actual = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            resolver.ResolveAsync(Request(feedName: null), borrow, CancellationToken.None));

        Assert.Same(refusal, actual);
        Assert.Same(borrow, acquirer.LastBorrow);
        Assert.Equal(new[] { "first" }, acquirer.ScopedFeedNames);
        Assert.Equal(0, acquirer.UnscopedCallCount);
    }

    [SupportedPhysicalStoreFact]
    public async Task ScopedAdmissionRefusalDuringVersionEnumerationDoesNotFallBackToAnotherFeed()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        await using var admitted = await CreateAdmission(context).AcquireForInstallPathsAsync(
            [context.SharedInstallPath], PackageStoreAdmissionKind.Loading);
        using var borrow = admitted.BorrowFor(context.SharedInstallPath);
        var options = OptionsFor(
            new FeedDefinition("first", new Uri("https://first.example/v3/index.json")),
            new FeedDefinition("second", new Uri("https://second.example/v3/index.json")));
        options.SetPriority("first", 1);
        options.SetPriority("second", 2);
        var refusal = new PackageStoreAdmissionException(
            PackageStoreAdmissionReason.UnknownAuthority, "Native package-store authority changed.", borrow.Root);
        var enumerator = Substitute.For<IFeedVersionEnumerator>();
        enumerator.EnumerateVersionsAsync(Arg.Any<FeedDefinition>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<PackageVersionList>(refusal));
        var acquirer = new RecordingScopedAcquirer(context.SharedInstallPath);
        var resolver = CreateResolver(options, acquirer, enumerator);
        var request = new PackageRequest("Shared.Dependency", "[2.0.0,)", null,
            PackageUpdatePolicy.Range, "scoped-test");

        var actual = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            resolver.ResolveAsync(request, borrow, CancellationToken.None));

        Assert.Same(refusal, actual);
        await enumerator.Received(1).EnumerateVersionsAsync(
            Arg.Is<FeedDefinition>(feed => feed.Name == "first"), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await enumerator.DidNotReceive().EnumerateVersionsAsync(
            Arg.Is<FeedDefinition>(feed => feed.Name == "second"), Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Equal(0, acquirer.ScopedCallCount);
        Assert.Equal(0, acquirer.UnscopedCallCount);
    }

    [SupportedPhysicalStoreFact]
    public async Task ScopedResolutionRefusesAnAcquirerWithoutTheScopedContract()
    {
        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        await using var admitted = await CreateAdmission(context).AcquireForInstallPathsAsync(
            [context.SharedInstallPath], PackageStoreAdmissionKind.Loading);
        using var borrow = admitted.BorrowFor(context.SharedInstallPath);
        var options = OptionsFor(new FeedDefinition("feed", new Uri("https://feed.example/v3/index.json")));
        var acquirer = Substitute.For<IRemotePackageAcquirer>();
        acquirer.AcquireAsync(Arg.Any<FeedDefinition>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(context.SharedInstallPath);
        var resolver = CreateResolver(options, acquirer);

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            resolver.ResolveAsync(Request("feed"), borrow, CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, refusal.Reason);
        await acquirer.DidNotReceive().AcquireAsync(
            Arg.Any<FeedDefinition>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static PackageRequest Request(string? feedName)
        => new("Shared.Dependency", "[2.1.0]", feedName, PackageUpdatePolicy.Exact, "scoped-test");

    private static FeedResolutionOptions OptionsFor(params FeedDefinition[] feeds)
    {
        var options = new FeedResolutionOptions { RemoteFallbackMode = RemoteFallbackMode.Always };
        options.Feeds.AddRange(feeds);
        return options;
    }

    private static PackageStoreAdmission CreateAdmission(RootMembershipProtectionVerificationTests.Context context)
        => new(context.Files, context.Registry, context.Fixture.PackageInstallRoot);

    private static MultiFeedPackageResolver CreateResolver(
        FeedResolutionOptions options,
        IRemotePackageAcquirer acquirer,
        IFeedVersionEnumerator? enumerator = null)
    {
        var wrappedOptions = new OptionsWrapper<FeedResolutionOptions>(options);
        return new MultiFeedPackageResolver(
            wrappedOptions,
            new FeedResolutionPolicy(wrappedOptions),
            acquirer,
            enumerator ?? Substitute.For<IFeedVersionEnumerator>(),
            Substitute.For<IVersionRangeEvaluator>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MultiFeedPackageResolver>.Instance);
    }

    private sealed class RecordingScopedAcquirer(string installPath, Exception? failure = null)
        : IScopedRemotePackageAcquirer
    {
        internal int ScopedCallCount { get; private set; }
        internal int UnscopedCallCount { get; private set; }
        internal PackageStoreOperationBorrow? LastBorrow { get; private set; }
        internal List<string> ScopedFeedNames { get; } = [];

        public Task<string> AcquireAsync(
            FeedDefinition feed,
            string packageId,
            string version,
            CancellationToken cancellationToken)
        {
            UnscopedCallCount++;
            return Task.FromResult(installPath);
        }

        public Task<string> AcquireAsync(
            FeedDefinition feed,
            string packageId,
            string version,
            PackageStoreOperationBorrow borrow,
            CancellationToken cancellationToken)
        {
            ScopedCallCount++;
            ScopedFeedNames.Add(feed.Name);
            LastBorrow = borrow;
            return failure is null ? Task.FromResult(installPath) : Task.FromException<string>(failure);
        }
    }
}
