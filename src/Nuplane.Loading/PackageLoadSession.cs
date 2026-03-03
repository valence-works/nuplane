namespace Nuplane.Loading;

public sealed record PackageLoadSession(
    string PackageId,
    string Version,
    string ActiveInstallPath,
    string ContextKey,
    DateTimeOffset LoadedAt,
    bool IsLoaded,
    string? LastError);
