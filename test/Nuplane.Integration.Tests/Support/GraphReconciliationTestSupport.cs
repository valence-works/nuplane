using Nuplane.Abstractions;

namespace Nuplane.Integration.Tests.Support;

internal static class GraphReconciliationTestSupport
{
    public const string RootPackageId = "Plugin.Root";
    public const string DependencyPackageId = "Plugin.Dependency";
    public const string TestFeedName = "test-feed";
    public const string RootVersion = "1.0.0";
    public const string DependencyVersion = "1.0.0";

    public static PackageRequest RootRequest() =>
        new(RootPackageId, RootVersion, TestFeedName, PackageUpdatePolicy.Exact, "graph-test");

    public static PackageRequest DependencyRequest() =>
        new(DependencyPackageId, DependencyVersion, TestFeedName, PackageUpdatePolicy.Exact, "graph-test");
}
