using System.Diagnostics;
using HomeLink.Models;
using HomeLink.Telemetry;

namespace HomeLink.Services;

public class LocationEnrichmentWorker : BackgroundService
{
    private readonly LocationEnrichmentQueue _queue;
    private readonly LocationService _locationService;
    private readonly StatePersistenceService _statePersistenceService;
    private readonly DisplayFrameCacheService _displayFrameCache;
    private readonly ILogger<LocationEnrichmentWorker> _logger;
    private readonly TelemetryDashboardState _dashboardState;

    public LocationEnrichmentWorker(
        LocationEnrichmentQueue queue,
        LocationService locationService,
        StatePersistenceService statePersistenceService,
        DisplayFrameCacheService displayFrameCache,
        ILogger<LocationEnrichmentWorker> logger,
        TelemetryDashboardState dashboardState)
    {
        _queue = queue;
        _locationService = locationService;
        _statePersistenceService = statePersistenceService;
        _displayFrameCache = displayFrameCache;
        _logger = logger;
        _dashboardState = dashboardState;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                LocationEnrichmentJob job = await _queue.DequeueAsync(stoppingToken);
                double enqueueToStartLagMs = Math.Max(0, (DateTimeOffset.UtcNow - job.EnqueuedUtc).TotalMilliseconds);
                HomeLinkTelemetry.WorkerQueueEnqueueToStartLagMs.Record(
                    enqueueToStartLagMs,
                    new KeyValuePair<string, object?>("queue", "location_enrichment"),
                    new KeyValuePair<string, object?>("worker", nameof(LocationEnrichmentWorker)));
                _dashboardState.RecordWorkerQueueLag("location_enrichment", nameof(LocationEnrichmentWorker), enqueueToStartLagMs, _queue.Depth);

                long processingStart = Stopwatch.GetTimestamp();
                LocationInfo? enriched = await _locationService.EnrichLocationAsync(job.RawSnapshot);

                if (enriched == null)
                {
                    double noResultDurationMs = Stopwatch.GetElapsedTime(processingStart).TotalMilliseconds;
                    HomeLinkTelemetry.WorkerQueueProcessingDurationMs.Record(
                        noResultDurationMs,
                        new KeyValuePair<string, object?>("queue", "location_enrichment"),
                        new KeyValuePair<string, object?>("worker", nameof(LocationEnrichmentWorker)),
                        new KeyValuePair<string, object?>("result", "empty"));
                    _dashboardState.RecordWorkerQueueProcessing("location_enrichment", nameof(LocationEnrichmentWorker), noResultDurationMs, _queue.Depth);
                    continue;
                }

                _locationService.SetCachedLocation(enriched);
                long persistenceStart = Stopwatch.GetTimestamp();
                await _statePersistenceService.SaveLocationAsync(enriched);
                double persistenceDurationMs = Stopwatch.GetElapsedTime(persistenceStart).TotalMilliseconds;
                HomeLinkTelemetry.LocationPersistenceDurationMs.Record(
                    persistenceDurationMs,
                    new KeyValuePair<string, object?>("component", nameof(LocationEnrichmentWorker)),
                    new KeyValuePair<string, object?>("stage", "persistence"));
                _dashboardState.RecordLocationStage("persistence", persistenceDurationMs);
                _displayFrameCache.SignalRenderNeeded();

                double processingDurationMs = Stopwatch.GetElapsedTime(processingStart).TotalMilliseconds;
                HomeLinkTelemetry.WorkerQueueProcessingDurationMs.Record(
                    processingDurationMs,
                    new KeyValuePair<string, object?>("queue", "location_enrichment"),
                    new KeyValuePair<string, object?>("worker", nameof(LocationEnrichmentWorker)),
                    new KeyValuePair<string, object?>("result", "ok"));
                _dashboardState.RecordWorkerQueueProcessing("location_enrichment", nameof(LocationEnrichmentWorker), processingDurationMs, _queue.Depth);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Location enrichment worker failed while processing queued location update.");
            }
        }
    }
}
