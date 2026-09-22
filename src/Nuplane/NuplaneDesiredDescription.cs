namespace Nuplane;

/// <summary>
/// The answer <see cref="NuplaneRestore.DescribeDesiredAsync"/> gives: what a restore of this
/// configuration would ask for, and where it would put it, without asking a network for anything.
/// </summary>
/// <param name="Requests">
/// Every desired request the same sources a reconciliation cycle reads would produce, deduplicated
/// and ordered by the same aggregator, each carrying whether it is a single-point pin.
/// </param>
/// <param name="CredentialRefusedFeeds">
/// The names of configured feeds whose <c>secrets://</c> credential reference could not be resolved.
/// A restore refuses these feeds up front instead of contacting them; they are named here so the
/// refusal is visible before anything runs. A feed whose reference resolves is not named here.
/// </param>
/// <param name="SourceErrors">
/// The message of every desired-state source that failed while being read, keyed by source type
/// name. A source that fails contributes no requests, so an empty <paramref name="Requests"/> with
/// an entry here means "could not tell", not "nothing is desired".
/// </param>
/// <param name="StateFilePath">The resolved state file a restore would write.</param>
/// <param name="InstallRoot">The resolved install root a restore would extract packages into.</param>
public sealed record NuplaneDesiredDescription(
    IReadOnlyList<DesiredPackageDescription> Requests,
    IReadOnlyList<string> CredentialRefusedFeeds,
    IReadOnlyDictionary<string, string> SourceErrors,
    string StateFilePath,
    string InstallRoot);
