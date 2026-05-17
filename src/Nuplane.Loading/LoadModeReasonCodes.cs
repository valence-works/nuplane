namespace Nuplane.Loading;

internal static class LoadModeReasonCodes
{
    public const string Default = "default";
    public const string PackageOverride = "package-override";
    public const string PackageMetadata = "package-metadata";
    public const string DependencyClosure = "dependency-closure";
    public const string MetadataInvalid = "metadata-invalid";
    public const string MetadataSuppressed = "metadata-suppressed";
    public const string AdvisorSuppressed = "advisor-suppressed";
    public const string MetadataConflict = "metadata-conflict";
    public const string AdvisorsDisabled = "advisors-disabled";
}

internal static class LoadModeScopes
{
    public const string DependencyClosure = "DependencyClosure";
    public const string PackageOnly = "PackageOnly";
}
