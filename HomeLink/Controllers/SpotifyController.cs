using System.Net;
using HomeLink.Models;
using HomeLink.Services;
using Microsoft.AspNetCore.Mvc;

namespace HomeLink.Controllers;

/// <summary>
/// Browser-facing Spotify OAuth flow: start it at <c>/api/spotify/authorize</c> and Spotify
/// returns to <c>/api/spotify/callback</c>, where the refresh token is stored in the state database.
/// </summary>
[ApiController]
[Route("api/spotify")]
public class SpotifyController : ControllerBase
{
    private readonly SpotifyService _spotifyService;
    private readonly SpotifyOAuthService _oauthService;
    private readonly ILogger<SpotifyController> _logger;

    public SpotifyController(SpotifyService spotifyService, SpotifyOAuthService oauthService, ILogger<SpotifyController> logger)
    {
        _spotifyService = spotifyService;
        _oauthService = oauthService;
        _logger = logger;
    }

    /// <summary>
    /// Current authorization state, including the redirect URI to register with the Spotify app.
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(SpotifyAuthStatus), StatusCodes.Status200OK)]
    public ActionResult<SpotifyAuthStatus> GetStatus()
    {
        SpotifyAuthStatus status = _spotifyService.GetAuthStatus();
        status.RedirectUri = _oauthService.ResolveRedirectUri(Request);
        status.SetupKeyRequired = _oauthService.SetupKeyRequired;
        return Ok(status);
    }

    /// <summary>
    /// Redirects to Spotify's consent screen. Requires <c>?key=</c> when SPOTIFY_SETUP_KEY is configured.
    /// </summary>
    [HttpGet("authorize")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public IActionResult Authorize([FromQuery] string? key = null)
    {
        if (!_oauthService.IsSetupKeyValid(key))
        {
            _logger.LogWarning("Spotify authorize denied: missing or invalid setup key.");
            return Unauthorized(new ErrorResponse { Error = "A valid setup key is required. Append ?key=<SPOTIFY_SETUP_KEY> to this URL." });
        }

        if (!_spotifyService.IsConfigured)
        {
            _logger.LogWarning("Spotify authorize denied: client id/secret are not configured.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ErrorResponse
            {
                Error = "Spotify client credentials are missing. Set SPOTIFY_ID and SPOTIFY_SECRET and restart the service."
            });
        }

        string redirectUri = _oauthService.ResolveRedirectUri(Request);
        string state = _oauthService.CreateState();
        string authorizeUrl = _oauthService.BuildAuthorizeUrl(_spotifyService.ClientId!, redirectUri, state);

        _logger.LogInformation("Starting Spotify OAuth flow with redirect URI {RedirectUri}.", redirectUri);
        return Redirect(authorizeUrl);
    }

    /// <summary>
    /// OAuth callback. Exchanges the authorization code for a refresh token and persists it.
    /// </summary>
    [HttpGet("callback")]
    [Produces("text/html")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Callback([FromQuery] string? code = null, [FromQuery] string? state = null, [FromQuery] string? error = null)
    {
        if (!string.IsNullOrEmpty(error))
        {
            _logger.LogWarning("Spotify OAuth callback returned an error: {Error}.", error);
            return HtmlResult(StatusCodes.Status400BadRequest, "Authorization failed", $"Spotify reported: {error}");
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            return HtmlResult(StatusCodes.Status400BadRequest, "Authorization failed", "No authorization code was returned by Spotify.");
        }

        if (!_oauthService.TryConsumeState(state))
        {
            return HtmlResult(StatusCodes.Status400BadRequest, "Authorization failed",
                "The state value was missing, expired or already used. Start again from /api/spotify/authorize.");
        }

        string redirectUri = _oauthService.ResolveRedirectUri(Request);

        try
        {
            await _spotifyService.ExchangeAuthorizationCodeAsync(code, redirectUri);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Spotify authorization code exchange failed.");
            return HtmlResult(StatusCodes.Status400BadRequest, "Authorization failed", ex.Message);
        }

        _logger.LogInformation("Spotify authorization completed successfully.");
        return HtmlResult(StatusCodes.Status200OK, "Spotify connected",
            "HomeLink now holds a fresh refresh token. It is stored in the state database and survives restarts.");
    }

    private ContentResult HtmlResult(int statusCode, string heading, string message)
    {
        string html = $$"""
                       <!DOCTYPE html>
                       <html lang="en">
                       <head>
                         <meta charset="utf-8" />
                         <title>HomeLink — {{WebUtility.HtmlEncode(heading)}}</title>
                         <style>
                           body { background:#0b1120; color:#e2e8f0; font-family:system-ui,-apple-system,Segoe UI,sans-serif; display:flex; min-height:100vh; align-items:center; justify-content:center; margin:0; }
                           .card { background:#111c33; border:1px solid #1e2c4a; border-radius:12px; padding:32px 40px; max-width:560px; }
                           h1 { font-size:20px; margin:0 0 12px; }
                           p { color:#94a3b8; line-height:1.5; margin:0 0 16px; }
                           a { color:#34d399; }
                         </style>
                       </head>
                       <body>
                         <div class="card">
                           <h1>{{WebUtility.HtmlEncode(heading)}}</h1>
                           <p>{{WebUtility.HtmlEncode(message)}}</p>
                           <p><a href="/telemetry/dashboard">Back to the dashboard</a></p>
                         </div>
                       </body>
                       </html>
                       """;

        return new ContentResult
        {
            StatusCode = statusCode,
            ContentType = "text/html; charset=utf-8",
            Content = html
        };
    }
}
