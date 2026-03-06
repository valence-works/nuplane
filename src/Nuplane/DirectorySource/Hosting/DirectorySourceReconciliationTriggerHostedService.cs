using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nuplane.Hosting;
using Nuplane.Runtime.Health;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Runtime.Reconciliation.Models;

namespace Nuplane.DirectorySource.Hosting;

/// <summary>
/// A hosted service that monitors a directory source and queues reconciliation triggers when it changes.
/// </summary>
internal sealed class DirectorySourceReconciliationTriggerHostedService(
    DirectorySourceOptions options,
    IReconciliationTriggerIngress triggerSink,
    ILogger<DirectorySourceReconciliationTriggerHostedService> logger,
    ObservationDegradationTracker? observationDegradationTracker = null)
    : BackgroundService
{
    private readonly DirectorySourceOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly IReconciliationTriggerIngress _triggerSink = triggerSink ?? throw new ArgumentNullException(nameof(triggerSink));
    private readonly ILogger<DirectorySourceReconciliationTriggerHostedService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly DebouncedDirtySignal _reconciliationSignal = new(options.DebounceWindow);

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
            observationDegradationTracker?.MarkDegraded();
            return;
        }

        _logger.LogInformation(
            "Directory watcher enabled for feed '{FeedName}' at '{DirectoryPath}' (debounce: {DebounceMs}ms, trigger type: {TriggerType}).",
            _options.FeedName,
            _options.DirectoryPath,
            (int)_options.DebounceWindow.TotalMilliseconds,
            nameof(TriggerType.ObservedChange));

        try
        {
            while (true)
            {
                await _reconciliationSignal.WaitForNextSettledSignalAsync(stoppingToken);

                try
                {
                    _triggerSink.Enqueue(new ReconciliationTrigger(
                        TriggerType.ObservedChange,
                        Source: _options.FeedName,
                        ObservationKind: FeedObservationKind.DirectoryWatcher));
                    _logger.LogDebug("Queued observed-change reconciliation trigger for feed '{FeedName}'.", _options.FeedName);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to queue observed-change reconciliation trigger for feed '{FeedName}'.", _options.FeedName);
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