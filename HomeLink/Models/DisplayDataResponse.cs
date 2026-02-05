namespace HomeLink.Models;

public class DisplayDataResponse
{
    public bool Success { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
    public DisplayMetadata Display { get; set; } = new();
    public SpotifyDisplayData? Spotify { get; set; }
    public LocationDisplayData? Location { get; set; }
}

public class DisplayMetadata
{
    public int Width { get; set; }
    public int Height { get; set; }
    public string Units { get; set; } = "px";
}

public class SpotifyDisplayData
{
    public bool IsPlaying { get; set; }
    public string StatusText { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string TitleDisplay { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string ArtistDisplay { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string AlbumDisplay { get; set; } = string.Empty;
    public long ProgressMs { get; set; }
    public long DurationMs { get; set; }
    public string ProgressText { get; set; } = string.Empty;
    public string AlbumArtUrl { get; set; } = string.Empty;
    public string SpotifyUri { get; set; } = string.Empty;
    public string ScannableCodeUrl { get; set; } = string.Empty;
}

public class LocationDisplayData
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string HumanReadable { get; set; } = string.Empty;
    public string LocationText { get; set; } = string.Empty;
    public string LocationDisplayText { get; set; } = string.Empty;
    public string CityCountryText { get; set; } = string.Empty;
    public string CoordinatesText { get; set; } = string.Empty;
    public string GoogleMapsUrl { get; set; } = string.Empty;
    public string QrCodeUrl { get; set; } = string.Empty;
    public KnownLocation? MatchedKnownLocation { get; set; }
    public double? KnownLocationDistanceMeters { get; set; }
    public string? KnownLocationDistanceText { get; set; }
    public DeviceStatusData DeviceStatus { get; set; } = new();
    public MapRenderData Map { get; set; } = new();
}

public class DeviceStatusData
{
    public int? BatteryLevel { get; set; }
    public int? BatteryStatus { get; set; }
    public int? Accuracy { get; set; }
    public int? Altitude { get; set; }
    public int? Velocity { get; set; }
    public string? Connection { get; set; }
    public string? TrackerId { get; set; }
    public long? Timestamp { get; set; }
    public string? BatteryPercentText { get; set; }
    public IReadOnlyList<string> StatusParts { get; set; } = Array.Empty<string>();
}

public class MapRenderData
{
    public int Zoom { get; set; }
    public int TileSize { get; set; }
    public string TileUrlTemplate { get; set; } = string.Empty;
}
