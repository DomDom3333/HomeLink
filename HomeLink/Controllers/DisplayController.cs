using System.Diagnostics;
using HomeLink.Models;
using Microsoft.AspNetCore.Mvc;
using HomeLink.Services;
using HomeLink.Telemetry;

namespace HomeLink.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DisplayController : ControllerBase
{
    private readonly DrawingService _drawingService;
    private readonly SpotifyService _spotifyService;
    private readonly LocationService _locationService;
    private readonly ILogger<DisplayController> _logger;
    private readonly TelemetryDashboardState _dashboardState;
    private readonly DisplayFrameCacheService _displayFrameCache;

    public DisplayController(DrawingService drawingService, SpotifyService spotifyService, LocationService locationService, ILogger<DisplayController> logger, TelemetryDashboardState dashboardState, DisplayFrameCacheService displayFrameCache)
    {
        _drawingService = drawingService;
        _spotifyService = spotifyService;
        _locationService = locationService;
        _logger = logger;
        _dashboardState = dashboardState;
        _displayFrameCache = displayFrameCache;
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
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public IActionResult RenderDisplay([FromQuery] bool dither = true, [FromQuery] int? deviceBattery = null)
    {
        long startTimestamp = Stopwatch.GetTimestamp();
        bool isError = false;
        HomeLinkTelemetry.DisplayRenderRequests.Add(1);

        using Activity? activity = HomeLinkTelemetry.ActivitySource.StartActivity("DisplayController.RenderDisplay", ActivityKind.Server);
        activity?.SetTag("display.dither", dither);
        if (deviceBattery.HasValue)
        {
            activity?.SetTag("display.device_battery", deviceBattery.Value);
        }

        _logger.LogInformation("RenderDisplay request received. Dither: {Dither}, DeviceBattery: {DeviceBattery}", dither, deviceBattery);

        _displayFrameCache.UpdateRequestedRenderOptions(dither, deviceBattery);

        if (!_spotifyService.IsAuthorized)
        {
            _logger.LogWarning("RenderDisplay denied: Spotify is not authorized.");
            isError = true;
            activity?.SetTag("error", true);
            activity?.SetTag("http.response.status_code", 401);
            return Unauthorized(new ErrorResponse { Error = "Spotify is not authorized. Please visit /api/spotify/authorize first." });
        }

        try
        {
            DisplayFrameSnapshot? snapshot = _displayFrameCache.GetLatestFrame();
            if (snapshot == null)
            {
                Response.Headers["Cache-Control"] = "no-store, no-transform, private";
                Response.Headers["Pragma"] = "no-cache";
                Response.Headers["X-Frame-Age-Ms"] = "-1";
                activity?.SetTag("http.response.status_code", 503);
                _logger.LogInformation("RenderDisplay no cached frame available yet.");
                return StatusCode(StatusCodes.Status503ServiceUnavailable,
                    new ErrorResponse { Error = "Display frame not ready yet. Retry shortly." });
            }

            // Ensure cache/etag headers are included even on 304 responses
            Response.Headers.ETag = snapshot.Etag;
            Response.Headers["Cache-Control"] = "no-store, no-transform, private";
            Response.Headers["Pragma"] = "no-cache";
            long ageMs = Math.Max(0, (long)(DateTimeOffset.UtcNow - snapshot.GeneratedAtUtc).TotalMilliseconds);
            Response.Headers["X-Frame-Age-Ms"] = ageMs.ToString();

            // Check If-None-Match header before rendering
            if (Request.Headers.IfNoneMatch.Count > 0 && Request.Headers.IfNoneMatch.Contains(snapshot.Etag))
            {
                _logger.LogInformation("RenderDisplay returning 304 Not Modified. ETag: {Etag}", snapshot.Etag);
                activity?.SetTag("http.response.status_code", 304);
                return StatusCode(StatusCodes.Status304NotModified);
            }

            // Metadata in headers (ESP32 can read these if desired)
            Response.Headers["X-Width"] = snapshot.Width.ToString();
            Response.Headers["X-Height"] = snapshot.Height.ToString();
            Response.Headers["X-BytesPerLine"] = snapshot.BytesPerLine.ToString();
            Response.Headers["X-Dithered"] = snapshot.Dithered ? "true" : "false";
            Response.ContentLength = snapshot.FrameBytes.Length;
            // Diagnostic header - echo device battery when provided (helps confirm public URL received the query)
            if (deviceBattery.HasValue)
                Response.Headers["X-Device-Battery"] = deviceBattery.Value.ToString();
            // Force download disposition so intermediaries treat this as opaque binary
            Response.Headers["Content-Disposition"] = "attachment; filename=display.bin";

            // Body is raw packed bytes (e.g. 64800 bytes for 960x540 @ 1bpp)
            _logger.LogInformation("RenderDisplay returning cached bitmap bytes. Width: {Width}, Height: {Height}, Bytes: {ByteCount}, FrameAgeMs: {FrameAgeMs}", snapshot.Width, snapshot.Height, snapshot.FrameBytes.Length, ageMs);
            activity?.SetTag("http.response.status_code", 200);
            activity?.SetTag("display.bitmap.width", snapshot.Width);
            activity?.SetTag("display.bitmap.height", snapshot.Height);
            return File(snapshot.FrameBytes, "application/octet-stream");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RenderDisplay failed.");
            isError = true;
            activity?.SetTag("error", true);
            activity?.SetTag("http.response.status_code", 400);
            activity?.AddException(ex);
            return BadRequest(new ErrorResponse { Error = ex.Message });
        }
        finally
        {
            double elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            HomeLinkTelemetry.DisplayRenderDurationMs.Record(elapsedMs);
            _dashboardState.RecordDisplay(elapsedMs, isError, deviceBattery);
            activity?.SetTag("display.duration_ms", elapsedMs);
        }
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
