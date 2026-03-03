namespace Nuplane.Runtime.Configuration;

/// <summary>
/// Specifies how the lock file is used during reconciliation.
/// </summary>
public enum LockFileMode
{
    /// <summary>The lock file is generated from resolved packages but not enforced.</summary>
    Generate,
    /// <summary>The lock file is enforced: resolved versions are overridden by lock entries when present.</summary>
    Enforce,
    /// <summary>The lock file is strictly enforced: every resolved package must have a lock entry.</summary>
    Strict
}

/// <summary>
/// Configuration options for the package lock file, controlling its mode, path, and hash enforcement.
/// </summary>
public sealed class LockFileOptions
{
    /// <summary>
    /// Gets or sets the lock file evaluation mode.
    /// </summary>
    public LockFileMode Mode { get; set; } = LockFileMode.Generate;

    /// <summary>
    /// Gets or sets the file path for the lock file.
    /// </summary>
    public string Path { get; set; } = "nuplane.lock.json";

    /// <summary>
    /// Gets or sets whether a hash mismatch between a resolved package and its lock entry causes failure.
    /// </summary>
    public bool FailOnHashMismatch { get; set; } = true;

    /// <summary>
    /// Gets or sets whether strict mode requires every resolved package to have a lock entry.
    /// </summary>
    public bool RequireEntryInStrictMode { get; set; } = true;

    /// <summary>
    /// Validates that the lock file options are internally consistent.
    /// </summary>
    /// <returns><see langword="true"/> if the options are valid; otherwise <see langword="false"/>.</returns>
    public bool IsValid() => !string.IsNullOrWhiteSpace(Path);
}
