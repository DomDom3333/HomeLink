using System.Diagnostics;
using HomeLink.Models;

namespace HomeLink.Services;

public sealed class DisplayRenderWorker : BackgroundService
{
    private static readonly TimeSpan DefaultPlayingPollInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DefaultPausedPollInterval = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan DefaultIdlePollInterval = TimeSpan.FromSeconds(45);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SpotifyService _spotifyService;
    private readonly LocationService _locationService;
    private readonly DisplayFrameCacheService _displayFrameCache;
    private readonly ILogger<DisplayRenderWorker> _logger;

    private readonly TimeSpan _playingPollInterval;
    private readonly TimeSpan _pausedPollInterval;
    private readonly TimeSpan _idlePollInterval;
    private readonly TimeSpan _playingCacheStaleness;
    private readonly TimeSpan _pausedCacheStaleness;

    private string? _lastSourceHash;

    public DisplayRenderWorker(
        IServiceScopeFactory scopeFactory,
        SpotifyService spotifyService,
        LocationService locationService,
        DisplayFrameCacheService displayFrameCache,
        ILogger<DisplayRenderWorker> logger,
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _spotifyService = spotifyService;
        _locationService = locationService;
        _displayFrameCache = displayFrameCache;
        _logger = logger;

        _playingPollInterval = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue<int?>("DisplayRender:PlayingPollIntervalSeconds") ?? (int)DefaultPlayingPollInterval.TotalSeconds, 5, 120));
        _pausedPollInterval = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue<int?>("DisplayRender:PausedPollIntervalSeconds") ?? (int)DefaultPausedPollInterval.TotalSeconds, 15, 600));
        _idlePollInterval = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue<int?>("DisplayRender:IdlePollIntervalSeconds") ?? (int)DefaultIdlePollInterval.TotalSeconds, 10, 300));

        // Staleness gate: do not call Spotify if local cache is fresh enough.
        _playingCacheStaleness = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue<int?>("DisplayRender:PlayingCacheStalenessSeconds") ?? (int)(_playingPollInterval.TotalSeconds - 2), 3, 120));
        _pausedCacheStaleness = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue<int?>("DisplayRender:PausedCacheStalenessSeconds") ?? (int)(_pausedPollInterval.TotalSeconds - 5), 10, 600));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "DisplayRenderWorker started. Poll intervals (playing/paused/idle): {Playing}s/{Paused}s/{Idle}s. Spotify cache staleness (playing/paused): {PlayingStaleness}s/{PausedStaleness}s",
            _playingPollInterval.TotalSeconds,
            _pausedPollInterval.TotalSeconds,
            _idlePollInterval.TotalSeconds,
            _playingCacheStaleness.TotalSeconds,
            _pausedCacheStaleness.TotalSeconds);

        TimeSpan nextWait = TimeSpan.Zero;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                nextWait = await RenderIfChangedAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DisplayRenderWorker iteration failed.");
                nextWait = _idlePollInterval;
            }

            await _displayFrameCache.WaitForSignalOrIntervalAsync(nextWait, stoppingToken);
        }

        _logger.LogInformation("DisplayRenderWorker stopping.");
    }

    private async Task<TimeSpan> RenderIfChangedAsync(CancellationToken cancellationToken)
    {
        if (!_spotifyService.IsAuthorized)
        {
            _logger.LogDebug("DisplayRenderWorker skipped: Spotify unauthorized.");
            return _pausedPollInterval;
        }

        DisplayRenderRequestOptions options = _displayFrameCache.GetRequestedRenderOptions();

        SpotifyTrackInfo? cachedTrack = _spotifyService.GetCachedTrackSnapshot();
        bool likelyPlaying = cachedTrack?.IsPlaying == true;
        TimeSpan maxCacheStaleness = likelyPlaying ? _playingCacheStaleness : _pausedCacheStaleness;

        SpotifyTrackInfo? spotifyData = await _spotifyService.GetCurrentlyPlayingAsync(maxCacheStaleness);
        LocationInfo? locationData = _locationService.GetCachedLocation();

        string sourceHash = DisplayFrameHashService.ComputeSourceHash(spotifyData, locationData, options.Dither, options.DeviceBattery);
        bool sourceChanged = !string.Equals(_lastSourceHash, sourceHash, StringComparison.Ordinal);
        if (!sourceChanged)
        {
            return spotifyData?.IsPlaying == true ? _playingPollInterval : _pausedPollInterval;
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        using IServiceScope scope = _scopeFactory.CreateScope();
        DrawingService drawingService = scope.ServiceProvider.GetRequiredService<DrawingService>();
        EInkBitmap bitmap = await drawingService.DrawDisplayDataAsync(spotifyData, locationData, options.Dither, options.DeviceBattery);
        stopwatch.Stop();

        string diagnostics =
            $"spotify_present:{spotifyData != null};spotify_playing:{spotifyData?.IsPlaying == true};location_present:{locationData != null};max_staleness_ms:{maxCacheStaleness.TotalMilliseconds:F0};duration_ms:{stopwatch.Elapsed.TotalMilliseconds:F1}";

        _displayFrameCache.UpdateFrame(bitmap, sourceHash, DateTimeOffset.UtcNow, stopwatch.Elapsed, diagnostics, options.Dither, options.DeviceBattery);
        _lastSourceHash = sourceHash;

        _logger.LogInformation(
            "DisplayRenderWorker updated frame. Width: {Width}, Height: {Height}, Bytes: {Bytes}, SourceHash: {SourceHash}",
            bitmap.Width,
            bitmap.Height,
            bitmap.PackedData.Length,
            sourceHash);

        return spotifyData?.IsPlaying == true ? _playingPollInterval : _pausedPollInterval;
    }
}
