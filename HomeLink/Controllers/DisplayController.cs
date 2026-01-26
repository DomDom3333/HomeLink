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
    /// Generates the complete display bitmap for the Lilygo T5 e-ink display.
    /// Combines location and Spotify data into a formatted grayscale image,
    /// applies Floyd-Steinberg dithering, and converts to 1-bit packed format.
    /// Uses cached location from OwnTracks updates.
    /// </summary>
    /// <returns>Binary bitmap data ready to send to e-ink display</returns>
    [HttpGet("render")]
    public async Task<ActionResult<dynamic>> RenderDisplay()
    {
        if (!_spotifyService.IsAuthorized)
        {
            return Unauthorized(new { error = "Spotify is not authorized. Please visit /api/spotify/authorize first." });
        }

        try
        {
            // Get Spotify data
            var spotifyData = await _spotifyService.GetCurrentlyPlayingAsync();

            // Get cached location data from OwnTracks
            var locationData = _locationService.GetCachedLocation();

            // Draw the display (async to support album art download)
            var bitmap = await _drawingService.DrawDisplayDataAsync(spotifyData, locationData);

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
