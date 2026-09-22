using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace HomeLink.Services;

/// <summary>
/// Builds the Spotify authorization URL, resolves the redirect URI for this deployment and
/// tracks the short-lived <c>state</c> values used to protect the callback against CSRF.
/// </summary>
public class SpotifyOAuthService
{
    public const string Scopes = "user-read-currently-playing user-read-playback-state";
    public const string CallbackPath = "/api/spotify/callback";

    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    private readonly ILogger<SpotifyOAuthService> _logger;
    private readonly string? _configuredRedirectUri;
    private readonly string? _setupKey;
    private readonly ConcurrentDictionary<string, DateTime> _pendingStates = new();

    public SpotifyOAuthService(ILogger<SpotifyOAuthService> logger, IConfiguration configuration)
    {
        _logger = logger;

        string? redirectUri = Environment.GetEnvironmentVariable("SPOTIFY_REDIRECT_URI");
        if (string.IsNullOrWhiteSpace(redirectUri))
            redirectUri = configuration["Spotify:RedirectUri"];

        _configuredRedirectUri = string.IsNullOrWhiteSpace(redirectUri) ? null : redirectUri.Trim();

        string? setupKey = Environment.GetEnvironmentVariable("SPOTIFY_SETUP_KEY");
        if (string.IsNullOrWhiteSpace(setupKey))
            setupKey = configuration["Spotify:SetupKey"];

        _setupKey = string.IsNullOrWhiteSpace(setupKey) ? null : setupKey.Trim();
    }

    /// <summary>
    /// True when a setup key is configured and therefore required to start the OAuth flow.
    /// </summary>
    public bool SetupKeyRequired => _setupKey != null;

    public bool IsSetupKeyValid(string? providedKey)
    {
        if (_setupKey == null)
            return true;

        if (string.IsNullOrEmpty(providedKey))
            return false;

        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(providedKey),
            System.Text.Encoding.UTF8.GetBytes(_setupKey));
    }

    /// <summary>
    /// The redirect URI registered with Spotify. Uses the configured value when present, otherwise
    /// derives it from the incoming request (so a reverse-proxied host works without extra config).
    /// </summary>
    public string ResolveRedirectUri(HttpRequest request)
    {
        if (_configuredRedirectUri != null)
            return _configuredRedirectUri;

        return $"{request.Scheme}://{request.Host}{CallbackPath}";
    }

    public string CreateState()
    {
        PruneExpiredStates();

        string state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _pendingStates[state] = DateTime.UtcNow.Add(StateLifetime);
        return state;
    }

    /// <summary>
    /// Consumes a state value. Returns false when it is unknown or expired.
    /// </summary>
    public bool TryConsumeState(string? state)
    {
        PruneExpiredStates();

        if (string.IsNullOrWhiteSpace(state))
            return false;

        if (!_pendingStates.TryRemove(state, out DateTime expiresUtc))
        {
            _logger.LogWarning("Spotify callback presented an unknown or already used state value.");
            return false;
        }

        if (expiresUtc < DateTime.UtcNow)
        {
            _logger.LogWarning("Spotify callback presented an expired state value.");
            return false;
        }

        return true;
    }

    public string BuildAuthorizeUrl(string clientId, string redirectUri, string state)
    {
        string query = string.Join('&',
            "response_type=code",
            $"client_id={Uri.EscapeDataString(clientId)}",
            $"scope={Uri.EscapeDataString(Scopes)}",
            $"redirect_uri={Uri.EscapeDataString(redirectUri)}",
            $"state={Uri.EscapeDataString(state)}",
            // Always show the consent screen so a stale grant can be replaced deliberately.
            "show_dialog=true");

        return $"https://accounts.spotify.com/authorize?{query}";
    }

    private void PruneExpiredStates()
    {
        DateTime now = DateTime.UtcNow;
        foreach (KeyValuePair<string, DateTime> entry in _pendingStates)
        {
            if (entry.Value < now)
                _pendingStates.TryRemove(entry.Key, out _);
        }
    }
}
