using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Metadata;
using Nuplane.Observability;
using Nuplane.Reconciliation.Configuration;

namespace Nuplane.Capabilities;

/// <summary>
/// Turns the capabilities the packages of a cycle declare, plus the host's configured selections,
/// into the additional root packages that cycle must acquire — and into loud refusals when the host
/// has not chosen.
/// </summary>
/// <remarks>
/// <para>
/// It is the only <see cref="IDesiredStateContributor"/> Nuplane ships. Every resolved package in
/// the cycle is read through the shared <see cref="NuplanePackageMetadataReader"/>, which is what
/// keeps this on exactly the trust path package metadata has always been read on: a
/// <c>nuplane.json</c> is only ever read from a package that has already been resolved and
/// installed through the configured source, trust, and integrity paths.
/// </para>
/// <para>
/// A package with no <c>nuplane.json</c>, a schema-1 document, and a valid schema-2 document with no
/// <c>capabilities</c> section all contribute nothing: nothing about a package without a schema-2
/// capability declaration changes. An invalid schema-2 document refuses its package
/// (<see cref="CapabilityRefusalStage.MetadataInvalid"/>), because a document that can affect the
/// closure and cannot be read leaves the closure unknowable; an invalid schema-1 document keeps
/// being ignored for selection and reported through the loading module's own diagnostics, because a
/// schema-1 document can never affect the closure.
/// </para>
/// <para>
/// Every decision beyond reading the files is made by the pure
/// <see cref="CapabilitySelectionResolver"/>, which this class hands the declarations, the
/// selections, and the cycle's desired requests as explicit roots. This class only performs the
/// I/O, maps the resolver's output onto the contributor contract, and emits the cycle's capability
/// log lines once each.
/// </para>
/// </remarks>
internal sealed class CapabilityDesiredStateContributor(
    IOptions<CapabilityOptions> capabilityOptions,
    IOptions<ReconciliationOptions> reconciliationOptions,
    CapabilityContributionLedger ledger,
    IReconciliationLogger logger) : IDesiredStateContributor
{
    private readonly IOptions<CapabilityOptions> _capabilityOptions = capabilityOptions ?? throw new ArgumentNullException(nameof(capabilityOptions));
    private readonly IOptions<ReconciliationOptions> _reconciliationOptions = reconciliationOptions ?? throw new ArgumentNullException(nameof(reconciliationOptions));
    private readonly CapabilityContributionLedger _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
    private readonly IReconciliationLogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly NuplanePackageMetadataReader _metadataReader = new();
    private readonly CycleLogGate _logGate = new();

    /// <inheritdoc />
    public Task<DesiredStateContribution> ContributeAsync(DesiredStateContributionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        var refusals = new List<ContributionRefusal>();
        var (declaringPackages, packageIdByIdentity) = ReadDeclarations(context, refusals);
        var selections = new Dictionary<string, CapabilitySelection>(
            _capabilityOptions.Value.Selections,
            StringComparer.OrdinalIgnoreCase);

        if (declaringPackages.Count == 0 && selections.Count == 0)
        {
            // The overwhelmingly common case: nothing in this closure declares a capability and the
            // host selects none. Only the metadata-invalid refusals above can be non-empty here.
            _ledger.RecordUnpinned(context.CorrelationId, []);
            return Task.FromResult(new DesiredStateContribution([], refusals));
        }

        // The roots earlier rounds contributed are handed to the resolver alongside the host's own,
        // for two reasons. It is what makes the loop a fixpoint: a capability whose option is
        // already a root needs no second injection, so a round that adds nothing new ends it. And it
        // extends the conflict rule to contributed roots — a second capability that requires the
        // same package at a version the first one already pinned is refused, naming both, instead of
        // silently getting the version that happened to be resolved first.
        var explicitRoots = context.ContributedSoFar.Count == 0
            ? context.DesiredRequests
            : [.. context.DesiredRequests, .. context.ContributedSoFar];

        var resolution = CapabilitySelectionResolver.Resolve(
            declaringPackages,
            selections,
            explicitRoots,
            _reconciliationOptions.Value.RequirePinnedContributions);

        // Recorded on every round, including an empty one, so that a new cycle clears whatever the
        // previous cycle refused instead of reporting it again.
        _ledger.RecordUnpinned(context.CorrelationId, resolution.UnpinnedRequests);

        foreach (var refusal in resolution.Refusals)
        {
            foreach (var identity in refusal.DeclaringPackageIds)
            {
                refusals.Add(new(
                    packageIdByIdentity.GetValueOrDefault(identity, identity),
                    refusal.Stage,
                    refusal.Message));
            }
        }

        LogDiagnostics(context.CorrelationId, resolution);

        return Task.FromResult(new DesiredStateContribution(
            BuildRequests(context.CorrelationId, declaringPackages, resolution),
            refusals));
    }

    /// <summary>
    /// Reads every resolved package's <c>nuplane.json</c>, in a deterministic order, and returns the
    /// packages that declare capabilities together with the map from the <c>id@version</c> identity
    /// the resolver names packages by back to the package id a failure is recorded under. An invalid
    /// schema-2 document contributes a refusal instead of a declaration.
    /// </summary>
    private (List<CapabilityDeclaringPackage> DeclaringPackages, Dictionary<string, string> PackageIdByIdentity) ReadDeclarations(
        DesiredStateContributionContext context,
        List<ContributionRefusal> refusals)
    {
        var declaringPackages = new List<CapabilityDeclaringPackage>();
        var packageIdByIdentity = new Dictionary<string, string>(StringComparer.Ordinal);

        var packages = context.ResolvedPackages
            .Where(static package => !string.IsNullOrWhiteSpace(package.InstallPath))
            .GroupBy(static package => $"{package.Id}@{package.Version}", StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .OrderBy(static package => package.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static package => package.Version, StringComparer.OrdinalIgnoreCase);

        foreach (var package in packages)
        {
            var read = _metadataReader.Read(package.Id, package.Version, package.InstallPath);
            if (!read.MetadataFound)
            {
                continue;
            }

            if (!read.IsValid)
            {
                // Only schema 2 can put a root in the closure, so only schema 2 is worth failing a
                // package over. A document that did not parse far enough to declare a version at
                // all is treated as the pre-capability world treated it: ignored here, and reported
                // by whatever already reported it.
                if (read.DeclaredSchemaVersion == 2)
                {
                    refusals.Add(new(package.Id, CapabilityRefusalStage.MetadataInvalid, read.Diagnostic!));
                }

                continue;
            }

            if (read.Metadata!.Capabilities.Count == 0)
            {
                continue;
            }

            var declaringPackage = new CapabilityDeclaringPackage(package.Id, package.Version, read.Metadata.Capabilities);
            declaringPackages.Add(declaringPackage);
            packageIdByIdentity[declaringPackage.Identity] = package.Id;
        }

        return (declaringPackages, packageIdByIdentity);
    }

    /// <summary>
    /// Maps each injected request onto the contributor contract, attaching the packages whose
    /// declaration of that capability option put it there — the packages that are failed with
    /// <see cref="CapabilityRefusalStage.Unresolved"/> if the request cannot be acquired.
    /// </summary>
    private List<ContributedPackageRequest> BuildRequests(
        string correlationId,
        List<CapabilityDeclaringPackage> declaringPackages,
        CapabilityResolution resolution)
    {
        var requests = new List<ContributedPackageRequest>(resolution.Injections.Count);

        foreach (var injection in resolution.Injections)
        {
            requests.Add(new(injection, FindDeclaringPackageIds(declaringPackages, injection.SourceName)));

            if (CapabilitySourceName.TryParse(injection.SourceName, out var capabilityName, out var optionName)
                && _logGate.TryEnter(correlationId, injection.SourceName))
            {
                _logger.LogCapabilitySelected(
                    correlationId,
                    capabilityName,
                    optionName,
                    injection.Id,
                    injection.VersionRange);
            }
        }

        return requests;
    }

    /// <summary>
    /// The ids of the packages that declared the capability option <paramref name="sourceName"/>
    /// names, ordered and de-duplicated. Derived from the declarations themselves rather than from
    /// the injected package id alone, so two capabilities that happen to offer the same package each
    /// fail only their own declaring packages.
    /// </summary>
    private static IReadOnlyList<string> FindDeclaringPackageIds(
        IReadOnlyList<CapabilityDeclaringPackage> declaringPackages,
        string sourceName)
    {
        if (!CapabilitySourceName.TryParse(sourceName, out var capabilityName, out var optionName))
        {
            return [];
        }

        return declaringPackages
            .Where(package => package.Declarations.Any(declaration =>
                string.Equals(declaration.Name, capabilityName, StringComparison.OrdinalIgnoreCase) &&
                declaration.Options.Any(option => string.Equals(option.Name, optionName, StringComparison.OrdinalIgnoreCase))))
            .Select(static package => package.PackageId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Emits the resolver's informational outcomes, each once per cycle: a capability an explicit
    /// root already satisfies, and a configured selection nothing in the cycle declares.
    /// </summary>
    private void LogDiagnostics(string correlationId, CapabilityResolution resolution)
    {
        foreach (var diagnostic in resolution.Diagnostics)
        {
            if (!_logGate.TryEnter(correlationId, $"{diagnostic.Kind}:{diagnostic.CapabilityName}:{diagnostic.PackageId}"))
            {
                continue;
            }

            switch (diagnostic.Kind)
            {
                case CapabilityDiagnosticKind.SatisfiedByExplicitRoot:
                    _logger.LogCapabilitySatisfiedByExplicitRoot(correlationId, diagnostic.CapabilityName, diagnostic.PackageId!);
                    break;
                case CapabilityDiagnosticKind.SelectionUnmatched:
                    _logger.LogCapabilitySelectionUnmatched(correlationId, diagnostic.CapabilityName);
                    break;
            }
        }
    }

    /// <summary>
    /// Keeps each capability log line to one occurrence per reconciliation cycle. Contributors run
    /// once per contribution round, and a cycle that injects anything runs at least two rounds — the
    /// second only to confirm the fixpoint — so without this a host that names its engine by hand
    /// would see the one promised Information line twice.
    /// </summary>
    private sealed class CycleLogGate
    {
        private readonly object _gate = new();
        private readonly HashSet<string> _entered = new(StringComparer.Ordinal);
        private string? _correlationId;

        internal bool TryEnter(string correlationId, string key)
        {
            lock (_gate)
            {
                if (!string.Equals(_correlationId, correlationId, StringComparison.Ordinal))
                {
                    _correlationId = correlationId;
                    _entered.Clear();
                }

                return _entered.Add(key);
            }
        }
    }
}
