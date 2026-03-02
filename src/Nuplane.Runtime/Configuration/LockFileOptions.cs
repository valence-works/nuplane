namespace Nuplane.Runtime.Configuration;

public enum LockFileMode
{
    Generate,
    Enforce,
    Strict
}

public sealed class LockFileOptions
{
    public LockFileMode Mode { get; set; } = LockFileMode.Generate;

    public string Path { get; set; } = "nuplane.lock.json";

    public bool FailOnHashMismatch { get; set; } = true;

    public bool RequireEntryInStrictMode { get; set; } = true;

    public bool IsValid() => !string.IsNullOrWhiteSpace(Path);
}
