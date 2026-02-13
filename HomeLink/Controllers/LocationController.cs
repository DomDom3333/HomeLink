using Microsoft.AspNetCore.Mvc;
using HomeLink.Services;
using System.Diagnostics;
using System.Text.Json.Serialization;
using HomeLink.Models;
using HomeLink.Telemetry;

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
    private readonly TelemetryDashboardState _dashboardState;
    private readonly DisplayFrameCacheService _displayFrameCache;

    public LocationController(LocationService locationService, ILogger<LocationController> logger, TelemetryDashboardState dashboardState, DisplayFrameCacheService displayFrameCache)
    {
        _locationService = locationService;
        _logger = logger;
        _dashboardState = dashboardState;
        _displayFrameCache = displayFrameCache;
    }

    /// <summary>
    /// Receives location updates from OwnTracks.
    /// This endpoint accepts the OwnTracks JSON payload and caches the location
    /// for use by the DisplayController.
    /// </summary>
    /// <param name="payload">The OwnTracks payload containing location data</param>
    /// <returns>OwnTracks-compatible response</returns>
    [HttpPost("owntracks")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(object[]), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<object>> ReceiveOwnTracksUpdate([FromBody] OwnTracksPayload payload)
    {
        long startTimestamp = Stopwatch.GetTimestamp();
        bool isError = false;
        HomeLinkTelemetry.LocationUpdates.Add(1);

        using Activity? activity = HomeLinkTelemetry.ActivitySource.StartActivity("LocationController.ReceiveOwnTracksUpdate", ActivityKind.Server);
        activity?.SetTag("owntracks.type", payload.Type);
        activity?.SetTag("owntracks.tracker_id", payload.TrackerId);

        _logger.LogInformation("ReceiveOwnTracksUpdate request received. Type: {Type}, TrackerId: {TrackerId}", payload.Type, payload.TrackerId);

        // OwnTracks sends different message types, we only care about location updates
        if (payload.Type != "location")
        {
            _logger.LogDebug("Received non-location OwnTracks message of type: {Type}", payload.Type);
            // Return empty array as per OwnTracks protocol for non-location messages
            _logger.LogInformation("ReceiveOwnTracksUpdate returning empty response for non-location payload.");
            activity?.SetTag("http.response.status_code", 200);
            return Ok(Array.Empty<object>());
        }

        if (!payload.Latitude.HasValue || !payload.Longitude.HasValue)
        {
            _logger.LogWarning("Received OwnTracks location update without coordinates");
            isError = true;
            activity?.SetTag("error", true);
            activity?.SetTag("http.response.status_code", 400);
            return BadRequest(new ErrorResponse { Error = "Missing latitude or longitude" });
        }

        try
        {
            // Create metadata from OwnTracks payload
            OwnTracksMetadata metadata = new()
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

            LocationInfo? location = await _locationService.UpdateCachedLocationAsync(
                payload.Latitude.Value,
                payload.Longitude.Value,
                metadata);

            _logger.LogInformation(
                "Updated cached location: {HumanReadable} ({Lat}, {Lon}) from tracker {TrackerId}",
                location?.HumanReadable ?? "Unknown",
                payload.Latitude.Value,
                payload.Longitude.Value,
                payload.TrackerId ?? "unknown");
            _displayFrameCache.SignalRenderNeeded();

            // OwnTracks expects an array response (can contain commands to send back)
            // Empty array means no commands
            _logger.LogInformation("ReceiveOwnTracksUpdate processed successfully. Returning empty command array.");
            activity?.SetTag("http.response.status_code", 200);
            return Ok(Array.Empty<object>());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing OwnTracks location update");
            isError = true;
            activity?.SetTag("error", true);
            activity?.SetTag("http.response.status_code", 500);
            activity?.AddException(ex);
            return StatusCode(500, new ErrorResponse { Error = "Failed to process location update" });
        }
        finally
        {
            double elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            HomeLinkTelemetry.LocationLookupDurationMs.Record(elapsedMs);
            _dashboardState.RecordLocation(elapsedMs, isError);
            activity?.SetTag("location.update.duration_ms", elapsedMs);
        }
    }
}
