using System.Globalization;
using HomeLink.Models;
using HomeLink.Utils;

namespace HomeLink.Services;

public class DisplayDataService
{
    private const int MapZoom = 16;
    private const int MapTileSize = 256;
    private const string MapTileTemplate = "https://tile.openstreetmap.org/{z}/{x}/{y}.png";

    public DisplayDataResponse BuildDisplayData(SpotifyTrackInfo? spotifyData, LocationInfo? locationData, DisplayMetadata display)
    {
        return new DisplayDataResponse
        {
            Success = true,
            GeneratedAt = DateTimeOffset.UtcNow,
            Display = display,
            Spotify = BuildSpotifyData(spotifyData),
            Location = BuildLocationData(locationData)
        };
    }

    private SpotifyDisplayData? BuildSpotifyData(SpotifyTrackInfo? spotifyData)
    {
        if (spotifyData == null)
        {
            return null;
        }

        return new SpotifyDisplayData
        {
            IsPlaying = spotifyData.IsPlaying,
            StatusText = spotifyData.IsPlaying ? "PLAYING" : "PAUSED",
            Title = spotifyData.Title,
            TitleDisplay = TextUtils.TruncateText(spotifyData.Title, 30),
            Artist = spotifyData.Artist,
            ArtistDisplay = TextUtils.TruncateText(spotifyData.Artist, 35),
            Album = spotifyData.Album,
            AlbumDisplay = TextUtils.TruncateText(spotifyData.Album, 40),
            ProgressMs = spotifyData.ProgressMs,
            DurationMs = spotifyData.DurationMs,
            ProgressText = $"{TimeUtils.FormatTime(spotifyData.ProgressMs)} / {TimeUtils.FormatTime(spotifyData.DurationMs)}",
            AlbumArtUrl = spotifyData.AlbumCoverUrl,
            SpotifyUri = spotifyData.SpotifyUri,
            ScannableCodeUrl = spotifyData.ScannableCodeUrl
        };
    }

    private LocationDisplayData? BuildLocationData(LocationInfo? locationData)
    {
        if (locationData == null)
        {
            return null;
        }

        string locationDataDisplayName = !string.IsNullOrEmpty(locationData.DisplayName) ? locationData.DisplayName : "Unknown Location";
        string locationText = !string.IsNullOrEmpty(locationData.HumanReadable)
            ? locationData.HumanReadable
            : locationDataDisplayName;

        return new LocationDisplayData
        {
            Latitude = locationData.Latitude,
            Longitude = locationData.Longitude,
            DisplayName = locationData.DisplayName,
            HumanReadable = locationData.HumanReadable,
            LocationText = locationText,
            LocationDisplayText = TextUtils.TruncateText(locationText, 35),
            CityCountryText = TextUtils.BuildCityCountryString(locationData),
            CoordinatesText = $"GPS: {locationData.Latitude.ToString("F5", CultureInfo.InvariantCulture)}, {locationData.Longitude.ToString("F5", CultureInfo.InvariantCulture)}",
            GoogleMapsUrl = locationData.GoogleMapsUrl,
            QrCodeUrl = locationData.QrCodeUrl,
            MatchedKnownLocation = locationData.MatchedKnownLocation,
            KnownLocationDistanceMeters = BuildKnownLocationDistanceMeters(locationData),
            KnownLocationDistanceText = BuildKnownLocationDistanceText(locationData),
            DeviceStatus = BuildDeviceStatus(locationData),
            Map = new MapRenderData
            {
                Zoom = MapZoom,
                TileSize = MapTileSize,
                TileUrlTemplate = MapTileTemplate
            }
        };
    }

    private static DeviceStatusData BuildDeviceStatus(LocationInfo locationData)
    {
        List<string> statusParts = new();
        string? batteryText = null;

        if (locationData.BatteryLevel.HasValue)
        {
            batteryText = $"{locationData.BatteryLevel}%";
        }

        if (locationData.Accuracy.HasValue)
        {
            statusParts.Add($"±{locationData.Accuracy}m");
        }

        if (locationData.Velocity is > 0)
        {
            statusParts.Add($"{locationData.Velocity} km/h");
        }

        if (!string.IsNullOrEmpty(locationData.Connection))
        {
            statusParts.Add(locationData.Connection switch
            {
                "w" => "WiFi",
                "m" => "Mobile",
                "o" => "Offline",
                _ => locationData.Connection
            });
        }

        return new DeviceStatusData
        {
            BatteryLevel = locationData.BatteryLevel,
            BatteryStatus = locationData.BatteryStatus,
            Accuracy = locationData.Accuracy,
            Altitude = locationData.Altitude,
            Velocity = locationData.Velocity,
            Connection = locationData.Connection,
            TrackerId = locationData.TrackerId,
            Timestamp = locationData.Timestamp,
            BatteryPercentText = batteryText,
            StatusParts = statusParts
        };
    }

    private static double? BuildKnownLocationDistanceMeters(LocationInfo locationData)
    {
        if (locationData.MatchedKnownLocation == null)
        {
            return null;
        }

        return GeoUtils.CalculateDistance(
            locationData.Latitude,
            locationData.Longitude,
            locationData.MatchedKnownLocation.Latitude,
            locationData.MatchedKnownLocation.Longitude);
    }

    private static string? BuildKnownLocationDistanceText(LocationInfo locationData)
    {
        if (locationData.MatchedKnownLocation == null)
        {
            return null;
        }

        double distance = GeoUtils.CalculateDistance(
            locationData.Latitude,
            locationData.Longitude,
            locationData.MatchedKnownLocation.Latitude,
            locationData.MatchedKnownLocation.Longitude);

        return distance < 1000
            ? $"~{distance:F0}m from center"
            : $"~{distance / 1000:F1}km from center";
    }
}
