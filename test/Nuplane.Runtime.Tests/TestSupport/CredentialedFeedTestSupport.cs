using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nuplane.Feeds.Configuration;
using Nuplane.Feeds.Credentials;

namespace Nuplane.Runtime.Tests.TestSupport;

/// <summary>
/// A secret reference provider that answers from a fixed map and counts what it was asked for, so a
/// test can assert both the value that came back and how often it was looked up.
/// </summary>
internal sealed class StubSecretReferenceProvider(string scheme, IReadOnlyDictionary<string, string?> secrets)
    : ISecretReferenceProvider
{
    private readonly List<string> _requestedNames = [];
    private int _lookups;

    public string Scheme { get; } = scheme;

    public int Lookups => Volatile.Read(ref _lookups);

    public IReadOnlyList<string> RequestedNames
    {
        get
        {
            lock (_requestedNames)
            {
                return _requestedNames.ToArray();
            }
        }
    }

    public ValueTask<string?> ResolveAsync(string name, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _lookups);
        lock (_requestedNames)
        {
            _requestedNames.Add(name);
        }

        return new(secrets.GetValueOrDefault(name));
    }

    internal static StubSecretReferenceProvider Holding(string scheme, string name, string? value) =>
        new(scheme, new Dictionary<string, string?>(StringComparer.Ordinal) { [name] = value });
}

/// <summary>
/// Sets a process environment variable for the duration of a test and restores whatever was there
/// before. Use a name unique to the test: the variable is process-wide.
/// </summary>
internal sealed class EnvironmentVariableScope : IDisposable
{
    private readonly string _name;
    private readonly string? _previous;

    public EnvironmentVariableScope(string name, string? value)
    {
        _name = name;
        _previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
}

/// <summary>
/// Captures everything written to the logging pipeline — message, structured state, scopes and
/// exceptions — so a test can assert that a secret appears in none of it.
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<string> _entries = [];

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.ToArray();
            }
        }
    }

    /// <summary>Everything captured, as one string, for a single "does not contain" assertion.</summary>
    public string AllText => string.Join(Environment.NewLine, Entries);

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public void Dispose()
    {
    }

    public ILoggerFactory CreateFactory() => LoggerFactory.Create(builder =>
    {
        builder.SetMinimumLevel(LogLevel.Trace);
        builder.AddProvider(this);
    });

    private void Add(string entry)
    {
        lock (_entries)
        {
            _entries.Add(entry);
        }
    }

    private sealed class CapturingLogger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            owner.Add($"scope {category}: {state}");
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            owner.Add($"{logLevel} {category}: {formatter(state, exception)} | state={state} | exception={exception}");

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}

internal static class CredentialedFeedTestComposition
{
    /// <summary>
    /// Removes the validator that requires every non-<c>file://</c> feed to use HTTPS, so that a test
    /// can point a composed host or restore at the in-process test server, which
    /// <see cref="System.Net.HttpListener"/> can only serve over HTTP. Everything else about the
    /// composition stays exactly as <c>AddNuplane</c> built it.
    /// </summary>
    /// <remarks>
    /// The same validator carries the <c>secrets://</c> shape rule, which
    /// <c>FeedCredentialOptionsValidatorTests</c> covers directly, so no rule goes untested — this
    /// only stops a loopback URL from being refused before the feed is ever contacted.
    /// </remarks>
    internal static void AllowHttpLoopbackFeeds(IServiceCollection services) =>
        services.Remove(services.Single(descriptor =>
            descriptor.ServiceType == typeof(IValidateOptions<FeedResolutionOptions>)
            && descriptor.ImplementationType == typeof(FeedCredentialCompositeValidator)));
}
