using System.Security.Cryptography;
using System.Text;
using HomeLink.Models;
using Microsoft.AspNetCore.Mvc;
using HomeLink.Services;

namespace HomeLink.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DisplayController : ControllerBase
{
    private readonly DrawingService _drawingService;
    private readonly SpotifyService _spotifyService;
    private readonly LocationService _locationService;
    private readonly ILogger<DisplayController> _logger;

    public DisplayController(DrawingService drawingService, SpotifyService spotifyService, LocationService locationService, ILogger<DisplayController> logger)
    {
        _drawingService = drawingService;
        _spotifyService = spotifyService;
        _locationService = locationService;
        _logger = logger;
    }

    /// <summary>
    /// Binary endpoint for the ESP32: returns packed 1bpp bitmap bytes (no JSON, no base64).
    /// </summary>
    /// <param name="dither">Whether to apply dithering (default true).</param>
    /// <param name="deviceBattery">Optional device battery percentage (0-100). Shows warning if below 10%.</param>
    /// <returns>application/octet-stream body = bitmap.PackedData</returns>
    [HttpGet("render")]
    [Produces("application/octet-stream")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RenderDisplay([FromQuery] bool dither = true, [FromQuery] int? deviceBattery = null)
    {
        _logger.LogInformation("RenderDisplay request received. Dither: {Dither}, DeviceBattery: {DeviceBattery}", dither, deviceBattery);

        if (!_spotifyService.IsAuthorized)
        {
            _logger.LogWarning("RenderDisplay denied: Spotify is not authorized.");
            return Unauthorized(new ErrorResponse { Error = "Spotify is not authorized. Please visit /api/spotify/authorize first." });
        }

        try
        {
            SpotifyTrackInfo? spotifyData = await _spotifyService.GetCurrentlyPlayingAsync();
            LocationInfo? locationData = _locationService.GetCachedLocation();

            // Compute ETag from stable source data (excluding volatile timestamps)
            string etag = ComputeSourceDataEtag(spotifyData, locationData, dither, deviceBattery);

            // Check If-None-Match header before rendering
            if (Request.Headers.IfNoneMatch.Count > 0 && Request.Headers.IfNoneMatch.Contains(etag))
            {
                _logger.LogInformation("RenderDisplay returning 304 Not Modified. ETag: {Etag}", etag);
                return StatusCode(StatusCodes.Status304NotModified);
            }

            // Render bitmap only if needed
            EInkBitmap bitmap = await _drawingService.DrawDisplayDataAsync(spotifyData, locationData, dither, deviceBattery);

            // Metadata in headers (ESP32 can read these if desired)
            Response.Headers["X-Width"] = bitmap.Width.ToString();
            Response.Headers["X-Height"] = bitmap.Height.ToString();
            Response.Headers["X-BytesPerLine"] = bitmap.BytesPerLine.ToString();
            Response.Headers["X-Dithered"] = dither ? "true" : "false";
            Response.Headers.ETag = etag;
            // Prevent intermediaries (CDNs/proxies) from caching or altering the binary response
            Response.Headers["Cache-Control"] = "no-store, no-transform, private";
            Response.Headers["Pragma"] = "no-cache";
            // Diagnostic header - echo device battery when provided (helps confirm public URL received the query)
            if (deviceBattery.HasValue)
                Response.Headers["X-Device-Battery"] = deviceBattery.Value.ToString();
            // Force download disposition so intermediaries treat this as opaque binary
            Response.Headers["Content-Disposition"] = "attachment; filename=display.bin";

            // Body is raw packed bytes (e.g. 64800 bytes for 960x540 @ 1bpp)
            _logger.LogInformation("RenderDisplay returning bitmap bytes. Width: {Width}, Height: {Height}, Bytes: {ByteCount}", bitmap.Width, bitmap.Height, bitmap.PackedData.Length);
            return File(bitmap.PackedData, "application/octet-stream");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RenderDisplay failed.");
            return BadRequest(new ErrorResponse { Error = ex.Message });
        }
    }

    private static string ComputeSourceDataEtag(SpotifyTrackInfo? spotify, LocationInfo? location, bool dither, int? deviceBattery = null)
    {
        var sb = new StringBuilder();
        
        // Include dither setting
        sb.Append($"dither:{dither}|");
        
        // Include device battery (for low battery warning state changes)
        if (deviceBattery.HasValue)
        {
            // Only include threshold-relevant state (above or below 10%)
            sb.Append($"deviceBatteryLow:{(deviceBattery.Value < 10)}|");
            sb.Append($"deviceBattery:{deviceBattery.Value % 10 == 0}|");
        }
        
        // Include stable Spotify fields (exclude ProgressMs as it changes constantly)
        if (spotify != null)
        {
            sb.Append($"spotify:");
            sb.Append($"title:{spotify.Title}|");
            sb.Append($"artist:{spotify.Artist}|");
            sb.Append($"album:{spotify.Album}|");
            sb.Append($"coverUrl:{spotify.AlbumCoverUrl}|");
            sb.Append($"duration:{spotify.DurationMs}|");
            sb.Append($"uri:{spotify.SpotifyUri}|");
            sb.Append($"playing:{spotify.IsPlaying}|");
            sb.Append($"progressMs:{spotify.ProgressMs}|");
        }
        
        // Include stable Location fields (exclude timestamps, battery, velocity, etc.)
        if (location != null)
        {
            sb.Append($"location:");
            // Round coordinates to ~11m precision (5 decimal places) to avoid minor GPS drift
            sb.Append($"lat:{Math.Round(location.Latitude, 5)}|");
            sb.Append($"lon:{Math.Round(location.Longitude, 5)}|");
            sb.Append($"name:{location.DisplayName}|");
            sb.Append($"hr:{location.HumanReadable}|");
            sb.Append($"district:{location.District}|");
            sb.Append($"city:{location.City}|");
            sb.Append($"town:{location.Town}|");
            sb.Append($"village:{location.Village}|");
            sb.Append($"country:{location.Country}|");
            sb.Append($"knownLoc:{location.MatchedKnownLocation?.Name}|");
            // Exclude: BatteryLevel, BatteryStatus, Accuracy, Altitude, Velocity, Timestamp
            // (these change frequently but don't affect the display meaningfully)
        }
        
        string content = sb.ToString();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        string tag = Convert.ToHexString(hash);
        return $"\"{tag}\"";
    }
    /// <summary>
    /// Renders the display image as a PNG file.
    /// </summary>
    /// <param name="dither">Whether to apply dithering (default true). Set to false for grayscale output.</param>
    /// <param name="deviceBattery">Optional device battery percentage (0-100) to show the display battery indicator.</param>
    /// <returns>PNG image file of the current display</returns>
    [HttpGet("image")]
    [Produces("image/png")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RenderDisplayImage([FromQuery] bool dither = true, [FromQuery] int? deviceBattery = null)
    {
        _logger.LogInformation("RenderDisplayImage request received. Dither: {Dither}, DeviceBattery: {DeviceBattery}", dither, deviceBattery);

        if (!_spotifyService.IsAuthorized)
        {
            _logger.LogWarning("RenderDisplayImage denied: Spotify is not authorized.");
            return Unauthorized(new ErrorResponse { Error = "Spotify is not authorized. Please visit /api/spotify/authorize first." });
        }

        try
        {
            SpotifyTrackInfo? spotifyData = await _spotifyService.GetCurrentlyPlayingAsync();
            LocationInfo? locationData = _locationService.GetCachedLocation();
            byte[] pngBytes = await _drawingService.RenderDisplayPngAsync(spotifyData, locationData, dither, deviceBattery);
            // Prevent intermediaries from caching or transforming the PNG (important for exact pixel output)
            Response.Headers["Cache-Control"] = "no-store, no-transform, private";
            Response.Headers["Pragma"] = "no-cache";
            // Diagnostic header - echo device battery when provided (helps confirm public URL received the query)
            if (deviceBattery.HasValue)
                Response.Headers["X-Device-Battery"] = deviceBattery.Value.ToString();
            // Use attachment disposition to avoid image optimization by proxies/CDNs
            Response.Headers["Content-Disposition"] = "attachment; filename=display.png";
            _logger.LogInformation("RenderDisplayImage returning PNG. Bytes: {ByteCount}", pngBytes.Length);
             return File(pngBytes, "image/png");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RenderDisplayImage failed.");
            return BadRequest(new ErrorResponse { Error = ex.Message });
        }
    }
    
    /// <summary>
    /// Renders display with only Spotify data (for testing/fallback).
    /// </summary>
    [HttpGet("render-spotify-only")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(RenderBitmapResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<RenderBitmapResponse>> RenderSpotifyOnly()
    {
        _logger.LogInformation("RenderSpotifyOnly request received.");

        if (!_spotifyService.IsAuthorized)
        {
            _logger.LogWarning("RenderSpotifyOnly denied: Spotify is not authorized.");
            return Unauthorized(new ErrorResponse { Error = "Spotify is not authorized. Please visit /api/spotify/authorize first." });
        }

        try
        {
            var spotifyData = await _spotifyService.GetCurrentlyPlayingAsync();
            var bitmap = await _drawingService.DrawDisplayDataAsync(spotifyData, null);

            RenderBitmapResponse response = new()
            {
                Success = true,
                Bitmap = new BitmapResponse
                {
                    Width = bitmap.Width,
                    Height = bitmap.Height,
                    BytesPerLine = bitmap.BytesPerLine,
                    Data = Convert.ToBase64String(bitmap.PackedData)
                }
            };

            _logger.LogInformation("RenderSpotifyOnly returning bitmap payload.");
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RenderSpotifyOnly failed.");
            return BadRequest(new ErrorResponse { Error = ex.Message });
        }
    }

    /// <summary>
    /// Renders display with only location data (for testing/fallback).
    /// Uses cached location from OwnTracks updates.
    /// </summary>
    [HttpGet("render-location-only")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(RenderBitmapResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RenderBitmapResponse>> RenderLocationOnly()
    {
        _logger.LogInformation("RenderLocationOnly request received.");

        try
        {
            var locationData = _locationService.GetCachedLocation();
            if (locationData == null)
            {
                _logger.LogWarning("RenderLocationOnly requested but no cached location is available.");
                return NotFound(new ErrorResponse { Error = "No location cached. Send an OwnTracks location update first via POST /api/location/owntracks" });
            }
            
            var bitmap = await _drawingService.DrawDisplayDataAsync(null, locationData);

            RenderBitmapResponse response = new()
            {
                Success = true,
                Bitmap = new BitmapResponse
                {
                    Width = bitmap.Width,
                    Height = bitmap.Height,
                    BytesPerLine = bitmap.BytesPerLine,
                    Data = Convert.ToBase64String(bitmap.PackedData)
                }
            };

            _logger.LogInformation("RenderLocationOnly returning bitmap payload.");
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RenderLocationOnly failed.");
            return BadRequest(new ErrorResponse { Error = ex.Message });
        }
    }
}
