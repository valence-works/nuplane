using Nuplane.Abstractions;
using Nuplane.Capabilities;

namespace Nuplane.Runtime.Tests.Capabilities;

public sealed class CapabilitySelectionResolverTests
{
    private static CapabilityDeclaringPackage Package(string id, string version, params PackageCapabilityDeclaration[] declarations) =>
        new(id, version, declarations);

    private static PackageCapabilityDeclaration Declaration(string name, params PackageCapabilityOption[] options) =>
        new(name, Description: null, options);

    private static PackageCapabilityOption Option(string name, string packageId, string versionRange) =>
        new(name, packageId, versionRange);

    private static CapabilitySelection Selection(params string[] options) =>
        new() { Options = options };

    private static PackageRequest ExplicitRoot(string id, string versionRange, string sourceName = "feed-rule:nuget") =>
        new(id, versionRange, FeedName: null, PackageUpdatePolicy.Exact, sourceName);

    private static CapabilityResolution Resolve(
        IReadOnlyList<CapabilityDeclaringPackage> declarationsByPackage,
        IReadOnlyDictionary<string, CapabilitySelection> selections,
        IReadOnlyList<PackageRequest>? explicitRoots = null,
        bool requirePinned = false) =>
        CapabilitySelectionResolver.Resolve(declarationsByPackage, selections, explicitRoots ?? [], requirePinned);

    /// <summary>Every ordering of <paramref name="items"/>, for permutation-based determinism tests.</summary>
    private static IEnumerable<T[]> Permutations<T>(IReadOnlyList<T> items)
    {
        if (items.Count == 0)
        {
            yield return [];
            yield break;
        }

        for (var i = 0; i < items.Count; i++)
        {
            var remaining = items.Where((_, index) => index != i).ToArray();
            foreach (var permutation in Permutations(remaining))
            {
                yield return [items[i], .. permutation];
            }
        }
    }

    [Fact]
    public void Resolve_NoDeclarationsAndNoSelections_ReturnsEmptyResolution()
    {
        var result = Resolve([], new Dictionary<string, CapabilitySelection>());

        Assert.Empty(result.Injections);
        Assert.Empty(result.Refusals);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Resolve_SingleSelectedOption_InjectsPackageRequest()
    {
        var declaration = Declaration("ef-provider",
            Option("PostgreSql", "Npgsql.EntityFrameworkCore.PostgreSQL", "[10.0.0]"),
            Option("Sqlite", "Microsoft.EntityFrameworkCore.Sqlite", "[10.0.10]"));
        var package = Package("Acme.Module", "1.2.0", declaration);
        var selections = new Dictionary<string, CapabilitySelection> { ["ef-provider"] = Selection("PostgreSql") };

        var result = Resolve([package], selections);

        var injection = Assert.Single(result.Injections);
        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", injection.Id);
        Assert.Equal("[10.0.0]", injection.VersionRange);
        Assert.Equal(PackageUpdatePolicy.Exact, injection.UpdatePolicy);
        Assert.Equal("capability:ef-provider=PostgreSql", injection.SourceName);
        Assert.Empty(result.Refusals);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Resolve_MultiOptionSelection_InjectsBothOrderedByOptionName()
    {
        var declaration = Declaration("ef-provider",
            Option("Sqlite", "Microsoft.EntityFrameworkCore.Sqlite", "[10.0.10]"),
            Option("PostgreSql", "Npgsql.EntityFrameworkCore.PostgreSQL", "[10.0.0]"));
        var package = Package("Acme.Module", "1.2.0", declaration);
        var selections = new Dictionary<string, CapabilitySelection> { ["ef-provider"] = Selection("PostgreSql", "Sqlite") };

        var result = Resolve([package], selections);

        Assert.Equal(2, result.Injections.Count);
        Assert.Equal("capability:ef-provider=PostgreSql", result.Injections[0].SourceName);
        Assert.Equal("capability:ef-provider=Sqlite", result.Injections[1].SourceName);
    }

    [Fact]
    public void Resolve_NoSelectionAndNoExplicitRoot_RefusesUnselectedNamingCapabilityOptionsAndKey()
    {
        var declaration = Declaration("ef-provider",
            Option("Sqlite", "Microsoft.EntityFrameworkCore.Sqlite", "[10.0.10]"),
            Option("PostgreSql", "Npgsql.EntityFrameworkCore.PostgreSQL", "[10.0.0]"));
        var package = Package("Acme.Module", "1.2.0", declaration);

        var result = Resolve([package], new Dictionary<string, CapabilitySelection>());

        Assert.Empty(result.Injections);
        var refusal = Assert.Single(result.Refusals);
        Assert.Equal(CapabilityRefusalStage.Unselected, refusal.Stage);
        Assert.Equal("ef-provider", refusal.CapabilityName);
        Assert.Equal(["Acme.Module@1.2.0"], refusal.DeclaringPackageIds);
        Assert.Contains("Acme.Module@1.2.0", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("PostgreSql", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Sqlite", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Nuplane:Capabilities:ef-provider", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_NoSelectionWithSatisfyingExplicitRoot_SatisfiesWithDiagnosticAndNoInjection()
    {
        var declaration = Declaration("ef-provider",
            Option("PostgreSql", "Npgsql.EntityFrameworkCore.PostgreSQL", "[10.0.0]"));
        var package = Package("Acme.Module", "1.2.0", declaration);
        var explicitRoots = new[] { ExplicitRoot("Npgsql.EntityFrameworkCore.PostgreSQL", "[10.0.0]") };

        var result = Resolve([package], new Dictionary<string, CapabilitySelection>(), explicitRoots);

        Assert.Empty(result.Injections);
        Assert.Empty(result.Refusals);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(CapabilityDiagnosticKind.SatisfiedByExplicitRoot, diagnostic.Kind);
        Assert.Equal("ef-provider", diagnostic.CapabilityName);
        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", diagnostic.PackageId);
        Assert.Contains("ef-provider", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("Npgsql.EntityFrameworkCore.PostgreSQL", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_TwoEnginesBothExplicitRoots_SatisfiesBothWithoutSelection()
    {
        var declaration = Declaration("ef-provider",
            Option("PostgreSql", "Npgsql.EntityFrameworkCore.PostgreSQL", "[10.0.0]"),
            Option("Sqlite", "Microsoft.EntityFrameworkCore.Sqlite", "[10.0.10]"));
        var package = Package("Acme.Module", "1.2.0", declaration);
        var explicitRoots = new[]
        {
            ExplicitRoot("Npgsql.EntityFrameworkCore.PostgreSQL", "[10.0.0]"),
            ExplicitRoot("Microsoft.EntityFrameworkCore.Sqlite", "[10.0.10]")
        };

        var result = Resolve([package], new Dictionary<string, CapabilitySelection>(), explicitRoots);

        Assert.Empty(result.Injections);
        Assert.Empty(result.Refusals);
        Assert.Equal(2, result.Diagnostics.Count);
    }

    [Fact]
    public void Resolve_SelectionNamesUnknownOption_RefusesUnknownOption()
    {
        var declaration = Declaration("ef-provider",
            Option("PostgreSql", "Npgsql.EntityFrameworkCore.PostgreSQL", "[10.0.0]"));
        var package = Package("Acme.Module", "1.2.0", declaration);
        var selections = new Dictionary<string, CapabilitySelection> { ["ef-provider"] = Selection("Postgres") };

        var result = Resolve([package], selections);

        Assert.Empty(result.Injections);
        var refusal = Assert.Single(result.Refusals);
        Assert.Equal(CapabilityRefusalStage.UnknownOption, refusal.Stage);
        Assert.Contains("Postgres", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("PostgreSql", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ConflictingDeclarationsDifferentPackageId_RefusesConflictOnEveryDeclaringPackage()
    {
        var declarationA = Declaration("ef-provider", Option("PostgreSql", "Npgsql.EntityFrameworkCore.PostgreSQL", "[10.0.0]"));
        var declarationB = Declaration("ef-provider", Option("PostgreSql", "Other.Npgsql.Provider", "[10.0.0]"));
        var packageA = Package("Acme.ModuleA", "1.0.0", declarationA);
        var packageB = Package("Acme.ModuleB", "1.0.0", declarationB);
        var selections = new Dictionary<string, CapabilitySelection> { ["ef-provider"] = Selection("PostgreSql") };

        var result = Resolve([packageA, packageB], selections);

        Assert.Empty(result.Injections);
        var refusal = Assert.Single(result.Refusals);
        Assert.Equal(CapabilityRefusalStage.Conflict, refusal.Stage);
        Assert.Equal(["Acme.ModuleA@1.0.0", "Acme.ModuleB@1.0.0"], refusal.DeclaringPackageIds);
        Assert.Contains("Npgsql.EntityFrameworkCore.PostgreSQL", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Other.Npgsql.Provider", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ConflictingDeclarationsDifferentVersionWithoutOverride_RefusesConflict()
    {
        var declarationA = Declaration("ef-provider", Option("PostgreSql", "Npgsql.EntityFrameworkCore.PostgreSQL", "[9.0.4]"));
        var declarationB = Declaration("ef-provider", Option("PostgreSql", "Npgsql.EntityFrameworkCore.PostgreSQL", "[10.0.0]"));
        var packageA = Package("Acme.ModuleA", "1.0.0", declarationA);
        var packageB = Package("Acme.ModuleB", "1.0.0", declarationB);
        var selections = new Dictionary<string, CapabilitySelection> { ["ef-provider"] = Selection("PostgreSql") };

        var result = Resolve([packageA, packageB], selections);

        Assert.Empty(result.Injections);
        var refusal = Assert.Single(result.Refusals);
        Assert.Equal(CapabilityRefusalStage.Conflict, refusal.Stage);
        Assert.Contains("[9.0.4]", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("[10.0.0]", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Nuplane:Capabilities:ef-provider:Version", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ConflictingDeclarationsDifferentVersionWithHostOverride_UsesOverrideInstead()
    {
        var declarationA = Declaration("ef-provider", Option("PostgreSql", "Npgsql.EntityFrameworkCore.PostgreSQL", "[9.0.4]"));
        var declarationB = Declaration("ef-provider", Option("PostgreSql", "Npgsql.EntityFrameworkCore.PostgreSQL", "[10.0.0]"));
        var packageA = Package("Acme.ModuleA", "1.0.0", declarationA);
        var packageB = Package("Acme.ModuleB", "1.0.0", declarationB);
        var selections = new Dictionary<string, CapabilitySelection>
        {
            ["ef-provider"] = new() { Options = ["PostgreSql"], Version = "[9.5.0]" }
        };

        var result = Resolve([packageA, packageB], selections);

        Assert.Empty(result.Refusals);
        var injection = Assert.Single(result.Injections);
        Assert.Equal("[9.5.0]", injection.VersionRange);
    }

    [Fact]
    public void Resolve_ExplicitRootSatisfiesSelectedOption_NoInjectionForThatOption()
    {
        var declaration = Declaration("ef-provider", Option("PostgreSql", "Npgsql.EntityFrameworkCore.PostgreSQL", "[10.0.0]"));
        var package = Package("Acme.Module", "1.2.0", declaration);
        var selections = new Dictionary<string, CapabilitySelection> { ["ef-provider"] = Selection("PostgreSql") };
        var explicitRoots = new[] { ExplicitRoot("Npgsql.EntityFrameworkCore.PostgreSQL", "[10.0.0]") };

        var result = Resolve([package], selections, explicitRoots);

        Assert.Empty(result.Injections);
        Assert.Empty(result.Refusals);
    }

    [Fact]
    public void Resolve_ExplicitRootDoesNotSatisfyDeclaredOption_RefusesConflictNamingBothRequests()
    {
        var declaration = Declaration("ef-provider", Option("PostgreSql", "Npgsql.EntityFrameworkCore.PostgreSQL", "[10.0.0]"));
        var package = Package("Elsa.Secrets.Persistence.EntityFrameworkCore", "4.0.0", declaration);
        var explicitRoots = new[] { ExplicitRoot("Npgsql.EntityFrameworkCore.PostgreSQL", "[9.0.4]", "feed-rule:nuget") };

        var result = Resolve([package], new Dictionary<string, CapabilitySelection>(), explicitRoots);

        Assert.Empty(result.Injections);
        var refusal = Assert.Single(result.Refusals);
        Assert.Equal(CapabilityRefusalStage.Conflict, refusal.Stage);
        Assert.Contains("[9.0.4]", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("[10.0.0]", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("feed-rule:nuget", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Elsa.Secrets.Persistence.EntityFrameworkCore@4.0.0", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ExplicitRootRangeIntersectsDeclaredRange_SatisfiesWithNoInjection()
    {
        var declaration = Declaration("ef-provider", Option("PostgreSql", "Npgsql.EntityFrameworkCore.PostgreSQL", "[10.0.0,11.0.0)"));
        var package = Package("Acme.Module", "1.2.0", declaration);
        var selections = new Dictionary<string, CapabilitySelection> { ["ef-provider"] = Selection("PostgreSql") };
        var explicitRoots = new[] { ExplicitRoot("Npgsql.EntityFrameworkCore.PostgreSQL", "[10.5.0,10.8.0)") };

        var result = Resolve([package], selections, explicitRoots);

        Assert.Empty(result.Injections);
        Assert.Empty(result.Refusals);
    }

    [Fact]
    public void Resolve_ExplicitRootRangeIsDisjointFromDeclaredRange_RefusesConflict()
    {
        var declaration = Declaration("ef-provider", Option("PostgreSql", "Npgsql.EntityFrameworkCore.PostgreSQL", "[10.0.0,11.0.0)"));
        var package = Package("Acme.Module", "1.2.0", declaration);
        var explicitRoots = new[] { ExplicitRoot("Npgsql.EntityFrameworkCore.PostgreSQL", "[12.0.0,13.0.0)") };

        var result = Resolve([package], new Dictionary<string, CapabilitySelection>(), explicitRoots);

        Assert.Empty(result.Injections);
        var refusal = Assert.Single(result.Refusals);
        Assert.Equal(CapabilityRefusalStage.Conflict, refusal.Stage);
        Assert.Contains("[12.0.0,13.0.0)", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("[10.0.0,11.0.0)", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("does not satisfy", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ExplicitRootVersionIsFloating_RefusesConflictAskingToPinIt()
    {
        var declaration = Declaration("ef-provider", Option("PostgreSql", "Npgsql.EntityFrameworkCore.PostgreSQL", "[10.0.0]"));
        var package = Package("Acme.Module", "1.2.0", declaration);
        var explicitRoots = new[] { ExplicitRoot("Npgsql.EntityFrameworkCore.PostgreSQL", "10.0.*") };

        var result = Resolve([package], new Dictionary<string, CapabilitySelection>(), explicitRoots);

        Assert.Empty(result.Injections);
        var refusal = Assert.Single(result.Refusals);
        Assert.Equal(CapabilityRefusalStage.Conflict, refusal.Stage);
        Assert.Contains("10.0.*", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be verified", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("pin", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_RequirePinnedAndSelectedOptionIsARange_RefusesUnpinnedWithNoInjection()
    {
        var declaration = Declaration("ef-provider", Option("PostgreSql", "Npgsql.EntityFrameworkCore.PostgreSQL", "[10.0.0,11.0.0)"));
        var package = Package("Acme.Module", "1.2.0", declaration);
        var selections = new Dictionary<string, CapabilitySelection> { ["ef-provider"] = Selection("PostgreSql") };

        var result = Resolve([package], selections, requirePinned: true);

        Assert.Empty(result.Injections);
        var refusal = Assert.Single(result.Refusals);
        Assert.Equal(CapabilityRefusalStage.Unpinned, refusal.Stage);
        Assert.Contains("[10.0.0,11.0.0)", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(["Acme.Module@1.2.0"], refusal.DeclaringPackageIds);
    }

    [Fact]
    public void Resolve_RequirePinnedAndSelectedOptionIsExact_Injects()
    {
        var declaration = Declaration("ef-provider", Option("PostgreSql", "Npgsql.EntityFrameworkCore.PostgreSQL", "[10.0.0]"));
        var package = Package("Acme.Module", "1.2.0", declaration);
        var selections = new Dictionary<string, CapabilitySelection> { ["ef-provider"] = Selection("PostgreSql") };

        var result = Resolve([package], selections, requirePinned: true);

        Assert.Empty(result.Refusals);
        Assert.Single(result.Injections);
    }

    [Fact]
    public void Resolve_RequirePinnedAndOnlyOneOfTwoSelectedOptionsIsUnpinned_InjectsTheOtherAndRefusesOnlyTheUnpinnedOne()
    {
        var declaration = Declaration("ef-provider",
            Option("PostgreSql", "Npgsql.EntityFrameworkCore.PostgreSQL", "[10.0.0]"),
            Option("Sqlite", "Microsoft.EntityFrameworkCore.Sqlite", "[10.0.0,11.0.0)"));
        var package = Package("Acme.Module", "1.2.0", declaration);
        var selections = new Dictionary<string, CapabilitySelection> { ["ef-provider"] = Selection("PostgreSql", "Sqlite") };

        var result = Resolve([package], selections, requirePinned: true);

        var injection = Assert.Single(result.Injections);
        Assert.Equal("capability:ef-provider=PostgreSql", injection.SourceName);
        var refusal = Assert.Single(result.Refusals);
        Assert.Equal(CapabilityRefusalStage.Unpinned, refusal.Stage);
        Assert.Contains("Sqlite", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_CapabilityNameIsCaseInsensitive_DeclarationAndSelectionMatchAcrossCasing()
    {
        var declaration = Declaration("EF-Provider", Option("PostgreSql", "Npgsql.EntityFrameworkCore.PostgreSQL", "[10.0.0]"));
        var package = Package("Acme.Module", "1.2.0", declaration);
        var selections = new Dictionary<string, CapabilitySelection> { ["ef-provider"] = Selection("postgresql") };

        var result = Resolve([package], selections);

        var injection = Assert.Single(result.Injections);
        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", injection.Id);
    }

    [Fact]
    public void Resolve_SelectionMatchesNoDeclaration_AddsDiagnosticNamingTheKey()
    {
        var selections = new Dictionary<string, CapabilitySelection> { ["message-broker"] = Selection("RabbitMq") };

        var result = Resolve([], selections);

        Assert.Empty(result.Injections);
        Assert.Empty(result.Refusals);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(CapabilityDiagnosticKind.SelectionUnmatched, diagnostic.Kind);
        Assert.Equal("message-broker", diagnostic.CapabilityName);
        Assert.Contains("Nuplane:Capabilities:message-broker", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_MultipleConsistentDeclaringPackages_InjectsOnce()
    {
        var declarationA = Declaration("ef-provider", Option("PostgreSql", "Npgsql.EntityFrameworkCore.PostgreSQL", "[10.0.0]"));
        var declarationB = Declaration("ef-provider", Option("PostgreSql", "Npgsql.EntityFrameworkCore.PostgreSQL", "[10.0.0]"));
        var packageA = Package("Acme.ModuleA", "1.0.0", declarationA);
        var packageB = Package("Acme.ModuleB", "1.0.0", declarationB);
        var selections = new Dictionary<string, CapabilitySelection> { ["ef-provider"] = Selection("PostgreSql") };

        var result = Resolve([packageA, packageB], selections);

        Assert.Single(result.Injections);
        Assert.Empty(result.Refusals);
    }

    [Fact]
    public void Resolve_MultipleCapabilities_InjectionsOrderedByCapabilityNameThenOptionName()
    {
        var packages = new[]
        {
            Package("Acme.MessageBroker", "1.0.0",
                Declaration("message-broker", Option("RabbitMq", "RabbitMQ.Client", "[6.0.0]"))),
            Package("Acme.EfModule", "1.0.0",
                Declaration("ef-provider",
                    Option("Sqlite", "Microsoft.EntityFrameworkCore.Sqlite", "[10.0.10]"),
                    Option("PostgreSql", "Npgsql.EntityFrameworkCore.PostgreSQL", "[10.0.0]")))
        };
        var selections = new Dictionary<string, CapabilitySelection>
        {
            ["message-broker"] = Selection("RabbitMq"),
            ["ef-provider"] = Selection("Sqlite", "PostgreSql")
        };

        var result = Resolve(packages, selections);

        Assert.Equal(3, result.Injections.Count);
        Assert.Equal("capability:ef-provider=PostgreSql", result.Injections[0].SourceName);
        Assert.Equal("capability:ef-provider=Sqlite", result.Injections[1].SourceName);
        Assert.Equal("capability:message-broker=RabbitMq", result.Injections[2].SourceName);
    }

    [Fact]
    public void Resolve_IsDeterministicRegardlessOfInputOrder()
    {
        // Three declaring packages across two capabilities, two selections, and an explicit-root list
        // that includes both a matching and an irrelevant root, so permuting each of the three inputs
        // independently exercises capability-name canonicalization (mixed casing across packageA and
        // packageB's declarations), option-name canonicalization, explicit-root matching, and the
        // selection-map walk all at once.
        var declarationA = Declaration("Ef-Provider",
            Option("PostgreSql", "Npgsql.EntityFrameworkCore.PostgreSQL", "[10.0.0]"),
            Option("Sqlite", "Microsoft.EntityFrameworkCore.Sqlite", "[10.0.10]"));
        var declarationB = Declaration("ef-provider",
            Option("postgresql", "Npgsql.EntityFrameworkCore.PostgreSQL", "[10.0.0]"),
            Option("SQLITE", "Microsoft.EntityFrameworkCore.Sqlite", "[10.0.10]"));
        var declarationC = Declaration("message-broker", Option("RabbitMq", "RabbitMQ.Client", "[6.0.0]"));

        var packages = new[]
        {
            Package("Acme.ModuleA", "1.0.0", declarationA),
            Package("Acme.ModuleB", "2.0.0", declarationB),
            Package("Acme.ModuleC", "1.0.0", declarationC)
        };

        var selectionEntries = new[]
        {
            new KeyValuePair<string, CapabilitySelection>("ef-provider", Selection("Sqlite", "PostgreSql")),
            new KeyValuePair<string, CapabilitySelection>("message-broker", Selection("RabbitMq"))
        };

        var explicitRoots = new[]
        {
            ExplicitRoot("Microsoft.EntityFrameworkCore.Sqlite", "[10.0.10]"),
            ExplicitRoot("Some.Unrelated.Package", "[1.0.0]")
        };

        CapabilityResolution? baseline = null;
        var permutationCount = 0;

        foreach (var packagePermutation in Permutations(packages))
        {
            foreach (var selectionPermutation in Permutations(selectionEntries))
            {
                var selections = new Dictionary<string, CapabilitySelection>();
                foreach (var entry in selectionPermutation)
                {
                    selections[entry.Key] = entry.Value;
                }

                foreach (var rootPermutation in Permutations(explicitRoots))
                {
                    permutationCount++;
                    var result = CapabilitySelectionResolver.Resolve(packagePermutation, selections, rootPermutation, requirePinned: true);

                    if (baseline is null)
                    {
                        baseline = result;
                        continue;
                    }

                    // CapabilityResolution's list-typed properties do not carry structural equality
                    // themselves (List<T> equality is reference equality), so the "byte-identical
                    // output" contract is asserted element-by-element instead of via record equality.
                    Assert.Equal(baseline.Injections, result.Injections);
                    Assert.Equal(baseline.Refusals, result.Refusals);
                    Assert.Equal(baseline.Diagnostics, result.Diagnostics);
                }
            }
        }

        Assert.Equal(6 * 2 * 2, permutationCount);
        Assert.NotNull(baseline);
        Assert.Empty(baseline!.Refusals);

        // Canonicalization picks the ordinally-smallest casing observed across every source
        // ('E' sorts before 'e'), independent of which package the resolver saw first. Sqlite is
        // satisfied by the explicit root, so only PostgreSql and message-broker's RabbitMq inject.
        Assert.Equal(2, baseline.Injections.Count);
        Assert.Equal("capability:Ef-Provider=PostgreSql", baseline.Injections[0].SourceName);
        Assert.Equal("capability:message-broker=RabbitMq", baseline.Injections[1].SourceName);
    }
}
