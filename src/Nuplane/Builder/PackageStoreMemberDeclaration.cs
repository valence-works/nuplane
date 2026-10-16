namespace Nuplane.Builder;

/// <summary>Declares one participating client's state file for a named package-store root.</summary>
/// <param name="RootLabel">The root label registered by the same Nuplane builder.</param>
/// <param name="MemberId">The stable member identifier used by package-store membership.</param>
/// <param name="StateFilePath">The absolute state-file path owned by this client.</param>
public sealed record PackageStoreMemberDeclaration(string RootLabel, string MemberId, string StateFilePath);
