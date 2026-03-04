using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    public static IServiceCollection AddNuplaneDirectorySource(
        this IServiceCollection services,
        Action<DirectorySourceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddSingleton<IValidateOptions<DirectorySourceOptions>, DirectorySourceOptionsValidator>();

        services
            .AddOptions<DirectorySourceOptions>()
            .Configure(configure)
            .PostConfigure(options =>
            {
                if (!string.IsNullOrWhiteSpace(options.DirectoryPath))
                {
                    options.DirectoryPath = Path.GetFullPath(options.DirectoryPath);
                }

                if (string.IsNullOrWhiteSpace(options.SourceName))
                {
                    options.SourceName = "Directory.Drop";
                }

                var validIds = options.AllowlistedPackageIds
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .ToList();
                options.AllowlistedPackageIds.Clear();
                foreach (var id in validIds)
                {
                    options.AllowlistedPackageIds.Add(id);
                }
            })
            .ValidateOnStart();

        services.AddSingleton(sp => sp.GetRequiredService<IOptions<DirectorySourceOptions>>().Value);

        services.AddSingleton<IDesiredPackageSource>(sp =>
{
    var opts = sp.GetRequiredService<DirectorySourceOptions>();
    return new DirectoryNupkgDesiredSource(
        opts.SourceName,
        opts.DirectoryPath,
        opts.AllowlistedPackageIds,
        sp.GetService<ILogger<DirectoryNupkgDesiredSource>>());
});

        // Preview options to conditionally register the hosted service.
        var preview = new DirectorySourceOptions();
        configure(preview);

        if (preview.TriggerReconciliationOnChange)
        {
            var capturedOptions = normalizedOptions;
            services.AddSingleton<IHostedService>(sp =>
                new DirectorySourceReconciliationTriggerHostedService(
                    capturedOptions,
                    sp.GetRequiredService<IReconciliationService>(),
                    sp.GetRequiredService<ILogger<DirectorySourceReconciliationTriggerHostedService>>()));
        }

        return services;
    }
}

internal sealed class DirectorySourceReconciliationTriggerHostedService : BackgroundService
{
    private readonly DirectorySourceOptions options;
    private readonly IReconciliationService reconciliationService;
    private readonly ILogger<DirectorySourceReconciliationTriggerHostedService> logger;
    private readonly Channel<bool> changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest
    });
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

        watcher = new(options.DirectoryPath, "*.nupkg")
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite
        };

        watcher.Created += OnChanged;
        watcher.Changed += OnChanged;
        watcher.Deleted += OnChanged;
        watcher.Renamed += OnRenamed;
        watcher.EnableRaisingEvents = true;

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