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

    public DisplayController(DrawingService drawingService, SpotifyService spotifyService, LocationService locationService)
    {
        _drawingService = drawingService;
        _spotifyService = spotifyService;
        _locationService = locationService;
    }

    /// <summary>
    /// Binary endpoint for the ESP32: returns packed 1bpp bitmap bytes (no JSON, no base64).
    /// </summary>
    /// <param name="dither">Whether to apply dithering (default true).</param>
    /// <returns>application/octet-stream body = bitmap.PackedData</returns>
    [HttpGet("render")]
    [Produces("application/octet-stream")]
    public async Task<IActionResult> RenderDisplay([FromQuery] bool dither = true)
    {
        if (!_spotifyService.IsAuthorized)
        {
            return Unauthorized(new { error = "Spotify is not authorized. Please visit /api/spotify/authorize first." });
        }

        try
        {
            SpotifyTrackInfo? spotifyData = await _spotifyService.GetCurrentlyPlayingAsync();
            LocationInfo? locationData = _locationService.GetCachedLocation();

            EInkBitmap bitmap = await _drawingService.DrawDisplayDataAsync(spotifyData, locationData, dither);

            // Metadata in headers (ESP32 can read these if desired)
            Response.Headers["X-Width"] = bitmap.Width.ToString();
            Response.Headers["X-Height"] = bitmap.Height.ToString();
            Response.Headers["X-BytesPerLine"] = bitmap.BytesPerLine.ToString();
            Response.Headers["X-Dithered"] = dither ? "true" : "false";
            Response.Headers["Cache-Control"] = "no-store, no-transform";

            // Body is raw packed bytes (e.g. 64800 bytes for 960x540 @ 1bpp)
            return File(bitmap.PackedData, "application/octet-stream");
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Optional: keep a JSON version for debugging in browser/Postman.
    /// </summary>
    [HttpGet("render-json")]
    public async Task<ActionResult<dynamic>> RenderDisplayJson([FromQuery] bool dither = true)
    {
        if (!_spotifyService.IsAuthorized)
        {
            return Unauthorized(new { error = "Spotify is not authorized. Please visit /api/spotify/authorize first." });
        }

        try
        {
            SpotifyTrackInfo? spotifyData = await _spotifyService.GetCurrentlyPlayingAsync();
            LocationInfo? locationData = _locationService.GetCachedLocation();
            EInkBitmap bitmap = await _drawingService.DrawDisplayDataAsync(spotifyData, locationData, dither);

            return Ok(new
            {
                success = true,
                dithered = dither,
                bitmap = new
                {
                    width = bitmap.Width,
                    height = bitmap.Height,
                    bytesPerLine = bitmap.BytesPerLine,
                    data = Convert.ToBase64String(bitmap.PackedData)
                }
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
    /// <summary>
    /// Renders the display image as a PNG file.
    /// </summary>
    /// <param name="dither">Whether to apply dithering (default true). Set to false for grayscale output.</param>
    /// <returns>PNG image file of the current display</returns>
    [HttpGet("image")]
    public async Task<IActionResult> RenderDisplayImage([FromQuery] bool dither = true)
    {
        if (!_spotifyService.IsAuthorized)
        {
            return Unauthorized(new { error = "Spotify is not authorized. Please visit /api/spotify/authorize first." });
        }

        try
        {
            SpotifyTrackInfo? spotifyData = await _spotifyService.GetCurrentlyPlayingAsync();
            LocationInfo? locationData = _locationService.GetCachedLocation();
            byte[] pngBytes = await _drawingService.RenderDisplayPngAsync(spotifyData, locationData, dither);
            return File(pngBytes, "image/png");
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
    
    /// <summary>
    /// Renders display with only Spotify data (for testing/fallback).
    /// </summary>
    [HttpGet("render-spotify-only")]
    public async Task<ActionResult<dynamic>> RenderSpotifyOnly()
    {
        if (!_spotifyService.IsAuthorized)
        {
            return Unauthorized(new { error = "Spotify is not authorized. Please visit /api/spotify/authorize first." });
        }

        try
        {
            var spotifyData = await _spotifyService.GetCurrentlyPlayingAsync();
            var bitmap = await _drawingService.DrawDisplayDataAsync(spotifyData, null);

            return Ok(new
            {
                success = true,
                bitmap = new
                {
                    width = bitmap.Width,
                    height = bitmap.Height,
                    bytesPerLine = bitmap.BytesPerLine,
                    data = Convert.ToBase64String(bitmap.PackedData)
                }
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Renders display with only location data (for testing/fallback).
    /// Uses cached location from OwnTracks updates.
    /// </summary>
    [HttpGet("render-location-only")]
    public async Task<ActionResult<dynamic>> RenderLocationOnly()
    {
        try
        {
            var locationData = _locationService.GetCachedLocation();
            if (locationData == null)
            {
                return NotFound(new { error = "No location cached. Send an OwnTracks location update first via POST /api/location/owntracks" });
            }
            
            var bitmap = await _drawingService.DrawDisplayDataAsync(null, locationData);

            return Ok(new
            {
                success = true,
                bitmap = new
                {
                    width = bitmap.Width,
                    height = bitmap.Height,
                    bytesPerLine = bitmap.BytesPerLine,
                    data = Convert.ToBase64String(bitmap.PackedData)
                }
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
