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
    /// Generates the complete display bitmap for the Lilygo T5 e-ink display.
    /// Combines location and Spotify data into a formatted grayscale image,
    /// applies Floyd-Steinberg dithering, and converts to 1-bit packed format.
    /// Uses cached location from OwnTracks updates.
    /// </summary>
    /// <param name="dither">Whether to apply dithering (default true). Set to false for grayscale output.</param>
    /// <returns>Binary bitmap data ready to send to e-ink display</returns>
    [HttpGet("render")]
    public async Task<ActionResult<dynamic>> RenderDisplay([FromQuery] bool dither = true)
    {
        if (!_spotifyService.IsAuthorized)
        {
            return Unauthorized(new { error = "Spotify is not authorized. Please visit /api/spotify/authorize first." });
        }

        try
        {
            // Get Spotify data
            SpotifyTrackInfo? spotifyData = await _spotifyService.GetCurrentlyPlayingAsync();

            // Get cached location data from OwnTracks
            LocationInfo? locationData = _locationService.GetCachedLocation();

            // Draw the display (async to support album art download)
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
}
