using Microsoft.AspNetCore.Mvc;
using HomeLink.Services;
using System.Text.Json.Serialization;

namespace HomeLink.Controllers;

/// <summary>
/// OwnTracks payload model.
/// See: https://owntracks.org/booklet/tech/json/
/// </summary>
public class OwnTracksPayload
{
    /// <summary>
    /// Type of message (_type). For location updates this is "location".
    /// </summary>
    [JsonPropertyName("_type")]
    public string? Type { get; set; }

    /// <summary>
    /// Latitude (lat).
    /// </summary>
    [JsonPropertyName("lat")]
    public double? Latitude { get; set; }

    /// <summary>
    /// Longitude (lon).
    /// </summary>
    [JsonPropertyName("lon")]
    public double? Longitude { get; set; }

    /// <summary>
    /// Accuracy of the location in meters (acc).
    /// </summary>
    [JsonPropertyName("acc")]
    public int? Accuracy { get; set; }

    /// <summary>
    /// Altitude above sea level in meters (alt).
    /// </summary>
    [JsonPropertyName("alt")]
    public int? Altitude { get; set; }

    /// <summary>
    /// Battery level percentage (batt).
    /// </summary>
    [JsonPropertyName("batt")]
    public int? Battery { get; set; }

    /// <summary>
    /// Battery status (bs): 0=unknown, 1=unplugged, 2=charging, 3=full.
    /// </summary>
    [JsonPropertyName("bs")]
    public int? BatteryStatus { get; set; }

    /// <summary>
    /// Course over ground in degrees (cog).
    /// </summary>
    [JsonPropertyName("cog")]
    public int? CourseOverGround { get; set; }

    /// <summary>
    /// Timestamp as Unix epoch (tst).
    /// </summary>
    [JsonPropertyName("tst")]
    public long? Timestamp { get; set; }

    /// <summary>
    /// Trigger for the location update (t): p=ping, c=circular region, b=beacon, r=report location, u=manual, t=timer, v=monitoring.
    /// </summary>
    [JsonPropertyName("t")]
    public string? Trigger { get; set; }

    /// <summary>
    /// Tracker ID (tid).
    /// </summary>
    [JsonPropertyName("tid")]
    public string? TrackerId { get; set; }

    /// <summary>
    /// Velocity/speed in km/h (vel).
    /// </summary>
    [JsonPropertyName("vel")]
    public int? Velocity { get; set; }

    /// <summary>
    /// Vertical accuracy in meters (vac).
    /// </summary>
    [JsonPropertyName("vac")]
    public int? VerticalAccuracy { get; set; }

    /// <summary>
    /// WiFi connection status (wifi).
    /// </summary>
    [JsonPropertyName("wifi")]
    public bool? Wifi { get; set; }

    /// <summary>
    /// SSID of connected WiFi (SSID) - iOS only.
    /// </summary>
    [JsonPropertyName("SSID")]
    public string? Ssid { get; set; }

    /// <summary>
    /// BSSID of connected WiFi (BSSID) - iOS only.
    /// </summary>
    [JsonPropertyName("BSSID")]
    public string? Bssid { get; set; }

    /// <summary>
    /// Monitoring mode (m): 0=quiet, 1=manual, 2=significant, 3=move.
    /// </summary>
    [JsonPropertyName("m")]
    public int? MonitoringMode { get; set; }

    /// <summary>
    /// Connection status (conn): w=WiFi, o=offline, m=mobile.
    /// </summary>
    [JsonPropertyName("conn")]
    public string? Connection { get; set; }

    /// <summary>
    /// In regions (inregions) - list of region names the device is currently in.
    /// </summary>
    [JsonPropertyName("inregions")]
    public List<string>? InRegions { get; set; }
}

[ApiController]
[Route("api/[controller]")]
public class LocationController : ControllerBase
{
    private readonly LocationService _locationService;
    private readonly ILogger<LocationController> _logger;

    public LocationController(LocationService locationService, ILogger<LocationController> logger)
    {
        _locationService = locationService;
        _logger = logger;
    }

    /// <summary>
    /// Receives location updates from OwnTracks.
    /// This endpoint accepts the OwnTracks JSON payload and caches the location
    /// for use by the DisplayController.
    /// </summary>
    /// <param name="payload">The OwnTracks payload containing location data</param>
    /// <returns>OwnTracks-compatible response</returns>
    [HttpPost("owntracks")]
    public async Task<ActionResult<object>> ReceiveOwnTracksUpdate([FromBody] OwnTracksPayload payload)
    {
        // OwnTracks sends different message types, we only care about location updates
        if (payload.Type != "location")
        {
            _logger.LogDebug("Received non-location OwnTracks message of type: {Type}", payload.Type);
            // Return empty array as per OwnTracks protocol for non-location messages
            return Ok(Array.Empty<object>());
        }

        if (!payload.Latitude.HasValue || !payload.Longitude.HasValue)
        {
            _logger.LogWarning("Received OwnTracks location update without coordinates");
            return BadRequest(new { error = "Missing latitude or longitude" });
        }

        try
        {
            // Create metadata from OwnTracks payload
            var metadata = new OwnTracksMetadata
            {
                BatteryLevel = payload.Battery,
                BatteryStatus = payload.BatteryStatus,
                Accuracy = payload.Accuracy,
                Altitude = payload.Altitude,
                Velocity = payload.Velocity,
                Connection = payload.Connection,
                TrackerId = payload.TrackerId,
                Timestamp = payload.Timestamp
            };

            var location = await _locationService.UpdateCachedLocationAsync(
                payload.Latitude.Value,
                payload.Longitude.Value,
                metadata);

            _logger.LogInformation(
                "Updated cached location: {HumanReadable} ({Lat}, {Lon}) from tracker {TrackerId}",
                location?.HumanReadable ?? "Unknown",
                payload.Latitude.Value,
                payload.Longitude.Value,
                payload.TrackerId ?? "unknown");

            // OwnTracks expects an array response (can contain commands to send back)
            // Empty array means no commands
            return Ok(Array.Empty<object>());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing OwnTracks location update");
            return StatusCode(500, new { error = "Failed to process location update" });
        }
    }

    /// <summary>
    /// Gets the currently cached location.
    /// </summary>
    [HttpGet("current")]
    public ActionResult<object> GetCurrentLocation()
    {
        var cachedLocation = _locationService.GetCachedLocation();
        var timestamp = _locationService.GetCachedLocationTimestamp();

        if (cachedLocation == null)
        {
            return NotFound(new { error = "No location cached. Send a location update first." });
        }

        return Ok(new
        {
            location = cachedLocation,
            cachedAt = timestamp,
            ageSeconds = (DateTime.UtcNow - timestamp).TotalSeconds
        });
    }
}
