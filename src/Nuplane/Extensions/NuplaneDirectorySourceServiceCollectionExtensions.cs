using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nuplane.Abstractions;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Sources.Directory;

namespace Nuplane.Extensions;

/// <summary>
/// Provides extension methods for registering directory-backed desired-state inputs.
/// </summary>
public static class NuplaneDirectorySourceServiceCollectionExtensions
{
    /// <summary>
    /// Registers a directory-based desired-state source and, optionally, a file-change watcher
    /// that triggers manual reconciliation when <c>.nupkg</c> files change.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configure">The options configuration callback.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when arguments are <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when configured options are invalid.</exception>
    public static IServiceCollection AddNuplaneDirectorySource(
        this IServiceCollection services,
        Action<DirectorySourceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var configuredOptions = new DirectorySourceOptions();
        configure(configuredOptions);

        if (string.IsNullOrWhiteSpace(configuredOptions.DirectoryPath))
        {
            throw new ArgumentException("DirectoryPath is required for directory source registration.", nameof(configure));
        }

        if (configuredOptions.DebounceWindow <= TimeSpan.Zero)
        {
            throw new ArgumentException("DebounceWindow must be greater than zero.", nameof(configure));
        }

        var normalizedOptions = new DirectorySourceOptions
        {
            DirectoryPath = Path.GetFullPath(configuredOptions.DirectoryPath),
            SourceName = string.IsNullOrWhiteSpace(configuredOptions.SourceName) ? "Directory.Drop" : configuredOptions.SourceName,
            TriggerReconciliationOnChange = configuredOptions.TriggerReconciliationOnChange,
            DebounceWindow = configuredOptions.DebounceWindow
        };

        foreach (var packageId in configuredOptions.AllowlistedPackageIds)
        {
            if (!string.IsNullOrWhiteSpace(packageId))
            {
                normalizedOptions.AllowlistedPackageIds.Add(packageId);
            }
        }

        services.AddSingleton(normalizedOptions);
        services.AddSingleton<IDesiredPackageSource>(sp =>
            new DirectoryNupkgDesiredSource(
                normalizedOptions.SourceName,
                normalizedOptions.DirectoryPath,
                normalizedOptions.AllowlistedPackageIds));

        if (normalizedOptions.TriggerReconciliationOnChange)
        {
            services.AddHostedService<DirectorySourceReconciliationTriggerHostedService>();
        }

        return services;
    }
}

internal sealed class DirectorySourceReconciliationTriggerHostedService : BackgroundService
{
    private readonly DirectorySourceOptions options;
    private readonly IReconciliationService reconciliationService;
    private readonly ILogger<DirectorySourceReconciliationTriggerHostedService> logger;
    private readonly Channel<bool> changes = Channel.CreateUnbounded<bool>();
    private FileSystemWatcher? watcher;

    public DirectorySourceReconciliationTriggerHostedService(
        DirectorySourceOptions options,
        IReconciliationService reconciliationService,
        ILogger<DirectorySourceReconciliationTriggerHostedService> logger)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.reconciliationService = reconciliationService ?? throw new ArgumentNullException(nameof(reconciliationService));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(options.DirectoryPath);

        watcher = new FileSystemWatcher(options.DirectoryPath, "*.nupkg")
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };

        watcher.Created += OnChanged;
        watcher.Changed += OnChanged;
        watcher.Deleted += OnChanged;
        watcher.Renamed += OnRenamed;

        logger.LogInformation(
            "Directory watcher enabled for Nuplane desired-state source at '{DirectoryPath}' with debounce {DebounceMs}ms.",
            options.DirectoryPath,
            (int)options.DebounceWindow.TotalMilliseconds);

        try
        {
            while (await changes.Reader.WaitToReadAsync(stoppingToken))
            {
                while (changes.Reader.TryRead(out _))
                {
                }

                await Task.Delay(options.DebounceWindow, stoppingToken);

                if (changes.Reader.TryRead(out _))
                {
                    changes.Writer.TryWrite(true);
                    continue;
                }

                try
                {
                    var result = await reconciliationService.TriggerManualAsync(stoppingToken);
                    logger.LogInformation(
                        "Directory-triggered reconcile completed. Skipped={Skipped}, Degraded={IsDegraded}.",
                        result.Skipped,
                        result.IsDegraded);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Directory-triggered reconcile failed.");
                }
            }
        }
        finally
        {
            if (watcher is not null)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Created -= OnChanged;
                watcher.Changed -= OnChanged;
                watcher.Deleted -= OnChanged;
                watcher.Renamed -= OnRenamed;
                watcher.Dispose();
                watcher = null;
            }
        }
    }

    private void OnChanged(object? _, FileSystemEventArgs __)
    {
        changes.Writer.TryWrite(true);
    }

    private void OnRenamed(object? _, RenamedEventArgs __)
    {
        changes.Writer.TryWrite(true);
    }
}