using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HomeLink.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using HomeLink.Services;

namespace HomeLink.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DisplayController : ControllerBase
{
    private readonly DrawingService _drawingService;
    private readonly DisplayDataService _displayDataService;
    private readonly SpotifyService _spotifyService;
    private readonly LocationService _locationService;

    public DisplayController(DrawingService drawingService, DisplayDataService displayDataService, SpotifyService spotifyService, LocationService locationService)
    {
        _drawingService = drawingService;
        _displayDataService = displayDataService;
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
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RenderDisplay([FromQuery] bool dither = true)
    {
        if (!_spotifyService.IsAuthorized)
        {
            return Unauthorized(new ErrorResponse { Error = "Spotify is not authorized. Please visit /api/spotify/authorize first." });
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
            return BadRequest(new ErrorResponse { Error = ex.Message });
        }
    }

    /// <summary>
    /// Optional: keep a JSON version for debugging in browser/Postman.
    /// </summary>
    [HttpGet("render-json")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(RenderBitmapWithDitherResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<RenderBitmapWithDitherResponse>> RenderDisplayJson([FromQuery] bool dither = true)
    {
        if (!_spotifyService.IsAuthorized)
        {
            return Unauthorized(new ErrorResponse { Error = "Spotify is not authorized. Please visit /api/spotify/authorize first." });
        }

        try
        {
            SpotifyTrackInfo? spotifyData = await _spotifyService.GetCurrentlyPlayingAsync();
            LocationInfo? locationData = _locationService.GetCachedLocation();
            EInkBitmap bitmap = await _drawingService.DrawDisplayDataAsync(spotifyData, locationData, dither);

            RenderBitmapWithDitherResponse response = new()
            {
                Success = true,
                Dithered = dither,
                Bitmap = new BitmapResponse
                {
                    Width = bitmap.Width,
                    Height = bitmap.Height,
                    BytesPerLine = bitmap.BytesPerLine,
                    Data = Convert.ToBase64String(bitmap.PackedData)
                }
            };

            return Ok(response);
        }
        catch (Exception ex)
        {
            return BadRequest(new ErrorResponse { Error = ex.Message });
        }
    }

    /// <summary>
    /// Returns the display data as structured JSON for client-side rendering.
    /// </summary>
    [HttpGet("data")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(DisplayDataResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<DisplayDataResponse>> GetDisplayData()
    {
        if (!_spotifyService.IsAuthorized)
        {
            return Unauthorized(new ErrorResponse { Error = "Spotify is not authorized. Please visit /api/spotify/authorize first." });
        }

        try
        {
            SpotifyTrackInfo? spotifyData = await _spotifyService.GetCurrentlyPlayingAsync();
            LocationInfo? locationData = _locationService.GetCachedLocation();
            DisplayMetadata display = _drawingService.GetDisplayMetadata();

            DisplayDataResponse response = _displayDataService.BuildDisplayData(spotifyData, locationData, display);

            return WithEtag(response);
        }
        catch (Exception ex)
        {
            return BadRequest(new ErrorResponse { Error = ex.Message });
        }
    }

    /// <summary>
    /// Returns the Spotify portion of the display data as JSON.
    /// </summary>
    [HttpGet("data/spotify")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(SpotifyDisplayData), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<SpotifyDisplayData>> GetSpotifyData()
    {
        if (!_spotifyService.IsAuthorized)
        {
            return Unauthorized(new ErrorResponse { Error = "Spotify is not authorized. Please visit /api/spotify/authorize first." });
        }

        try
        {
            SpotifyTrackInfo? spotifyData = await _spotifyService.GetCurrentlyPlayingAsync();
            SpotifyDisplayData? response = _displayDataService.BuildSpotifyData(spotifyData);

            return WithEtag(response);
        }
        catch (Exception ex)
        {
            return BadRequest(new ErrorResponse { Error = ex.Message });
        }
    }

    /// <summary>
    /// Returns the location portion of the display data as JSON.
    /// </summary>
    [HttpGet("data/location")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(LocationDisplayData), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public ActionResult<LocationDisplayData> GetLocationData()
    {
        try
        {
            LocationInfo? locationData = _locationService.GetCachedLocation();
            LocationDisplayData? response = _displayDataService.BuildLocationData(locationData);

            return WithEtag(response);
        }
        catch (Exception ex)
        {
            return BadRequest(new ErrorResponse { Error = ex.Message });
        }
    }

    private ActionResult<T> WithEtag<T>(T payload)
    {
        string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        string etag = ComputeEtag(json);

        Response.Headers.ETag = etag;
        Response.Headers.CacheControl = "no-cache";

        if (Request.Headers.IfNoneMatch.Count > 0 && Request.Headers.IfNoneMatch.Contains(etag))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return Ok(payload);
    }

    private static string ComputeEtag(string content)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        string tag = Convert.ToHexString(hash);
        return $"\"{tag}\"";
    }
    /// <summary>
    /// Renders the display image as a PNG file.
    /// </summary>
    /// <param name="dither">Whether to apply dithering (default true). Set to false for grayscale output.</param>
    /// <returns>PNG image file of the current display</returns>
    [HttpGet("image")]
    [Produces("image/png")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RenderDisplayImage([FromQuery] bool dither = true)
    {
        if (!_spotifyService.IsAuthorized)
        {
            return Unauthorized(new ErrorResponse { Error = "Spotify is not authorized. Please visit /api/spotify/authorize first." });
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
        if (!_spotifyService.IsAuthorized)
        {
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

            return Ok(response);
        }
        catch (Exception ex)
        {
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
        try
        {
            var locationData = _locationService.GetCachedLocation();
            if (locationData == null)
            {
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

            return Ok(response);
        }
        catch (Exception ex)
        {
            return BadRequest(new ErrorResponse { Error = ex.Message });
        }
    }
}
