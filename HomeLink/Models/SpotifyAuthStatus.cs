namespace HomeLink.Models;

/// <summary>
/// Current Spotify authorization state, as reported by <c>GET /api/spotify/status</c> and the dashboard.
/// </summary>
public class SpotifyAuthStatus
{
    /// <summary>True when SPOTIFY_ID and SPOTIFY_SECRET are set, so the OAuth flow can run.</summary>
    public bool Configured { get; set; }

    /// <summary>True when a refresh token is present (from the OAuth flow, persistence, or the environment).</summary>
    public bool Authorized { get; set; }

    /// <summary>When the current refresh token was obtained, if known.</summary>
    public DateTime? RefreshTokenObtainedUtc { get; set; }

    /// <summary>Expiry of the cached access token, if one is held.</summary>
    public DateTime? AccessTokenExpiresUtc { get; set; }

    /// <summary>Message of the last failed token refresh, if any. Cleared on the next successful refresh.</summary>
    public string? LastRefreshError { get; set; }

    public DateTime? LastRefreshErrorUtc { get; set; }

    /// <summary>The redirect URI this instance uses — add it verbatim to the Spotify app settings.</summary>
    public string? RedirectUri { get; set; }

    /// <summary>True when a setup key is required to start the OAuth flow.</summary>
    public bool SetupKeyRequired { get; set; }
}
