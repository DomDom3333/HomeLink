using HomeLink.Models;
using SpotifyAPI.Web;

namespace HomeLink.Services;

public class SpotifyService
{
    private readonly string? _clientId;
    private readonly string? _clientSecret;
    private readonly object _tokenLock = new();
    private readonly object _cacheLock = new();
    
    private string? _accessToken;
    private string? _refreshToken;
    private DateTime _tokenExpiry = DateTime.MinValue;
    private SpotifyTrackInfo? _lastTrackInfo;
    // Track when we last synchronized with Spotify and the device-reported progress at that time
    private DateTime _lastSyncUtc = DateTime.MinValue;

    public SpotifyService(string? clientId, string? clientSecret, string? refreshToken = null, DateTime? expiry = null)
    {
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
            throw new InvalidOperationException("Spotify is not authorized. Please provide SPOTIFY_REFRESH_TOKEN in environment variables or complete the OAuth flow first.");
        }

        // Refresh token if expired or about to expire
        if (DateTime.UtcNow >= expiry || string.IsNullOrEmpty(accessToken))
        {
            SpotifyClientConfig config = SpotifyClientConfig.CreateDefault();

            // Use AuthorizationCodeRefreshRequest which accepts clientId and clientSecret but they can be empty
            // When clientSecret is null/empty, some Spotify apps (PKCE/public client) only require client_id.
            // If Spotify requires client authentication and none is provided, the request will fail and we surface the error.
            AuthorizationCodeRefreshRequest refreshRequest = new AuthorizationCodeRefreshRequest(_clientId ?? string.Empty, _clientSecret ?? string.Empty, refreshToken);
            AuthorizationCodeRefreshResponse response = await new OAuthClient(config).RequestToken(refreshRequest);

            lock (_tokenLock)
            {
                _accessToken = response.AccessToken;
                // Spotify may or may not return a new refresh token. Preserve the existing one if not returned.
                if (!string.IsNullOrEmpty(response.RefreshToken))
                    _refreshToken = response.RefreshToken;

                _tokenExpiry = DateTime.UtcNow.AddSeconds(response.ExpiresIn - 60);
                accessToken = _accessToken;
            }
        }

        return new SpotifyClient(SpotifyClientConfig.CreateDefault().WithToken(accessToken!));
    }

    // Computes an optimistic, locally advanced progress for the cached track, if applicable.
    private SpotifyTrackInfo? GetOptimisticallyAdvancedCachedTrack()
    {
        lock (_cacheLock)
        {
            if (_lastTrackInfo == null)
                return null;

            // If we know it's playing, advance progress by the elapsed wall-clock time since last sync.
            if (_lastTrackInfo.IsPlaying)
            {
                DateTime now = DateTime.UtcNow;
                long elapsedMs = (long)(now - _lastSyncUtc).TotalMilliseconds;
                if (elapsedMs > 0)
                {
                    long advancedProgress = _lastTrackInfo.ProgressMs + elapsedMs;
                    // Cap at duration
                    if (advancedProgress >= _lastTrackInfo.DurationMs)
                    {
                        // If we reached or passed the end, mark as not playing and cap progress
                        _lastTrackInfo.ProgressMs = _lastTrackInfo.DurationMs;
                        _lastTrackInfo.IsPlaying = false;
                    }
                    else
                    {
                        _lastTrackInfo.ProgressMs = advancedProgress;
                    }
                    // Move the last sync cursor forward so subsequent calls only add new elapsed time
                    _lastSyncUtc = now;
                }
                // Return a shallow copy to avoid external mutation of our cache
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
                    IsPlaying = _lastTrackInfo.IsPlaying
                };
            }

            // If paused and we have cached info, just return it as-is.
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

    public async Task<SpotifyTrackInfo?> GetCurrentlyPlayingAsync()
    {
        // First, if we have cached info and the song hasn't ended yet, return the locally advanced value
        SpotifyTrackInfo? cached = GetOptimisticallyAdvancedCachedTrack();
        if (cached != null && cached.IsPlaying)
        {
            // We can safely return without polling if we're still within the track duration
            if (cached.ProgressMs < cached.DurationMs)
            {
                return cached;
            }
        }

        SpotifyClient client = await GetClientAsync();
        CurrentlyPlaying? currentlyPlaying = await client.Player.GetCurrentlyPlaying(new PlayerCurrentlyPlayingRequest());
        
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
                return _lastTrackInfo;
            }

            if (_lastTrackInfo != null)
            {
                // If nothing is playing, return the last known track but marked as paused
                _lastTrackInfo.IsPlaying = false;
                return _lastTrackInfo;
            }
        }

        return null;
    }
}