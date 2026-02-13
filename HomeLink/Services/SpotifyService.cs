using System.Diagnostics;
using HomeLink.Models;
using HomeLink.Telemetry;
using Microsoft.Extensions.Logging;
using SpotifyAPI.Web;

namespace HomeLink.Services;

public class SpotifyService
{
    private readonly ILogger<SpotifyService> _logger;
    private readonly string? _clientId;
    private readonly string? _clientSecret;
    private readonly TelemetryDashboardState _dashboardState;
    private readonly StatePersistenceService _statePersistenceService;
    private readonly object _tokenLock = new();
    private readonly object _cacheLock = new();
    private readonly SemaphoreSlim _refreshSemaphore = new(1, 1);
    
    private string? _accessToken;
    private string? _refreshToken;
    private DateTime _tokenExpiry = DateTime.MinValue;
    private SpotifyTrackInfo? _lastTrackInfo;
    // Track when we last synchronized with Spotify and the device-reported progress at that time
    private DateTime _lastSyncUtc = DateTime.MinValue;
    private static readonly TimeSpan MaxOptimisticCacheAge = TimeSpan.FromSeconds(15);

    public SpotifyService(ILogger<SpotifyService> logger, TelemetryDashboardState dashboardState, StatePersistenceService statePersistenceService, string? clientId, string? clientSecret, string? refreshToken = null, DateTime? expiry = null)
    {
        _logger = logger;
        _dashboardState = dashboardState;
        _statePersistenceService = statePersistenceService;
        _clientId = string.IsNullOrWhiteSpace(clientId) ? null : clientId;
        _clientSecret = string.IsNullOrWhiteSpace(clientSecret) ? null : clientSecret;

        // If tokens are provided (for example from env vars), initialize them here
        if (!string.IsNullOrEmpty(refreshToken) || expiry.HasValue)
        {
            lock (_tokenLock)
            {
                _refreshToken = refreshToken;
                _tokenExpiry = expiry ?? DateTime.MinValue;
            }
        }

        (SpotifyTrackInfo TrackInfo, DateTime LastSyncUtc)? persisted = _statePersistenceService.LoadSpotifyTrackAsync().GetAwaiter().GetResult();
        if (persisted.HasValue)
        {
            lock (_cacheLock)
            {
                _lastTrackInfo = persisted.Value.TrackInfo;
                _lastSyncUtc = persisted.Value.LastSyncUtc;
            }
        }
    }

    /// <summary>
    /// Checks if the service has been authorized with a user token.
    /// </summary>
    public bool IsAuthorized
    {
        get
        {
            lock (_tokenLock)
            {
                return !string.IsNullOrEmpty(_refreshToken);
            }
        }
    }

    private async Task<SpotifyClient> GetClientAsync()
    {
        string? accessToken = await EnsureAccessTokenAsync(forceRefresh: false);
        return new SpotifyClient(SpotifyClientConfig.CreateDefault().WithToken(accessToken));
    }

    private async Task<string> EnsureAccessTokenAsync(bool forceRefresh)
    {
        string? accessToken;
        string? refreshToken;
        DateTime expiry;

        lock (_tokenLock)
        {
            accessToken = _accessToken;
            refreshToken = _refreshToken;
            expiry = _tokenExpiry;
        }

        if (string.IsNullOrEmpty(refreshToken))
        {
            _logger.LogWarning("Spotify token refresh requested but no refresh token is configured.");
            throw new InvalidOperationException("Spotify is not authorized. Please provide SPOTIFY_REFRESH_TOKEN in environment variables or complete the OAuth flow first.");
        }

        if (!forceRefresh && !string.IsNullOrEmpty(accessToken) && DateTime.UtcNow < expiry)
        {
            return accessToken;
        }

        await _refreshSemaphore.WaitAsync();
        try
        {
            // Another request may have refreshed while we were waiting.
            lock (_tokenLock)
            {
                accessToken = _accessToken;
                refreshToken = _refreshToken;
                expiry = _tokenExpiry;
            }

            if (!forceRefresh && !string.IsNullOrEmpty(accessToken) && DateTime.UtcNow < expiry)
            {
                return accessToken;
            }

            _logger.LogInformation("Refreshing Spotify access token (forceRefresh: {ForceRefresh}).", forceRefresh);

            SpotifyClientConfig config = SpotifyClientConfig.CreateDefault();
            AuthorizationCodeRefreshRequest refreshRequest = new AuthorizationCodeRefreshRequest(_clientId ?? string.Empty, _clientSecret ?? string.Empty, refreshToken!);
            AuthorizationCodeRefreshResponse response = await new OAuthClient(config).RequestToken(refreshRequest);

            lock (_tokenLock)
            {
                _accessToken = response.AccessToken;
                if (!string.IsNullOrEmpty(response.RefreshToken))
                    _refreshToken = response.RefreshToken;

                _tokenExpiry = DateTime.UtcNow.AddSeconds(response.ExpiresIn - 60);
                _logger.LogInformation("Spotify access token refreshed successfully. Expires at {ExpiryUtc}.", _tokenExpiry);
                return _accessToken!;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh Spotify access token.");
            throw;
        }
        finally
        {
            _refreshSemaphore.Release();
        }
    }

    // Computes an optimistic, locally advanced progress for the cached track, if applicable.
    private SpotifyTrackInfo? GetOptimisticallyAdvancedCachedTrack()
    {
        lock (_cacheLock)
        {
            if (_lastTrackInfo == null)
                return null;

            // If we know it's playing, compute an optimistic progress without mutating the cache.
            if (_lastTrackInfo.IsPlaying)
            {
                DateTime now = DateTime.UtcNow;

                // If _lastSyncUtc is uninitialized, don't advance optimistically to avoid huge jumps.
                long elapsedMs = _lastSyncUtc == DateTime.MinValue ? 0L : (long)(now - _lastSyncUtc).TotalMilliseconds;

                long progressToReturn = _lastTrackInfo.ProgressMs;

                if (elapsedMs > 0)
                {
                    long advancedProgress = _lastTrackInfo.ProgressMs + elapsedMs;
                    // Cap at duration
                    if (advancedProgress >= _lastTrackInfo.DurationMs)
                    {
                        progressToReturn = _lastTrackInfo.DurationMs;
                        // Return a copy marked as not playing (do not mutate cached state)
                        return new SpotifyTrackInfo
                        {
                            Title = _lastTrackInfo.Title,
                            Artist = _lastTrackInfo.Artist,
                            Album = _lastTrackInfo.Album,
                            AlbumCoverUrl = _lastTrackInfo.AlbumCoverUrl,
                            ProgressMs = progressToReturn,
                            DurationMs = _lastTrackInfo.DurationMs,
                            SpotifyUri = _lastTrackInfo.SpotifyUri,
                            ScannableCodeUrl = _lastTrackInfo.ScannableCodeUrl,
                            IsPlaying = false
                        };
                    }

                    progressToReturn = advancedProgress;
                }

                // Still playing, return advanced progress (no cache mutation, no _lastSyncUtc update)
                return new SpotifyTrackInfo
                {
                    Title = _lastTrackInfo.Title,
                    Artist = _lastTrackInfo.Artist,
                    Album = _lastTrackInfo.Album,
                    AlbumCoverUrl = _lastTrackInfo.AlbumCoverUrl,
                    ProgressMs = progressToReturn,
                    DurationMs = _lastTrackInfo.DurationMs,
                    SpotifyUri = _lastTrackInfo.SpotifyUri,
                    ScannableCodeUrl = _lastTrackInfo.ScannableCodeUrl,
                    IsPlaying = true
                };
            }

            // If paused and we have cached info, just return a copy as-is.
            return new SpotifyTrackInfo
            {
                Title = _lastTrackInfo.Title,
                Artist = _lastTrackInfo.Artist,
                Album = _lastTrackInfo.Album,
                AlbumCoverUrl = _lastTrackInfo.AlbumCoverUrl,
                ProgressMs = _lastTrackInfo.ProgressMs,
                DurationMs = _lastTrackInfo.DurationMs,
                SpotifyUri = _lastTrackInfo.SpotifyUri,
                ScannableCodeUrl = _lastTrackInfo.ScannableCodeUrl,
                IsPlaying = false
            };
        }
    }

    private DateTime GetLastSyncUtc()
    {
        lock (_cacheLock)
        {
            return _lastSyncUtc;
        }
    }

    public SpotifyTrackInfo? GetCachedTrackSnapshot()
    {
        return GetOptimisticallyAdvancedCachedTrack();
    }

    public async Task<SpotifyTrackInfo?> GetCurrentlyPlayingAsync(TimeSpan? maxCacheStaleness)
    {
        long startTimestamp = Stopwatch.GetTimestamp();
        bool isError = false;
        HomeLinkTelemetry.SpotifyRequests.Add(1);

        using Activity? activity = HomeLinkTelemetry.ActivitySource.StartActivity("SpotifyService.GetCurrentlyPlaying", ActivityKind.Internal);

        try
        {
            SpotifyTrackInfo? cached = GetOptimisticallyAdvancedCachedTrack();
            DateTime lastSyncUtc = GetLastSyncUtc();
            TimeSpan cacheAge = DateTime.UtcNow - lastSyncUtc;

            if (cached != null && maxCacheStaleness.HasValue && cacheAge <= maxCacheStaleness.Value)
            {
                activity?.SetTag("spotify.cache_hit", true);
                activity?.SetTag("spotify.cache_age_ms", cacheAge.TotalMilliseconds);
                return cached;
            }

            // First, if we have cached info and the song hasn't ended yet, return the locally advanced value
            if (cached != null && cached.IsPlaying)
            {
                // Only trust optimistic cache for a short window to detect skips/pauses promptly.
                if (cached.ProgressMs < cached.DurationMs && cacheAge <= MaxOptimisticCacheAge)
                {
                    activity?.SetTag("spotify.cache_hit", true);
                    activity?.SetTag("spotify.cache_age_ms", cacheAge.TotalMilliseconds);
                    return cached;
                }
            }

            SpotifyClient client = await GetClientAsync();
            CurrentlyPlaying? currentlyPlaying;
            try
            {
                _logger.LogInformation("Calling Spotify currently-playing endpoint.");
                currentlyPlaying = await client.Player.GetCurrentlyPlaying(new PlayerCurrentlyPlayingRequest());
                _logger.LogInformation("Spotify currently-playing response received. HasItem: {HasItem}, IsPlaying: {IsPlaying}.", currentlyPlaying?.Item != null, currentlyPlaying?.IsPlaying);
            }
            catch (APIUnauthorizedException)
            {
                // Access tokens can be invalidated before local expiry. Force a refresh and retry once.
                _logger.LogWarning("Spotify currently-playing call returned 401 Unauthorized. Forcing token refresh and retrying once.");
                string accessToken = await EnsureAccessTokenAsync(forceRefresh: true);
                SpotifyClient refreshedClient = new SpotifyClient(SpotifyClientConfig.CreateDefault().WithToken(accessToken));
                _logger.LogInformation("Retrying Spotify currently-playing endpoint after forced token refresh.");
                currentlyPlaying = await refreshedClient.Player.GetCurrentlyPlaying(new PlayerCurrentlyPlayingRequest());
                _logger.LogInformation("Spotify currently-playing retry response received. HasItem: {HasItem}, IsPlaying: {IsPlaying}.", currentlyPlaying?.Item != null, currentlyPlaying?.IsPlaying);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Spotify currently-playing request failed.");
                throw;
            }

            SpotifyTrackInfo? trackToPersist = null;
            DateTime trackSyncUtc = DateTime.MinValue;
            SpotifyTrackInfo? trackToReturn = null;

            lock (_cacheLock)
            {
                if (currentlyPlaying?.Item is FullTrack track)
                {
                    _lastTrackInfo = new SpotifyTrackInfo
                    {
                        Title = track.Name,
                        Artist = string.Join(", ", track.Artists.Select(a => a.Name)),
                        Album = track.Album.Name,
                        AlbumCoverUrl = track.Album.Images.FirstOrDefault()?.Url ?? string.Empty,
                        ProgressMs = currentlyPlaying.ProgressMs ?? 0,
                        DurationMs = track.DurationMs,
                        SpotifyUri = track.Uri,
                        ScannableCodeUrl = $"https://scannables.scdn.co/uri/plain/jpeg/000000/white/640/spotify:track:{track.Id}",
                        IsPlaying = currentlyPlaying.IsPlaying
                    };
                    _lastSyncUtc = DateTime.UtcNow;
                    trackToPersist = _lastTrackInfo;
                    trackSyncUtc = _lastSyncUtc;
                    trackToReturn = _lastTrackInfo;
                }
                else
                {
                    if (currentlyPlaying == null)
                    {
                        _logger.LogWarning("Spotify currently-playing response was empty.");
                    }

                    if (_lastTrackInfo != null)
                    {
                        // If nothing is playing, return the last known track but marked as paused
                        _lastTrackInfo.IsPlaying = false;
                        trackToPersist = _lastTrackInfo;
                        trackSyncUtc = _lastSyncUtc;
                        trackToReturn = _lastTrackInfo;
                    }
                }
            }

            if (trackToPersist != null)
            {
                await _statePersistenceService.SaveSpotifyTrackAsync(trackToPersist, trackSyncUtc);
                return trackToReturn;
            }

            return null;
        }
        catch (Exception ex)
        {
            isError = true;
            activity?.SetTag("error", true);
            activity?.AddException(ex);

            SpotifyTrackInfo? fallbackTrack = GetOptimisticallyAdvancedCachedTrack();
            if (fallbackTrack != null)
            {
                _logger.LogWarning(ex, "Spotify request failed, returning last cached Spotify state from persistence/memory.");
                return fallbackTrack;
            }

            throw;
        }
        finally
        {
            double elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            HomeLinkTelemetry.SpotifyRequestDurationMs.Record(elapsedMs);
            _dashboardState.RecordSpotify(elapsedMs, isError);
            activity?.SetTag("spotify.request.duration_ms", elapsedMs);
        }
    }

    public Task<SpotifyTrackInfo?> GetCurrentlyPlayingAsync()
    {
        return GetCurrentlyPlayingAsync(maxCacheStaleness: null);
    }

}
