namespace Nuplane.Loading;

/// <summary>
/// The decision one <see cref="IPackageActivationGate"/> returns for a package graph: activation is
/// either allowed, or blocked with a reason an operator can act on.
/// </summary>
public sealed record PackageActivationGateResult
{
    private PackageActivationGateResult(bool isAllowed, string? reason)
    {
        IsAllowed = isAllowed;
        Reason = reason;
    }

    /// <summary>
    /// Gets the result that lets the graph load. It changes nothing about how the graph is loaded.
    /// </summary>
    public static PackageActivationGateResult Allow { get; } = new(isAllowed: true, reason: null);

    /// <summary>
    /// Gets a value indicating whether the gate allows the graph to be activated.
    /// </summary>
    public bool IsAllowed { get; }

    /// <summary>
    /// Gets the reason activation was refused, or <see langword="null"/> when activation is allowed.
    /// The reason is surfaced in the load failure and in structured logs, so it must not contain secrets.
    /// </summary>
    public string? Reason { get; }

    /// <summary>
    /// Creates a result that refuses activation of the whole package graph. Because the graph is refused
    /// before it is resolved, every package in it is reported as a failed package carrying
    /// <paramref name="reason"/> — including members a successful load would have skipped as
    /// host-runtime-provided or as carrying no assemblies.
    /// </summary>
    /// <param name="reason">The secret-safe, human-readable reason activation was refused.</param>
    /// <returns>A blocking result carrying <paramref name="reason"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="reason"/> is <see langword="null"/>, empty, or whitespace.</exception>
    public static PackageActivationGateResult Block(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return new(isAllowed: false, reason);
    }
}
