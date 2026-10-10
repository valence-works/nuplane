namespace Nuplane.Runtime.Tests.TestSupport;

/// <summary>
/// A fact that is skipped when the test process is privileged (root on Unix, elevated on Windows),
/// because a privileged process ignores read-only file permissions.
/// </summary>
public sealed class UnprivilegedFactAttribute : FactAttribute
{
    public UnprivilegedFactAttribute()
    {
        if (Environment.IsPrivilegedProcess)
        {
            Skip = "Root ignores read-only file permissions.";
        }
    }
}
