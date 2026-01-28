namespace HomeLink.Models;

public class LocationInfo
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string HumanReadable { get; set; } = string.Empty;
    public string? District { get; set; }
    public string? City { get; set; }
    public string? Town { get; set; }
    public string? Village { get; set; }
    public string? Country { get; set; }
    public KnownLocation? MatchedKnownLocation { get; set; }
    public string GoogleMapsUrl { get; set; } = string.Empty;
    public string QrCodeUrl { get; set; } = string.Empty;
    
    // OwnTracks metadata
    /// <summary>Battery level percentage (0-100)</summary>
    public int? BatteryLevel { get; set; }
    
    /// <summary>Battery status: 0=unknown, 1=unplugged, 2=charging, 3=full</summary>
    public int? BatteryStatus { get; set; }
    
    /// <summary>GPS accuracy in meters</summary>
    public int? Accuracy { get; set; }
    
    /// <summary>Altitude in meters above sea level</summary>
    public int? Altitude { get; set; }
    
    /// <summary>Velocity/speed in km/h</summary>
    public int? Velocity { get; set; }
    
    /// <summary>Connection type: w=WiFi, o=offline, m=mobile</summary>
    public string? Connection { get; set; }
    
    /// <summary>Tracker ID from OwnTracks</summary>
    public string? TrackerId { get; set; }
    
    /// <summary>Timestamp of when location was recorded (Unix epoch)</summary>
    public long? Timestamp { get; set; }
}