using SpotifyAPI.Web;

namespace HomeLink.Services;

public class SpotifyTrackInfo
{
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string AlbumCoverUrl { get; set; } = string.Empty;
    public long ProgressMs { get; set; }
    public long DurationMs { get; set; }
    public string SpotifyUri { get; set; } = string.Empty;
    public string ScannableCodeUrl { get; set; } = string.Empty;
}

public class SpotifyService
{
    private readonly string? _clientId;
    private readonly string? _clientSecret;
    private readonly object _tokenLock = new();
    
    private string? _accessToken;
    private string? _refreshToken;
    private DateTime _tokenExpiry = DateTime.MinValue;

    public SpotifyService(string clientId, string clientSecret, string? refreshToken = null, DateTime? expiry = null)
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
            var config = SpotifyClientConfig.CreateDefault();

            // Use AuthorizationCodeRefreshRequest which accepts clientId and clientSecret but they can be empty
            // When clientSecret is null/empty, some Spotify apps (PKCE/public client) only require client_id.
            // If Spotify requires client authentication and none is provided, the request will fail and we surface the error.
            var refreshRequest = new AuthorizationCodeRefreshRequest(_clientId ?? string.Empty, _clientSecret ?? string.Empty, refreshToken);
            var response = await new OAuthClient(config).RequestToken(refreshRequest);

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

    public async Task<SpotifyTrackInfo?> GetCurrentlyPlayingAsync()
    {
        var client = await GetClientAsync();
        var currentlyPlaying = await client.Player.GetCurrentlyPlaying(new PlayerCurrentlyPlayingRequest());
        
        if (currentlyPlaying?.Item is FullTrack track)
        {
            return new SpotifyTrackInfo
            {
                Title = track.Name,
                Artist = string.Join(", ", track.Artists.Select(a => a.Name)),
                Album = track.Album.Name,
                AlbumCoverUrl = track.Album.Images.FirstOrDefault()?.Url ?? string.Empty,
                ProgressMs = currentlyPlaying.ProgressMs ?? 0,
                DurationMs = track.DurationMs,
                SpotifyUri = track.Uri,
                ScannableCodeUrl = $"https://scannables.scdn.co/uri/plain/jpeg/000000/white/640/spotify:track:{track.Id}"
            };
        }

        return null;
    }
}