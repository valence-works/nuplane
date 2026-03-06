using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nuplane.Hosting;
using Nuplane.Runtime.Health;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Runtime.Reconciliation.Models;

namespace Nuplane.DirectorySource.Hosting;

/// <summary>
/// A hosted service that triggers the reconciliation process for a directory source.
/// This service monitors changes in the specified directory and triggers reconciliation based on configuration options.
/// </summary>
internal sealed class DirectorySourceReconciliationTriggerHostedService(
    DirectorySourceOptions options,
    IReconciliationService reconciliationService,
    ILogger<DirectorySourceReconciliationTriggerHostedService> logger,
    WatcherDegradationTracker? watcherDegradationTracker = null)
    : BackgroundService
{
    private readonly DirectorySourceOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly IReconciliationService _reconciliationService = reconciliationService ?? throw new ArgumentNullException(nameof(reconciliationService));
    private readonly ILogger<DirectorySourceReconciliationTriggerHostedService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly DebouncedDirtySignal _reconciliationSignal = new(options?.DebounceWindow ?? throw new ArgumentNullException(nameof(options)));

    private FileSystemWatcher? _watcher;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(_options.DirectoryPath);

        try
        {
            _watcher = new(_options.DirectoryPath, "*.nupkg")
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite
            };

            _watcher.Created += OnChanged;
            _watcher.Changed += OnChanged;
            _watcher.Deleted += OnChanged;
            _watcher.Renamed += OnRenamed;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Directory watcher failed to start for feed '{FeedName}' at '{DirectoryPath}'. Falling back to scheduled reconciliation only.",
                _options.FeedName,
                _options.DirectoryPath);
            watcherDegradationTracker?.MarkDegraded();
            return;
        }

        _logger.LogInformation(
            "Directory watcher enabled for feed '{FeedName}' at '{DirectoryPath}' (debounce: {DebounceMs}ms, trigger type: {TriggerType}).",
            _options.FeedName,
            _options.DirectoryPath,
            (int)_options.DebounceWindow.TotalMilliseconds,
            nameof(TriggerType.DirectoryChange));

        try
        {
            while (true)
            {
                await _reconciliationSignal.WaitForNextSettledSignalAsync(stoppingToken);

                try
                {
                    var trigger = new ReconciliationTrigger(TriggerType.DirectoryChange, Source: _options.FeedName);
                    var result = await _reconciliationService.TriggerAsync(trigger, stoppingToken);
                    _logger.LogInformation(
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
                    _logger.LogWarning(ex, "Directory-triggered reconcile failed.");
                }
            }
        }
        finally
        {
            if (_watcher is not null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Created -= OnChanged;
                _watcher.Changed -= OnChanged;
                _watcher.Deleted -= OnChanged;
                _watcher.Renamed -= OnRenamed;
                _watcher.Dispose();
                _watcher = null;
            }
        }
    }

    private void OnChanged(object? _, FileSystemEventArgs __)
    {
        _reconciliationSignal.Signal();
    }

    private void OnRenamed(object? _, RenamedEventArgs __)
    {
        _reconciliationSignal.Signal();
    }
}