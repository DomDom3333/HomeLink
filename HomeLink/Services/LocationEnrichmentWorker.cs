using HomeLink.Models;

namespace HomeLink.Services;

public class LocationEnrichmentWorker : BackgroundService
{
    private readonly LocationEnrichmentQueue _queue;
    private readonly LocationService _locationService;
    private readonly StatePersistenceService _statePersistenceService;
    private readonly DisplayFrameCacheService _displayFrameCache;
    private readonly ILogger<LocationEnrichmentWorker> _logger;

    public LocationEnrichmentWorker(
        LocationEnrichmentQueue queue,
        LocationService locationService,
        StatePersistenceService statePersistenceService,
        DisplayFrameCacheService displayFrameCache,
        ILogger<LocationEnrichmentWorker> logger)
    {
        _queue = queue;
        _locationService = locationService;
        _statePersistenceService = statePersistenceService;
        _displayFrameCache = displayFrameCache;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                LocationEnrichmentJob job = await _queue.DequeueAsync(stoppingToken);
                LocationInfo? enriched = await _locationService.EnrichLocationAsync(job.RawSnapshot);

                if (enriched == null)
                    continue;

                _locationService.SetCachedLocation(enriched);
                await _statePersistenceService.SaveLocationAsync(enriched);
                _displayFrameCache.SignalRenderNeeded();
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
