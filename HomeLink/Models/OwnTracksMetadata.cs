namespace HomeLink.Models;

/// <summary>
/// Metadata from OwnTracks payload to enrich location information
/// </summary>
public class OwnTracksMetadata
{
    public int? BatteryLevel { get; set; }
    public int? BatteryStatus { get; set; }
    public int? Accuracy { get; set; }
    public int? Altitude { get; set; }
    public int? Velocity { get; set; }
    public string? Connection { get; set; }
    public string? TrackerId { get; set; }
    public long? Timestamp { get; set; }
}