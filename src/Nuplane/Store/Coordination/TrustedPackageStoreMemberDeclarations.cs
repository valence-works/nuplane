using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Nuplane.Store.Coordination;

/// <summary>One absolute state-file locator declared by trusted host composition for a package-store root.</summary>
internal sealed record TrustedPackageStoreMemberDeclaration(string RootLabel, string MemberId, string StateFileLocator);

/// <summary>An immutable declaration set whose completeness is explicitly asserted by trusted composition.</summary>
internal interface ITrustedPackageStoreMemberDeclarationDefinition
{
    bool IsComplete { get; }
    IReadOnlyList<TrustedPackageStoreMemberDeclaration> Declarations { get; }
}

internal interface ITrustedPackageStoreMemberDeclarationCatalog
{
    bool IsConfigured { get; }
    bool IsComplete { get; }
    IReadOnlyList<TrustedPackageStoreMemberDeclaration> ForRoot(string label);
    IReadOnlyList<TrustedPackageStoreMemberDeclaration> All { get; }
}

internal sealed class TrustedPackageStoreMemberDeclarationDefinition : ITrustedPackageStoreMemberDeclarationDefinition
{
    internal TrustedPackageStoreMemberDeclarationDefinition(
        bool isComplete,
        IEnumerable<TrustedPackageStoreMemberDeclaration> declarations)
    {
        ArgumentNullException.ThrowIfNull(declarations);
        var copied = declarations.Select(static declaration =>
            declaration is null
                ? throw new ArgumentException("A member declaration cannot be null.", nameof(declarations))
                : new TrustedPackageStoreMemberDeclaration(
                    declaration.RootLabel,
                    declaration.MemberId,
                    declaration.StateFileLocator)).ToArray();

        foreach (var declaration in copied)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(declaration.RootLabel);
            ArgumentException.ThrowIfNullOrWhiteSpace(declaration.MemberId);
            ArgumentException.ThrowIfNullOrWhiteSpace(declaration.StateFileLocator);
            if (!Path.IsPathFullyQualified(declaration.StateFileLocator))
                throw new ArgumentException("Trusted member state locators must be absolute.", nameof(declarations));
        }

        if (copied.GroupBy(static declaration => declaration.RootLabel, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Select(static declaration => declaration.MemberId)
                .Distinct(StringComparer.Ordinal).Count() != group.Count()))
            throw new ArgumentException("Member identifiers must be unique within each trusted root label.", nameof(declarations));
        if (copied.GroupBy(static declaration => declaration.RootLabel, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Select(static declaration => declaration.StateFileLocator)
                .Distinct(StringComparer.Ordinal).Count() != group.Count()))
            throw new ArgumentException("State locators must be unique within each trusted root label.", nameof(declarations));

        IsComplete = isComplete;
        Declarations = Array.AsReadOnly(copied);
    }

    public bool IsComplete { get; }
    public IReadOnlyList<TrustedPackageStoreMemberDeclaration> Declarations { get; }

    internal static TrustedPackageStoreMemberDeclarationDefinition? From(IServiceCollection services)
        => services.LastOrDefault(static descriptor =>
                descriptor.ServiceType == typeof(ITrustedPackageStoreMemberDeclarationDefinition))
            ?.ImplementationInstance as TrustedPackageStoreMemberDeclarationDefinition;
}

/// <summary>Resolves the host's explicitly frozen declaration snapshot from dependency injection.</summary>
internal sealed class TrustedPackageStoreMemberDeclarationCatalog(
    ITrustedPackageStoreMemberDeclarationDefinition? definition) : ITrustedPackageStoreMemberDeclarationCatalog
{
    // Snapshot interface implementations too: the registration boundary may be customized by a host, so
    // retaining its mutable list would let the set change after the preview starts validating observations.
    private readonly bool _isConfigured = definition is not null;
    private readonly TrustedPackageStoreMemberDeclarationDefinition? _snapshot = definition is null
        ? null
        : new TrustedPackageStoreMemberDeclarationDefinition(definition.IsComplete, definition.Declarations);

    public bool IsConfigured => _isConfigured;
    public bool IsComplete => _snapshot?.IsComplete == true;

    public IReadOnlyList<TrustedPackageStoreMemberDeclaration> ForRoot(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        return Array.AsReadOnly((_snapshot?.Declarations ?? Array.Empty<TrustedPackageStoreMemberDeclaration>())
            .Where(declaration => string.Equals(declaration.RootLabel, label, StringComparison.OrdinalIgnoreCase))
            .ToArray());
    }

    public IReadOnlyList<TrustedPackageStoreMemberDeclaration> All =>
        Array.AsReadOnly((_snapshot?.Declarations ?? Array.Empty<TrustedPackageStoreMemberDeclaration>()).ToArray());
}

internal static class TrustedPackageStoreMemberDeclarationRegistration
{
    internal static void Register(IServiceCollection services, TrustedPackageStoreMemberDeclarationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(definition);
        services.Replace(ServiceDescriptor.Singleton<ITrustedPackageStoreMemberDeclarationDefinition>(definition));
        services.Replace(ServiceDescriptor.Singleton<TrustedPackageStoreMemberDeclarationCatalog>(provider =>
            new(provider.GetService<ITrustedPackageStoreMemberDeclarationDefinition>())));
        services.Replace(ServiceDescriptor.Singleton<ITrustedPackageStoreMemberDeclarationCatalog>(provider =>
            provider.GetRequiredService<TrustedPackageStoreMemberDeclarationCatalog>()));
    }

    internal static void RegisterDefault(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<TrustedPackageStoreMemberDeclarationCatalog>(provider =>
            new(provider.GetService<ITrustedPackageStoreMemberDeclarationDefinition>()));
        services.TryAddSingleton<ITrustedPackageStoreMemberDeclarationCatalog>(provider =>
            provider.GetRequiredService<TrustedPackageStoreMemberDeclarationCatalog>());
    }
}
